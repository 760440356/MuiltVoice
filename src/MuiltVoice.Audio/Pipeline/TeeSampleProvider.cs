// Copyright (c) 2026 wdd. Licensed under PolyForm Noncommercial 1.0.0.
using System.Threading;
using NAudio.Wave;

namespace MuiltVoice.Audio.Pipeline;

/// <summary>
/// 扇出（Tee）组件：作为 ISampleProvider 串联在混音输出与主输出之间。
/// 主输出的 WasapiOut.Read() 拉取本组件 → 拉动底层 mixer → 数据流过时
/// 把副本（字节）推入每个次级输出的 <see cref="BufferedWaveProvider"/>。
/// 这样多个输出设备能独立拉取各自的缓冲，互不竞争。
/// </summary>
/// <remarks>
/// 线程模型：Read 由主输出的渲染线程调用（单线程）；AddTap/RemoveTap
/// 可由 UI 线程调用，内部用锁保护 tap 列表并发布快照供 Read 无锁读取。
/// </remarks>
public sealed class TeeSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly List<BufferedWaveProvider> _taps = new();
    private readonly object _lock = new();
    private volatile BufferedWaveProvider[] _tapSnapshot = Array.Empty<BufferedWaveProvider>();
    private byte[] _byteBuffer = Array.Empty<byte>();

    public TeeSampleProvider(ISampleProvider source)
    {
        if (source.WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat)
        {
            throw new ArgumentException("TeeSampleProvider 要求 IEEE float 源格式。", nameof(source));
        }
        _source = source;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>当前次级输出（tap）数量。</summary>
    public int TapCount => _tapSnapshot.Length;

    /// <summary>
    /// 添加一个次级输出缓冲。tap 的格式必须与源一致（48kHz stereo float）。
    /// </summary>
    public void AddTap(BufferedWaveProvider tap)
    {
        ArgumentNullException.ThrowIfNull(tap);
        EnsureFormatMatch(tap.WaveFormat);
        lock (_lock)
        {
            _taps.Add(tap);
            _tapSnapshot = _taps.ToArray();
        }
    }

    /// <summary>移除一个次级输出缓冲。</summary>
    public void RemoveTap(BufferedWaveProvider tap)
    {
        ArgumentNullException.ThrowIfNull(tap);
        lock (_lock)
        {
            _taps.Remove(tap);
            _tapSnapshot = _taps.ToArray();
        }
    }

    /// <summary>清空所有 tap。</summary>
    public void ClearTaps()
    {
        lock (_lock)
        {
            _taps.Clear();
            _tapSnapshot = Array.Empty<BufferedWaveProvider>();
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (read <= 0)
        {
            return read;
        }

        var taps = _tapSnapshot;
        if (taps.Length == 0)
        {
            return read;
        }

        // float → byte 副本（IEEE float 的字节布局即 float 的内存表示）
        int byteCount = read * 4;
        if (_byteBuffer.Length < byteCount)
        {
            _byteBuffer = new byte[byteCount];
        }
        Buffer.BlockCopy(buffer, offset * 4, _byteBuffer, 0, byteCount);

        // BufferedWaveProvider.AddSamples 内部会拷贝，复用 _byteBuffer 安全
        foreach (var tap in taps)
        {
            try
            {
                tap.AddSamples(_byteBuffer, 0, byteCount);
            }
            catch
            {
                // 单个 tap 异常不影响整体
            }
        }
        return read;
    }

    private void EnsureFormatMatch(WaveFormat tapFormat)
    {
        var src = _source.WaveFormat;
        if (tapFormat.SampleRate != src.SampleRate
            || tapFormat.Channels != src.Channels
            || tapFormat.Encoding != src.Encoding)
        {
            throw new ArgumentException(
                $"tap 格式不匹配：期望 {src.SampleRate}Hz {src.Channels}ch {src.Encoding}，" +
                $"实际 {tapFormat.SampleRate}Hz {tapFormat.Channels}ch {tapFormat.Encoding}。");
        }
    }
}
