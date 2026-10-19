// Copyright (c) 2026 wdd. Licensed under PolyForm Noncommercial 1.0.0.
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using MuiltVoice.Audio.Formats;
using MuiltVoice.Audio.Meters;
using MuiltVoice.Config.Models;
using Serilog;

namespace MuiltVoice.Audio.Pipeline;

/// <summary>
/// 单路输入通道：采集 WASAPI capture/loopback → 独立 BufferedWaveProvider → 混音输入。
///
/// 每路输入独立缓冲（BufferedWaveProvider），规避 WASAPI 多 capture 回调线程
/// 并发写入同一缓冲导致的数据损毁。
///
/// WASAPI 采集初始化兼容策略（应对 0x8889000A / AUDCLNT_E_INVALID_SIZE）：
///   1. 先按 NAudio 默认（未指定 WaveFormat + EventSync）
///   2. 异常则用默认 WaveFormat + Pull
///   3. 都失败把最后异常抛出
///
/// 缓冲时长由调用方传入的 AudioSettings.InputBufferMs 决定（每路由独立可配）。
/// </summary>
public sealed class InputChannel : IDisposable
{
    private readonly IWaveIn _capture;
    private readonly BufferedWaveProvider _buffer;
    private readonly WaveToSampleProvider _sample;
    private readonly LevelMeter _meter;
    private readonly VolumeSampleProvider _volumeStage;
    private readonly ILogger _log;
    private readonly string _role; // Capture | Loopback
    private bool _disposed;

    public bool IsLoopback { get; }
    public string DeviceId { get; }
    public string Name { get; }

    /// <summary>采集格式（设备原始格式）。</summary>
    public WaveFormat CaptureFormat => _capture.WaveFormat;

    /// <summary>最终提供给混音端的格式（48kHz stereo float）。</summary>
    public WaveFormat OutputFormat => AudioConstants.UnifiedFormat;

    /// <summary>采样端（FormatNormalizer → 对外），直接挂到混音器。</summary>
    public ISampleProvider SampleProvider => _volumeStage;

    /// <summary>电平表（0..1），UI 用。</summary>
    public LevelMeter Meter => _meter;

    /// <summary>当前峰值（0..1），UI/日志便捷访问。</summary>
    public float PeakLevel => _meter.Peak;

    /// <summary>当前缓冲中待混音的字节数，UI 监控用。</summary>
    public long BufferedBytes => _buffer.BufferedBytes;

    public float Volume
    {
        get => _volumeStage.Volume;
        set => _volumeStage.Volume = Math.Clamp(value, 0f, 4f);
    }

    public bool IsMuted { get; set; }

    private InputChannel(
        IWaveIn capture,
        MMDevice device,
        bool isLoopback,
        AudioSettings settings)
    {
        _capture = capture;
        IsLoopback = isLoopback;
        DeviceId = device.ID;
        Name = SafeRead(() => device.FriendlyName) ?? "<unknown>";
        _role = isLoopback ? "Loopback" : "Capture";
        _log = AppLog.Log.ForContext<InputChannel>()
            .ForContext("Device", Name)
            .ForContext("Role", _role);

        var inputBufMs = Math.Clamp(settings?.InputBufferMs ?? AudioConstants.InputBufferDuration.Milliseconds, 10, 5000);
        _buffer = new BufferedWaveProvider(capture.WaveFormat)
        {
            ReadFully = true,
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromMilliseconds(inputBufMs),
        };
        _sample = new WaveToSampleProvider(_buffer);
        _meter = new LevelMeter(_sample);
        // Meter → Normalize(静态方法直接调) → Volume → 对外 SampleProvider
        var normalized = FormatNormalizer.NormalizeToUnified(_meter);
        _volumeStage = new VolumeSampleProvider(normalized);

        capture.DataAvailable += OnDataAvailable;
        capture.RecordingStopped += OnRecordingStopped;

        _log.Information(
            "InputChannel 构造：CapFmt={CF} {SR}Hz {Ch}ch → 统一 {OF}，InputBuffer={Buf}ms",
            capture.WaveFormat.Encoding, capture.WaveFormat.SampleRate, capture.WaveFormat.Channels,
            $"{AudioConstants.UnifiedFormat.SampleRate}Hz/{AudioConstants.UnifiedFormat.Channels}ch",
            inputBufMs);
    }

