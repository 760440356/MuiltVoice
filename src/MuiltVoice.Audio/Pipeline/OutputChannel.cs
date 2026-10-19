// Copyright (c) 2026 wdd. Licensed under PolyForm Noncommercial 1.0.0.
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using MuiltVoice.Audio.Formats;
using MuiltVoice.Config.Models;
using Serilog;

namespace MuiltVoice.Audio.Pipeline;

/// <summary>
/// 单路输出通道：把一个 ISampleProvider（48kHz stereo float）
/// 通过 WASAPI 共享模式渲染到指定设备。每个输出独立持有 WasapiOut。
/// 多输出场景下，主输出直接绑混音流（驱动 pull 链），次级输出绑各自的
/// BufferedWaveProvider（由 TeeSampleProvider 推入数据）。
///
/// WASAPI 初始化兼容策略（应对 0x8889000A / AUDCLNT_E_INVALID_SIZE 等驱动不兼容）：
///   1. 先用 useEventSync=true + RenderLatencyMs（20ms，低延迟）
///   2. 失败 → useEventSync=false + RenderLatencyMs（20ms）
///   3. 失败 → useEventSync=false + RenderLatencyFallbackMs（50ms，兼容性兜底）
///   4. 都失败 → 把最后一次异常抛出
/// </summary>
public sealed class OutputChannel : IDisposable
{
    private readonly WasapiOut _player;
    private readonly VolumeSampleProvider _volumeStage;
    private readonly ISampleProvider _source;
    private readonly BufferedWaveProvider? _localBuffer;
    private readonly ILogger _log;
    private float _volumeScalar = 1f;
    private bool _muted;
    private bool _disposed;

    /// <summary>渲染异常/停止时触发。</summary>
    public event EventHandler<Exception?>? PlaybackStopped;

    public string DeviceId { get; }
    public string Name { get; }

    /// <summary>是否为主输出（直接拉取源，驱动整条 pull 链）。</summary>
    public bool IsMaster { get; }

    public float Volume
    {
        get => _volumeScalar;
        set
        {
            _volumeScalar = Math.Clamp(value, 0f, 4f);
            ApplyVolume();
        }
    }

    public bool IsMuted
    {
        get => _muted;
        set
        {
            _muted = value;
            ApplyVolume();
        }
    }

    public PlaybackState PlaybackState => _player.PlaybackState;

    /// <summary> BufferedBytes（次级输出用），主输出返回 0。供 UI 监控堆积。</summary>
    public long BufferedBytes => _localBuffer?.BufferedBytes ?? 0;

    /// <summary>
    /// 次级输出内部的 <see cref="BufferedWaveProvider"/>，供
    /// <see cref="TeeSampleProvider"/> 推入数据。主输出返回 null。
    /// </summary>
    public BufferedWaveProvider? Buffer => _localBuffer;

    /// <summary>
    /// 构造输出通道。主输出直接绑 source（驱动整条 pull 链）；
    /// 次级输出传入 <paramref name="localBuffer"/>，外部通过 <see cref="AddSamples"/> 推入数据。
    /// </summary>
    private OutputChannel(MMDevice device, ISampleProvider source, bool isMaster, BufferedWaveProvider? localBuffer, AudioSettings settings, bool useExclusive)
    {
        DeviceId = device.ID;
        Name = SafeRead(() => device.FriendlyName) ?? "<unknown>";
        IsMaster = isMaster;
        _localBuffer = localBuffer;

        _log = AppLog.Log.ForContext<OutputChannel>()
            .ForContext("Device", Name)
            .ForContext("IsMaster", isMaster)
            .ForContext("Exclusive", useExclusive);

        _volumeStage = new VolumeSampleProvider(source);
        _source = _volumeStage;
        _player = CreateWasapiOutWithFallback(device, _source.ToWaveProvider(), settings, useExclusive);
        _player.PlaybackStopped += OnPlaybackStopped;
        ApplyVolume();
        _log.Debug("OutputChannel 构造成功，Role={Role}, Exclusive={Ex}, LatencyHint={Lat}ms / Fallback={Fb}ms",
            isMaster ? "Master" : "Slave", useExclusive, settings.RenderLatencyMs, settings.RenderLatencyFallbackMs);
    }

