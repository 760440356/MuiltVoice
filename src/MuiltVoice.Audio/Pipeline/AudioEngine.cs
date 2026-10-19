// Copyright (c) 2026 wdd. Licensed under PolyForm Noncommercial 1.0.0.
using NAudio.CoreAudioApi;
using NAudio.Wave;
using MuiltVoice.Audio.Devices;
using MuiltVoice.Audio.Formats;
using MuiltVoice.Config.Models;
using Serilog;

namespace MuiltVoice.Audio.Pipeline;

/// <summary>
/// 多输入混音 + 多输出扇出 引擎：
///   N 路输入 → 每路独立 BufferedWaveProvider → 格式统一 → AudioMixer 混音
///     → TeeSampleProvider 扇出 → M 路输出（主输出 pull 链 + 从输出独立缓冲）
///
/// 线程模型：
///   - 每路 InputChannel 的 WASAPI 回调线程写各自 BufferedWaveProvider（线程安全）
///   - 主 OutputChannel 的 pull 线程读混音 → 驱动 TeeSampleProvider → 写每个从输出的 Buffer
///     （被写的 Buffer 是独立 BufferedWaveProvider，避免冲突）
///   - 从输出的 WASAPI 线程独立拉自己的 Buffer
///
/// 使用方式：StartWithRouting(...) 一次配置并启动；Stop() 停止并释放所有通道。
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private readonly List<InputChannel> _inputs = new();
    private readonly List<OutputChannel> _outputs = new();
    private readonly ILogger _log = AppLog.Log.ForContext<AudioEngine>();
    private AudioMixer? _mixer;
    private TeeSampleProvider? _tee;
    private bool _running;
    private bool _disposed;

    public event EventHandler<bool>? RunningChanged;
    public bool IsRunning => _running;
    public IReadOnlyList<InputChannel> Inputs => _inputs;
    public IReadOnlyList<OutputChannel> Outputs => _outputs;
    public AudioMixer? Mixer => _mixer;
    public TeeSampleProvider? Tee => _tee;

    /// <summary>单输入直通：1 入 → 1 出。Demo 便捷方法。</summary>
    public void StartPassthrough(
        AudioDeviceEnumerator enumerator, string inputId, bool loopback, string outputId,
        AudioSettings? settings = null)
    {
        var s = settings ?? new AudioSettings();
        _log.Debug("StartPassthrough 便捷方法：Loopback={LB} InId={I} OutId={O}",
            loopback, inputId, outputId);
        var inputs = new[]
        {
            new InputSpec { DeviceId = inputId, Loopback = loopback },
        };
        var outputs = new[]
        {
            new OutputSpec { DeviceId = outputId, IsMaster = true },
        };
        StartWithRouting(enumerator, inputs, outputs, s);
    }

    /// <summary>多输入混音：N 入 → 1 出。Demo 便捷方法。</summary>
    public void StartMixing(
        AudioDeviceEnumerator enumerator, IReadOnlyList<InputSpec> inputs, string outputId,
        AudioSettings? settings = null)
    {
        var s = settings ?? new AudioSettings();
        _log.Debug("StartMixing 便捷方法：输入数={N} OutId={O}", inputs.Count, outputId);
        var outputs = new[]
        {
            new OutputSpec { DeviceId = outputId, IsMaster = true },
        };
        StartWithRouting(enumerator, inputs, outputs, s);
    }

    /// <summary>配置并启动路由链路；重复调用会先 Stop。settings 为空则用 Balanced 默认。</summary>
    public void StartWithRouting(
        AudioDeviceEnumerator enumerator,
        IReadOnlyList<InputSpec> inputs,
        IReadOnlyList<OutputSpec> outputs,
        AudioSettings? settings = null)
    {
        var s = settings ?? new AudioSettings();
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(enumerator);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(outputs);

        Stop();

        if (inputs.Count == 0)
        {
            _log.Warning("StartWithRouting 未配置输入，拒绝启动");
            throw new InvalidOperationException("请至少选择一个输入设备。");
        }
        if (outputs.Count == 0)
        {
            _log.Warning("StartWithRouting 未配置输出，拒绝启动");
            throw new InvalidOperationException("请至少选择一个输出设备。");
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Exception? cleanupErr = null;
        try
        {
            _log.Information(
                "========== 启动路由 ========== 输入{Nin}个，输出{Nout}个，统一格式 {Fmt}，预设={Pr}",
                inputs.Count, outputs.Count,
                $"{AudioConstants.UnifiedFormat.SampleRate}Hz/{AudioConstants.UnifiedFormat.Channels}ch/{AudioConstants.UnifiedFormat.BitsPerSample}bit-float",
                s.Preset);

            // --- 1. 打开所有输入设备 ---
            for (int i = 0; i < inputs.Count; i++)
            {
                var spec = inputs[i];
                _log.Debug("打开输入 #{i}：Loopback={LB} DeviceId={Id} Name={Name}",
                    i + 1, spec.Loopback, spec.DeviceId,
                    string.IsNullOrEmpty(spec.DisplayName) ? "(无)" : spec.DisplayName);

                var mmDevice = enumerator.GetDeviceById(spec.DeviceId);
                if (mmDevice is null)
                    throw new InvalidOperationException(
                        $"找不到输入设备，Id={spec.DeviceId}（可能已被拔出）。");

                InputChannel ch = spec.Loopback
                    ? InputChannel.CreateLoopback(mmDevice, s)
                    : InputChannel.CreateCapture(mmDevice, s);
                ch.Volume = spec.Volume;
                ch.IsMuted = spec.Muted;
                _inputs.Add(ch);
            }

            // --- 2. 混音器 + 扇出 ---
            _mixer = new AudioMixer();
            foreach (var ch in _inputs)
                _mixer.AddInput(ch.SampleProvider);
            _tee = new TeeSampleProvider(_mixer.Output);
            _log.Debug("混音器 & TeeSampleProvider 构造完成，输入数={N}", _mixer.InputCount);

            // 按 IsMaster=true 找主输出；找不到就取第 1 个
            int masterIdx = 0;
            for (int i = 0; i < outputs.Count; i++)
            {
                if (outputs[i].IsMaster) { masterIdx = i; break; }
            }
            var masterSpec = outputs[masterIdx];
            var masterMmDevice = enumerator.GetDeviceById(masterSpec.DeviceId);
            if (masterMmDevice is null)
                throw new InvalidOperationException($"找不到主输出设备，Id={masterSpec.DeviceId}。");
            _log.Debug("创建主输出：Index={I} Id={Id} Name={N} Exclusive={Ex}",
                masterIdx, masterSpec.DeviceId, masterSpec.DisplayName, masterSpec.UseExclusiveMode);
            var master = OutputChannel.CreateMaster(masterMmDevice, _tee, s, masterSpec.UseExclusiveMode);
            master.Volume = masterSpec.Volume;
            master.IsMuted = masterSpec.Muted;
            _outputs.Add(master);

            // --- 3. 从输出（TeeSampleProvider 扇出）按序 ---
            for (int i = 0; i < outputs.Count; i++)
            {
                if (i == masterIdx) continue;
                var spec = outputs[i];
                _log.Debug("创建从输出：Index={I} Id={Id} Name={N} Exclusive={Ex}",
                    i, spec.DeviceId, spec.DisplayName, spec.UseExclusiveMode);
                var mmDevice = enumerator.GetDeviceById(spec.DeviceId);
                if (mmDevice is null)
                {
                    _log.Warning("找不到从输出设备 Id={Id}，跳过", spec.DeviceId);
                    continue;
                }
                var slave = OutputChannel.CreateSlave(mmDevice, AudioConstants.UnifiedFormat, s, spec.UseExclusiveMode);
                slave.Volume = spec.Volume;
                slave.IsMuted = spec.Muted;
                _tee.AddTap(slave.Buffer!);
                _outputs.Add(slave);
            }

            // --- 4. 一次性全部启动（先输出生成 pull 链，再启动输入） ---
            _log.Debug("启动所有输出端 ...");
            foreach (var o in _outputs)
                o.Play();

            _log.Debug("启动所有输入端 ...");
            foreach (var ch in _inputs)
                ch.StartRecording();

            _running = true;
            sw.Stop();
            _log.Information(
                "✅ 路由已启动：{Nin}入→{Nout}出，耗时{ElapsedMs}ms，统一格式 {Fmt}",
                _inputs.Count, _outputs.Count, sw.ElapsedMilliseconds,
                $"{AudioConstants.UnifiedFormat.SampleRate}Hz/{AudioConstants.UnifiedFormat.Channels}ch");
            try { RunningChanged?.Invoke(this, true); }
            catch (Exception ex) { _log.Warning(ex, "RunningChanged(true) 回调异常"); }
        }
        catch (Exception ex)
        {
            cleanupErr = ex;
            _log.Error(ex,
                "❌ 启动路由失败（输入{Nin}个/输出{Nout}个），耗时{ElapsedMs}ms，正在回滚...",
                inputs.Count, outputs.Count, sw.ElapsedMilliseconds);
            throw;
        }
        finally
        {
            if (cleanupErr is not null)
            {
                try { Stop(); }
                catch (Exception ce) { _log.Warning(ce, "启动失败后的回滚 Stop 异常"); }
            }
        }
    }

    public void Stop()
    {
        if (_disposed) return;
        if (!_running && _inputs.Count == 0) return;
        _log.Information("Stop() 调用，当前运行={Running}，输入数={Nin}，输出数={Nout}",
            _running, _inputs.Count, _outputs.Count);

        // 先停采集，再停渲染（避免 pull 链瞬间空转爆音）
        try
        {
            foreach (var ch in _inputs)
                try { ch.StopRecording(); } catch { }
        }
        catch (Exception ex) { _log.Warning(ex, "停止输入端异常"); }

        try
        {
            foreach (var o in _outputs)
                try { o.Stop(); } catch { }
        }
        catch (Exception ex) { _log.Warning(ex, "停止输出端异常"); }

        DisposeChannels();

        bool wasRunning = _running;
        _running = false;
        if (wasRunning)
        {
            try { RunningChanged?.Invoke(this, false); }
            catch (Exception ex) { _log.Warning(ex, "RunningChanged(false) 回调异常"); }
        }
        _log.Information("已停止，所有通道已释放");
    }

    private void DisposeChannels()
    {
        foreach (var ch in _inputs)
            try { ch.Dispose(); } catch { }
        foreach (var o in _outputs)
            try { o.Dispose(); } catch { }
        try { (_mixer as IDisposable)?.Dispose(); } catch { }
        _inputs.Clear();
        _outputs.Clear();
        _mixer = null;
        _tee = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _log.Debug("Dispose() 开始");
        try { Stop(); } catch { }
        _disposed = true;
        _log.Debug("Dispose() 完成");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AudioEngine));
    }
}