    /// <summary>
    /// 构造普通采集麦克风：按回退策略初始化 WasapiCapture。
    /// </summary>
    public static InputChannel CreateCapture(MMDevice device, AudioSettings? settings = null)
    {
        var s = settings ?? new AudioSettings();
        var log = AppLog.Log.ForContext("Where", "InputChannel.CreateCapture")
            .ForContext("Device", SafeRead(() => device.FriendlyName) ?? "<unknown>");
        Exception? last = null;

        // 策略1：默认参数（使用设备共享模式的默认格式）
        try
        {
            log.Debug("策略1：WasapiCapture 默认参数");
            var cap = new WasapiCapture(device);
            return BuildCapture(cap, device, isLoopback: false, "策略1（默认）", log, s);
        }
        catch (Exception ex) { last = ex; log.Warning(ex, "策略1 失败，HR={Hr}", ex.HResult.ToString("X8")); }

        log.Error(last, "所有初始化策略失败");
        throw new InvalidOperationException(
            $"采集设备 [{SafeRead(() => device.FriendlyName)}] WASAPI 初始化失败。" +
            $"最后错误：HRESULT 0x{last?.HResult:X8} — {last?.Message}。" +
            $"若设备被其他程序独占请先关闭，或尝试重启 Windows Audio 服务。", last);
    }

    /// <summary>
    /// 构造扬声器回环（采集系统声音）：按回退策略初始化 WasapiLoopbackCapture。
    /// </summary>
    public static InputChannel CreateLoopback(MMDevice device, AudioSettings? settings = null)
    {
        var s = settings ?? new AudioSettings();
        var log = AppLog.Log.ForContext("Where", "InputChannel.CreateLoopback")
            .ForContext("Device", SafeRead(() => device.FriendlyName) ?? "<unknown>");
        Exception? last = null;

        try
        {
            log.Debug("策略1：WasapiLoopbackCapture 默认参数");
            var cap = new WasapiLoopbackCapture(device);
            return BuildCapture(cap, device, isLoopback: true, "策略1（默认）", log, s);
        }
        catch (Exception ex) { last = ex; log.Warning(ex, "策略1 失败，HR={Hr}", ex.HResult.ToString("X8")); }

        log.Error(last, "所有初始化策略失败");
        throw new InvalidOperationException(
            $"系统声音回环 [{SafeRead(() => device.FriendlyName)}] WASAPI 初始化失败。" +
            $"最后错误：HRESULT 0x{last?.HResult:X8} — {last?.Message}。" +
            "如果是\"禁用的音频增强\"或\"驱动独占模式\"问题，请在声音属性→高级中取消\"允许应用独占控制此设备\"。", last);
    }

    private static InputChannel BuildCapture(
        IWaveIn cap, MMDevice device, bool isLoopback, string which, ILogger log, AudioSettings s)
    {
        var fmt = cap.WaveFormat;
        log.Information("{W} 成功，CaptureFormat={Enc} {SR}Hz {Ch}ch",
            which, fmt.Encoding, fmt.SampleRate, fmt.Channels);
        return new InputChannel(cap, device, isLoopback, s);
    }

    public void StartRecording()
    {
        ThrowIfDisposed();
        _log.Debug("StartRecording() 开始");
        _capture.StartRecording();
        _log.Information("StartRecording() 成功，fmt={Fmt}",
            $"{_capture.WaveFormat.SampleRate}Hz/{_capture.WaveFormat.Channels}ch");
    }

    public void StopRecording()
    {
        _log.Debug("StopRecording() 调用");
        try { _capture.StopRecording(); }
        catch (Exception ex) { _log.Warning(ex, "StopRecording() 异常"); }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_disposed) return;
        // 静音：直接把数据清零再进缓冲（不丢帧，保留时间推进）
        if (IsMuted)
        {
            for (int i = 0; i < e.BytesRecorded; i++)
                e.Buffer[i] = 0;
        }
        try
        {
            _buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
        }
        catch (Exception ex)
        {
            // 缓冲异常：丢弃（不影响采集继续）
            _log.Warning(ex, "BufferedWaveProvider.AddSamples 异常，丢弃 {N} 字节", e.BytesRecorded);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
            _log.Error(e.Exception, "RecordingStopped（采集异常终止）");
        else
            _log.Debug("RecordingStopped（正常停止）");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _log.Debug("Dispose 开始");
        try { _capture.DataAvailable -= OnDataAvailable; } catch { }
        try { _capture.RecordingStopped -= OnRecordingStopped; } catch { }
        try { _capture.StopRecording(); } catch { }
        try { _capture.Dispose(); } catch (Exception ex) { _log.Warning(ex, "capture.Dispose 异常"); }
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
            throw new ObjectDisposedException(nameof(InputChannel));
    }
}
