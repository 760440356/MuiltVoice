// Copyright (c) 2026 wdd. Licensed under PolyForm Noncommercial 1.0.0.
namespace MuiltVoice.Audio.Pipeline;

/// <summary>
/// 输出通道规格（不可变）。描述一路输出的设备 Id、是否为主输出、初始音量与静音。
/// 主输出驱动整条 pull 链（直接拉取混音/Tee 输出）；次级输出由 TeeSampleProvider
/// 推入数据，独立拉取。一个路由配置中应恰好有一个主输出。
/// </summary>
public sealed record OutputSpec
{
    /// <summary>设备 Id（对应 MMDevice.ID）。</summary>
    public required string DeviceId { get; init; }

    /// <summary>是否为主输出（驱动 pull 链）。整个配置中应恰好一个为 true。</summary>
    public bool IsMaster { get; init; }

    /// <summary>初始音量倍率（默认 1.0）。</summary>
    public float Volume { get; init; } = 1f;

    /// <summary>初始是否静音（默认 false）。</summary>
    public bool Muted { get; init; }

    /// <summary>显示名（可选，仅用于 UI/日志）。</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>
    /// 是否对该设备启用 WASAPI 独占模式（设备粒度，与 OutputConfig 对齐）。
    /// 默认 false：走共享模式，不影响其他程序。
    /// </summary>
    public bool UseExclusiveMode { get; init; }
}
