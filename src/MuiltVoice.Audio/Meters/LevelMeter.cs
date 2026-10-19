// Copyright (c) 2026 wdd. Licensed under PolyForm Noncommercial 1.0.0.
using NAudio.Wave;

namespace MuiltVoice.Audio.Meters;

/// <summary>
/// 峰值电平表：作为 ISampleProvider 串联在音频链中，在 pull 线程上
/// 实时计算峰值并做峰值保持 + 慢衰减，供 UI VU 表读取。
/// 仅做测量，不修改样本。
/// </summary>
public sealed class LevelMeter : ISampleProvider
{
    private readonly ISampleProvider _source;
    private volatile float _peak;
    private int _samplesSincePeak;

    public LevelMeter(ISampleProvider source)
    {
        _source = source;
    }

    /// <summary>当前峰值（0..1+），线程安全读取。</summary>
    public float Peak => _peak;

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (read <= 0)
        {
            return read;
        }

        float max = 0f;
        int end = offset + read;
        for (int i = offset; i < end; i++)
        {
            float a = Math.Abs(buffer[i]);
            if (a > max)
            {
                max = a;
            }
        }

        // 峰值保持 + 慢衰减：新峰值立即更新；无更高峰值时约每 50ms 衰减
        if (max >= _peak)
        {
            _peak = max;
            _samplesSincePeak = 0;
        }
        else
        {
            _samplesSincePeak += read;
            // 50ms ≈ 48000 * 2 / 1000 * 50 = 4800 样本
            if (_samplesSincePeak > 4800)
            {
                _peak *= 0.85f;
                _samplesSincePeak = 0;
                if (_peak < 0.0005f)
                {
                    _peak = 0f;
                }
            }
        }
        return read;
    }

    /// <summary>手动复位峰值。</summary>
    public void Reset()
    {
        _peak = 0f;
        _samplesSincePeak = 0;
    }
}
