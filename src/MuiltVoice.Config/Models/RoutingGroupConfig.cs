// Copyright (c) 2026 wdd. Licensed under PolyForm Noncommercial 1.0.0.
namespace MuiltVoice.Config.Models;

/// <summary>
/// 一个路由路由的持久化配置：路由名 + 自身的输入/输出选择与音量。
/// 对应 HTML 原型中一个 Modal 的内容。
/// </summary>
public sealed class RoutingGroupConfig
{
    /// <summary>路由标识（用于 UI 层匹配已运行实例；为空时视为新路由）。</summary>
    public string GroupId { get; set; } = string.Empty;

    /// <summary>路由显示名。</summary>
    public string Name { get; set; } = "未命名路由";

    /// <summary>该路由的输入设备配置。</summary>
    public List<InputConfig> Inputs { get; set; } = new();

    /// <summary>该路由的输出设备配置。</summary>
    public List<OutputConfig> Outputs { get; set; } = new();

    /// <summary>
    /// 该路由的音频运行参数（延迟/缓冲/电平轮询）。
    /// 仅在下次启动路由时生效，运行中改动不影响当前路由。
    /// </summary>
    public AudioSettings Audio { get; set; } = new();
}
