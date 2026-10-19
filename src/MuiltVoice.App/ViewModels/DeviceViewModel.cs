// Copyright (c) 2026 wdd. Licensed under PolyForm Noncommercial 1.0.0.
using MuiltVoice.Audio.Devices;

namespace MuiltVoice.App.ViewModels;

/// <summary>
/// 设备视图模型：UI 列表中单个设备的可绑定状态（选中/音量/静音/电平）。
/// 同时承载输入与输出设备（由 <see cref="IsInput"/> 区分）。
/// </summary>
public sealed class DeviceViewModel : ViewModelBase
{
    private bool _isSelected;
    private float _volume = 1f;
    private bool _isMuted;
    private float _peakLevel;
    private long _bufferedBytes;
    private bool _isMaster;

    /// <summary>设备 Id（Loopback 项为默认渲染设备 Id）。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>原始设备名（不含标记）。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>方向标签：Input / Output / Loopback。</summary>
    public string DirectionLabel { get; init; } = string.Empty;

    /// <summary>是否为输入设备（含 Loopback）。</summary>
    public bool IsInput { get; init; }

    /// <summary>是否为输出设备。</summary>
    public bool IsOutput => !IsInput;

    public bool IsCableInput { get; init; }
    public bool IsCableOutput { get; init; }
    public bool IsVirtualCable { get; init; }
    public bool IsDefault { get; init; }

    /// <summary>IsCable 的简写，供模板/转换里用。</summary>
    public bool IsCable => IsVirtualCable || IsCableInput || IsCableOutput;

    /// <summary>是否为系统回放 Loopback 合成项。</summary>
    public bool IsLoopback { get; init; }

    /// <summary>是否被选中参与路由。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>音量倍率（0..2，1=原音量）。UI 滑块映射到 0..200%。</summary>
    public float Volume
    {
        get => _volume;
        set => SetProperty(ref _volume, value);
    }

    /// <summary>UI 滑块用的整数百分比（0..200），与 Volume 双向同步。</summary>
    public int VolumePercent
    {
        get => (int)Math.Round(_volume * 100f);
        set => Volume = value / 100f;
    }

    public bool IsMuted
    {
        get => _isMuted;
        set => SetProperty(ref _isMuted, value);
    }

    /// <summary>输入峰值电平（0..1+），由定时器从引擎拉取更新。</summary>
    public float PeakLevel
    {
        get => _peakLevel;
        set => SetProperty(ref _peakLevel, value);
    }

    /// <summary>输出缓冲字节数（监控堆积），由定时器从引擎拉取更新。</summary>
    public long BufferedBytes
    {
        get => _bufferedBytes;
        set => SetProperty(ref _bufferedBytes, value);
    }

    /// <summary>是否为主输出（驱动 pull 链）。</summary>
    public bool IsMaster
    {
        get => _isMaster;
        set => SetProperty(ref _isMaster, value);
    }

    /// <summary>
    /// 是否对该输出设备启用 WASAPI 独占模式（设备粒度）。
    /// 仅输出设备有效；输入设备永远共享（NAudio WasapiCapture 不支持独占）。
    /// 勾选后该设备被独占，其他程序无法使用；失败自动回退共享。
    /// </summary>
    public bool IsExclusive
    {
        get => _isExclusive;
        set => SetProperty(ref _isExclusive, value);
    }
    private bool _isExclusive;

    /// <summary>带标记的显示名。</summary>
    public string DisplayName
    {
        get
        {
            var prefix = IsLoopback ? "🔁 " : (IsInput ? "🎤 " : "🔊 ");
            var suffix = IsVirtualCable ? " ⚡" : "";
            if (IsDefault)
            {
                suffix += " (默认)";
            }
            return prefix + Name + suffix;
        }
    }

    public static DeviceViewModel FromInput(DeviceInfo d) => new()
    {
        Id = d.Id,
        Name = d.FriendlyName,
        DirectionLabel = d.DirectionLabel,
        IsInput = true,
        IsCableInput = d.IsCableInput,
        IsCableOutput = d.IsCableOutput,
        IsVirtualCable = d.IsVirtualCable,
        IsDefault = d.IsDefault,
    };

    public static DeviceViewModel Loopback(string defaultRenderId) => new()
    {
        Id = defaultRenderId,
        Name = "系统回放 Loopback",
        DirectionLabel = "Loopback",
        IsInput = true,
        IsLoopback = true,
    };

    public static DeviceViewModel FromOutput(DeviceInfo d) => new()
    {
        Id = d.Id,
        Name = d.FriendlyName,
        DirectionLabel = d.DirectionLabel,
        IsInput = false,
        IsCableInput = d.IsCableInput,
        IsCableOutput = d.IsCableOutput,
        IsVirtualCable = d.IsVirtualCable,
        IsDefault = d.IsDefault,
    };
}
