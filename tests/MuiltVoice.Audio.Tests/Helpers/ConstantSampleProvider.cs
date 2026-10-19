// Copyright (c) 2026 wdd. Licensed under PolyForm Noncommercial 1.0.0.
using NAudio.Wave;

namespace MuiltVoice.Audio.Tests.Helpers;

/// <summary>
/// 测试用 ISampleProvider：持续返回固定幅值的样本，便于验证混音求和。
/// </summary>
internal sealed class ConstantSampleProvider : ISampleProvider
{
    private readonly float _value;

    public ConstantSampleProvider(float value, int sampleRate = 48000, int channels = 2)
    {
        _value = value;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        for (int i = 0; i < count; i++)
        {
            buffer[offset + i] = _value;
        }
        return count;
    }
}

/// <summary>
/// 测试用 ISampleProvider：返回一段已知的样本序列后停止（返回 0 表示流结束）。
/// </summary>
internal sealed class FixedSequenceSampleProvider : ISampleProvider
{
    private readonly float[] _data;
    private int _position;

    public FixedSequenceSampleProvider(float[] data, int sampleRate = 48000, int channels = 2)
    {
        _data = data;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = 0;
        while (read < count && _position < _data.Length)
        {
            buffer[offset + read] = _data[_position];
            _position++;
            read++;
        }
        return read;
    }
}

/// <summary>
/// 测试用 ISampleProvider：包装真实源，但对外谎报一个不同的 WaveFormat，
/// 用于验证 TeeSampleProvider 等组件的格式守卫。
/// </summary>
internal sealed class LyingFormatSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly WaveFormat _fakeFormat;

    public LyingFormatSampleProvider(ISampleProvider source, WaveFormat fakeFormat)
    {
        _source = source;
        _fakeFormat = fakeFormat;
    }

    public WaveFormat WaveFormat => _fakeFormat;

    public int Read(float[] buffer, int offset, int count)
        => _source.Read(buffer, offset, count);
}