    /// <summary>
/// 构造 WasapiOut 并 Init：按回退策略尝试，最终把最后一次错误抛出。
/// 若 <paramref name="useExclusive"/>=true，则先走 Exclusive（独占）3 级策略，
/// 失败再回退到 Shared（共享）3 级策略，保证尽量启动成功。
/// 独占模式：Windows Audio Engine 不插手，延迟可降到 ~10-30ms，但设备被独占，
/// 其他程序无法使用，且必须是硬件支持的格式（我们用统一 48kHz float stereo，多数设备支持）。
/// </summary>
private static WasapiOut CreateWasapiOutWithFallback(MMDevice device, IWaveProvider sourceAsWave, AudioSettings s, bool useExclusive)
{
    var devName = SafeRead(() => device.FriendlyName) ?? "<unknown>";
    var log = AppLog.Log.ForContext("Where", "OutputChannel.WasapiInit").ForContext("Device", devName);
    Exception? last = null;

    var lat = Math.Clamp(s.RenderLatencyMs, 1, 2000);
    var fb = Math.Clamp(s.RenderLatencyFallbackMs, lat, 5000);

    // ======================================================
    // 独占模式（方案 C）：先走，若任何一级失败 → 回退共享模式
    // ======================================================
    if (useExclusive)
    {
        try
        {
            log.Debug("策略 E1：Exclusive + EventSync + Latency={L}ms", lat);
            var p = new WasapiOut(device, AudioClientShareMode.Exclusive,
                useEventSync: true, latency: lat);
            p.Init(sourceAsWave);
            log.Information("✅ 方案C 策略E1 成功（Exclusive + EventSync + {L}ms）", lat);
            return p;
        }
        catch (Exception ex)
        {
            last = ex;
            log.Warning(ex, "策略 E1 失败，HR={Hr}，回退策略 E2", ex.HResult.ToString("X8"));
        }

        try
        {
            log.Debug("策略 E2：Exclusive + Pull + Latency={L}ms", lat);
            var p = new WasapiOut(device, AudioClientShareMode.Exclusive,
                useEventSync: false, latency: lat);
            p.Init(sourceAsWave);
            log.Information("✅ 方案C 策略E2 成功（Exclusive + Pull + {L}ms）", lat);
            return p;
        }
        catch (Exception ex)
        {
            last = ex;
            log.Warning(ex, "策略 E2 失败，HR={Hr}，回退策略 E3", ex.HResult.ToString("X8"));
        }

        try
        {
            log.Debug("策略 E3：Exclusive + Pull + Latency={L}ms", fb);
            var p = new WasapiOut(device, AudioClientShareMode.Exclusive,
                useEventSync: false, latency: fb);
            p.Init(sourceAsWave);
            log.Information("✅ 方案C 策略E3 成功（Exclusive + Pull + {L}ms）", fb);
            return p;
        }
        catch (Exception ex)
        {
            last = ex;
            log.Warning(ex, "策略 E3 失败，HR={Hr}，设备可能不支持独占或被系统组策略禁用，准备回退共享模式", ex.HResult.ToString("X8"));
        }
    }

    // ======================================================
    // 共享模式（方案 A/B）：3 级回退
    // ======================================================
    // 策略 1：事件驱动 + 目标 latency（AudioSettings.RenderLatencyMs）
    try
    {
        log.Debug("策略1：Shared + EventSync + Latency={L}ms", lat);
        var p = new WasapiOut(device, AudioClientShareMode.Shared,
            useEventSync: true, latency: lat);
        p.Init(sourceAsWave);
        log.Information("策略1 成功（EventSync + {L}ms）", lat);
        return p;
    }
    catch (Exception ex)
    {
        last = ex;
        log.Warning(ex, "策略1 失败，HR={Hr}", ex.HResult.ToString("X8"));
    }

    // 策略 2：Pull 模式 + 同 latency（兼容性更高）
    try
    {
        log.Debug("策略2：Shared + Pull + Latency={L}ms", lat);
        var p = new WasapiOut(device, AudioClientShareMode.Shared,
            useEventSync: false, latency: lat);
        p.Init(sourceAsWave);
        log.Information("策略2 成功（Pull + {L}ms）", lat);
        return p;
    }
    catch (Exception ex)
    {
        last = ex;
        log.Warning(ex, "策略2 失败，HR={Hr}", ex.HResult.ToString("X8"));
    }

    // 策略 3：Pull 模式 + 兼容性 latency（AudioSettings.RenderLatencyFallbackMs，应对 0x8889000A）
    try
    {
        log.Debug("策略3：Shared + Pull + Latency={L}ms", fb);
        var p = new WasapiOut(device, AudioClientShareMode.Shared,
            useEventSync: false, latency: fb);
        p.Init(sourceAsWave);
        log.Information("策略3 成功（Pull + {L}ms）", fb);
        return p;
    }
    catch (Exception ex)
    {
        last = ex;
        log.Error(ex, "策略3 失败，HR={Hr}，所有回退都无效", ex.HResult.ToString("X8"));
    }

    throw new InvalidOperationException(
        $"输出设备 [{devName}] WASAPI 初始化失败，已尝试全部策略（UseExclusiveMode={useExclusive}）。" +
        $"最后错误：HRESULT 0x{last?.HResult:X8} — {last?.Message}，" +
        $"可能原因：①设备被其他程序独占，②驱动不支持 48kHz 32-bit float 共享/独占格式，" +
        $"③系统音频服务异常（重启 Windows Audio 可能有效）。", last);
}

