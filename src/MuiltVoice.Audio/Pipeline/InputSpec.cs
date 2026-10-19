// Copyright (c) 2026 wdd. Licensed under PolyForm Noncommercial 1.0.0.
namespace MuiltVoice.Audio.Pipeline;

/// <summary>
/// 输入通道规格（不可变）。描述一路输入的设备 Id、是否 Loopback、初始音量与静音。
/// 用于 AudioEngine.StartMixing 的入参，以及配置存档的序列化。
/// </summary>
public sealed record InputSpec
{
    /// <summary>设备 Id（对应 MMDevice.ID）。</summary>
    public required string DeviceId { get; init; }

    /// <summary>是否为系统回放 Loopback 捕获。</summary>
    public bool Loopback { get; init; }

    /// <summary>初始音量倍率（默认 1.0）。</summary>
    public float Volume { get; init; } = 1f;

    /// <summary>初始是否静音（默认 false）。</summary>
    public bool Muted { get; init; }

    /// <summary>显示名（可选，仅用于 UI/日志，不参与匹配）。</summary>
    public string DisplayName { get; init; } = string.Empty;
}
