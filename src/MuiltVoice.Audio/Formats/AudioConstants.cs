// Copyright (c) 2026 wdd. Licensed under PolyForm Noncommercial 1.0.0.
using NAudio.Wave;

namespace MuiltVoice.Audio.Formats;

/// <summary>
/// 全局音频格式常量。所有输入在进入混音器前统一规范化为此格式：
/// 48kHz、2 声道、32 位 IEEE float。选择 float 便于线性叠加与限幅，
/// 48kHz 兼顾音质与延迟，也是多数现代音频接口的默认 mix format。
/// </summary>
public static class AudioConstants
{
    public const int SampleRate = 48000;
    public const int Channels = 2;

    /// <summary>统一规范格式：48kHz stereo IEEE float。</summary>
    public static readonly WaveFormat UnifiedFormat =
        WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);

    /// <summary>
    /// 输入缓冲最大时长：超过则丢弃旧数据（防背压堆积）。
    /// 调到 200ms：与渲染端 latency 同量级，避免输入端堆积造成隐性延迟。
    /// </summary>
    public static readonly TimeSpan InputBufferDuration = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// 每个 tap（扇出端）缓冲最大时长。
    /// 500ms→200ms：限制从输出端最大堆积，进一步压缩端到端延迟。
    /// </summary>
    public static readonly TimeSpan TapBufferDuration = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// WASAPI 共享模式渲染目标延迟（毫秒）。
    /// 50ms→20ms：典型端到端延迟可降 30~60ms。0x8889000A 风险由
    /// OutputChannel 的多策略回退兜底（失败自动加大到 50ms）。
    /// </summary>
    public const int RenderLatencyMs = 20;

    /// <summary>回退策略用的更大 latency（毫秒）。</summary>
    public const int RenderLatencyFallbackMs = 50;

    /// <summary>WASAPI 捕获缓冲周期（毫秒）。</summary>
    public const int CaptureBufferMs = 50;
}