    /// <summary>构造主输出：直接拉取 <paramref name="source"/>。useExclusive 由 OutputSpec 传入（设备粒度）。</summary>
    public static OutputChannel CreateMaster(MMDevice device, ISampleProvider source, AudioSettings? settings = null, bool useExclusive = false)
    {
        var s = settings ?? new AudioSettings();
        AppLog.Log.Information("CreateMaster 输出：{Device}, Exclusive={Ex}, RenderLatency={L}ms Fallback={F}ms",
            SafeRead(() => device.FriendlyName) ?? "<unknown>", useExclusive, s.RenderLatencyMs, s.RenderLatencyFallbackMs);
        return new OutputChannel(device, source, isMaster: true, localBuffer: null, settings: s, useExclusive: useExclusive);
    }

    /// <summary>
    /// 构造次级输出：内部建一个 BufferedWaveProvider，外部通过 <see cref="AddSamples"/>
    /// 推入数据，WasapiOut 独立拉取。用于多输出扇出。
    /// 缓冲时长由 AudioSettings.TapBufferMs 决定。useExclusive 由 OutputSpec 传入（设备粒度）。
    /// </summary>
    public static OutputChannel CreateSlave(MMDevice device, WaveFormat format, AudioSettings? settings = null, bool useExclusive = false)
    {
        var s = settings ?? new AudioSettings();
        var name = SafeRead(() => device.FriendlyName) ?? "<unknown>";
        var tapMs = Math.Clamp(s.TapBufferMs, 10, 5000);
        AppLog.Log.Information("CreateSlave 输出：{Device}, Exclusive={Ex}, Format={F} {SR}Hz, TapBuffer={Buf}ms",
            name, useExclusive, format.Encoding, format.SampleRate, tapMs);
        var buffer = new BufferedWaveProvider(format)
        {
            ReadFully = true,
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromMilliseconds(tapMs),
        };
        return new OutputChannel(device, buffer.ToSampleProvider(), isMaster: false, localBuffer: buffer, settings: s, useExclusive: useExclusive);
    }

    /// <summary>次级输出推入数据。主输出调用会抛异常。</summary>
    public void AddSamples(byte[] buffer, int offset, int count)
    {
        if (IsMaster)
        {
            throw new InvalidOperationException("主输出不支持 AddSamples，由源直接驱动。");
        }
        if (_localBuffer is null)
        {
            return;
        }
        try
        {
            _localBuffer.AddSamples(buffer, offset, count);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "AddSamples 异常（丢弃），count={Count}", count);
        }
    }

    public void Play()
    {
        ThrowIfDisposed();
        _log.Debug("Play() 开始渲染");
        _player.Play();
    }

    public void Stop()
    {
        _log.Debug("Stop() 停止渲染，State={State}", _player.PlaybackState);
        try { _player.Stop(); }
        catch (Exception ex) { _log.Warning(ex, "停止渲染异常"); }
    }

    private void ApplyVolume()
    {
        _volumeStage.Volume = _muted ? 0f : _volumeScalar;
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
            _log.Error(e.Exception, "PlaybackStopped（异常）");
        else
            _log.Debug("PlaybackStopped（正常）");
        PlaybackStopped?.Invoke(this, e.Exception);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _log.Debug("Dispose 开始");
        try { _player.PlaybackStopped -= OnPlaybackStopped; } catch { }
        try { _player.Stop(); } catch { }
        try { _player.Dispose(); } catch { }
        _log.Debug("Dispose 完成");
    }

    private static string? SafeRead(Func<string?> read)
    {
        try { return read(); }
        catch { return null; }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(OutputChannel));
        }
    }
}
