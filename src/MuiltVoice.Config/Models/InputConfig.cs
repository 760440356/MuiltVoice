// Copyright (c) 2026 wdd. Licensed under PolyForm Noncommercial 1.0.0.
namespace MuiltVoice.Config.Models;

/// <summary>
/// 输入设备的持久化配置（与 <c>MuiltVoice.Audio.Pipeline.InputSpec</c> 对应，
/// 但位于无 NAudio 依赖的 Config 程序集，便于序列化）。
/// </summary>
public sealed class InputConfig
{
    /// <summary>设备 Id。Loopback 项存默认渲染设备 Id。</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>显示名（仅用于 UI 展示，不参与匹配）。</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>是否为系统回放 Loopback。</summary>
    public bool Loopback { get; set; }

    /// <summary>是否选中参与路由。</summary>
    public bool Selected { get; set; }

    /// <summary>音量倍率（0..2+，1=原音量）。</summary>
    public float Volume { get; set; } = 1f;

    /// <summary>是否静音。</summary>
    public bool Muted { get; set; }
}
