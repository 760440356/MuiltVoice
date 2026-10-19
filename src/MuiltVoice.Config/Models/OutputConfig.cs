// Copyright (c) 2026 wdd. Licensed under PolyForm Noncommercial 1.0.0.
namespace MuiltVoice.Config.Models;

/// <summary>
/// 输出设备的持久化配置（与 <c>MuiltVoice.Audio.Pipeline.OutputSpec</c> 对应）。
/// 注意：UseExclusiveMode 是**设备粒度**而非路由粒度——同一台机器上某设备勾选了独占，
/// 任何路由用到它都会走独占；占用后其他程序无法使用此设备。
/// </summary>
public sealed class OutputConfig
{
    /// <summary>设备 Id。</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>显示名。</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>是否选中参与路由。</summary>
    public bool Selected { get; set; }

    /// <summary>音量倍率（0..2+，1=原音量）。</summary>
    public float Volume { get; set; } = 1f;

    /// <summary>是否静音。</summary>
    public bool Muted { get; set; }

    /// <summary>
    /// 是否对该设备启用 WASAPI 独占模式（方案 C，延迟可降到 ~10-30ms）。
    /// 设备粒度：勾选后该设备被独占，其他程序无法使用；失败自动回退共享模式。
    /// 默认 false：保持共享，不影响其他程序。
    /// </summary>
    public bool UseExclusiveMode { get; set; }
}
