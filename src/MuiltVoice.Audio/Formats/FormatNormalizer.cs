// Copyright (c) 2026 wdd. Licensed under PolyForm Noncommercial 1.0.0.
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MuiltVoice.Audio.Formats;

/// <summary>
/// 格式规范化工具：把任意采样率/声道的 ISampleProvider 转换为
/// 统一格式（48kHz stereo IEEE float），供 MixingSampleProvider 使用。
/// </summary>
public static class FormatNormalizer
{
    /// <summary>
    /// 把 <paramref name="source"/> 规范化为 48kHz stereo float。
    /// 流程：WdlResampler(48k) → ToStereo（mono 升立体声，stereo 直通）。
    /// </summary>
    public static ISampleProvider NormalizeToUnified(ISampleProvider source)
    {
        var resampled = new WdlResamplingSampleProvider(source, AudioConstants.SampleRate);
        return resampled.ToStereo();
    }

    /// <summary>检查给定 provider 是否已是统一格式。</summary>
    public static bool IsUnified(ISampleProvider source)
    {
        var f = source.WaveFormat;
        return f.SampleRate == AudioConstants.SampleRate
            && f.Channels == AudioConstants.Channels
            && f.Encoding == WaveFormatEncoding.IeeeFloat;
    }
}
