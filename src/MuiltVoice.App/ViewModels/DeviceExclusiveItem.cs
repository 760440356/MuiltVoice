// Copyright (c) 2026 wdd. Licensed under PolyForm Noncommercial 1.0.0.
using MuiltVoice.Audio.Devices;
using NAudio.CoreAudioApi;

namespace MuiltVoice.App.ViewModels;

/// <summary>
/// 设备设置页（DeviceSettingsWindow）专用的行 VM：
/// 承载一个输出设备的"独占开关"状态（输入设备仅展示，不可调）。
/// 只与设备粒度的"是否独占"单一职责对齐，与路由解耦。
/// </summary>
public sealed class DeviceExclusiveItem : ViewModelBase
{
    private bool _isExclusive;

    public string DeviceId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool IsVirtualCable { get; init; }
    public bool IsDefault { get; init; }
    public bool IsRender { get; init; }   // true=输出设备（可调）；false=输入设备（只读展示）

    public bool IsExclusive
    {
        get => _isExclusive;
        set => SetProperty(ref _isExclusive, value);
    }

    public string DisplayName
    {
        get
        {
            var prefix = IsRender ? "🔊 " : "🎤 ";
            var suffix = IsVirtualCable ? " ⚡ VB-Cable" : "";
            if (IsDefault) suffix += " (默认)";
            return prefix + Name + suffix;
        }
    }

    public static DeviceExclusiveItem From(DeviceInfo d, bool isExclusive) => new()
    {
        DeviceId = d.Id,
        Name = d.FriendlyName,
        IsVirtualCable = d.IsVirtualCable,
        IsDefault = d.IsDefault,
        IsRender = d.DataFlow == DataFlow.Render,
        IsExclusive = isExclusive,
    };
}
