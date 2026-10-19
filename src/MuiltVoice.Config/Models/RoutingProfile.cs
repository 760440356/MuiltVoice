// Copyright (c) 2026 wdd. Licensed under PolyForm Noncommercial 1.0.0.
namespace MuiltVoice.Config.Models;

/// <summary>
/// 路由方案：包含一个或多个路由路由（每组独立路由链路）的可序列化快照。
/// 向后兼容：若仅使用旧版 Inputs/Outputs 字段（单组广播），在加载时会自动迁移到
/// <see cref="Groups"/> 的单元素列表中，保持旧方案 JSON 可直接被新版读取。
/// </summary>
public sealed class RoutingProfile
{
    /// <summary>方案名（同时作为文件名，去除非法字符）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 多路由路由（P8+ 新结构）。每个路由独立启停、独立混音。
    /// 优先使用此字段；<see cref="Inputs"/> / <see cref="Outputs"/> 仅为兼容旧存档。
    /// </summary>
    public List<RoutingGroupConfig> Groups { get; set; } = new();

    /// <summary>
    /// [旧兼容字段] 单组广播式方案的输入列表。
    /// 当 <see cref="Groups"/> 为空且 Inputs/Outputs 不为空时，表示这是一个旧存档，
    /// 调用方应在读取后调用 <see cref="MigrateLegacyToGroups"/> 将内容迁移为单个 Group。
    /// </summary>
    public List<InputConfig> Inputs { get; set; } = new();

    /// <summary>[旧兼容字段] 单组广播式方案的输出列表。</summary>
    public List<OutputConfig> Outputs { get; set; } = new();

    /// <summary>最后更新时间（UTC）。</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 从旧的 Inputs/Outputs 字段迁移到 Groups。
    /// - Groups 已有内容：不执行迁移（新结构优先）
    /// - Groups 为空但 Inputs/Outputs 非空：生成一个名为 "默认路由" 的 Group
    /// - 迁移后会把 Inputs/Outputs 置空，避免保存时再写出旧字段
    /// </summary>
    public void MigrateLegacyToGroups()
    {
        if (Groups.Count > 0) return;
        if (Inputs.Count == 0 && Outputs.Count == 0) return;

        Groups.Add(new RoutingGroupConfig
        {
            GroupId = "legacy-default",
            Name = "默认路由",
            Inputs = Inputs.ToList(),
            Outputs = Outputs.ToList(),
        });
        Inputs.Clear();
        Outputs.Clear();
    }

    /// <summary>
    /// 保存前的规范化：
    /// - 迁移旧存档（若还未迁移）
    /// - 为每个无 GroupId 的路由生成新 Id
    /// - 确保 Name 非空
    /// </summary>
    public void NormalizeBeforeSave()
    {
        MigrateLegacyToGroups();
        for (int i = 0; i < Groups.Count; i++)
        {
            var g = Groups[i];
            if (string.IsNullOrWhiteSpace(g.GroupId))
            {
                g.GroupId = "g_" + Guid.NewGuid().ToString("N")[..8];
            }
            if (string.IsNullOrWhiteSpace(g.Name))
            {
                g.Name = $"路由 {i + 1}";
            }
        }
    }
}
