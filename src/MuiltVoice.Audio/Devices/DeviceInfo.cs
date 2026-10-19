// Copyright (c) 2026 wdd. Licensed under PolyForm Noncommercial 1.0.0.
using NAudio.CoreAudioApi;

namespace MuiltVoice.Audio.Devices;

/// <summary>
/// 不可变的音频设备信息快照。封装从 <see cref="MMDevice"/> 读取的元数据，
/// 避免在 UI/配置层持有 COM 对象。引擎需要真实设备时通过
/// <see cref="AudioDeviceEnumerator.GetDeviceById"/> 按 Id 重新获取。
/// </summary>
public sealed record DeviceInfo
{
    /// <summary>设备在 Windows 中的唯一标识（IMMDevice.Id）。</summary>
    public required string Id { get; init; }

    /// <summary>友好的显示名称，UI 直接展示。</summary>
    public required string FriendlyName { get; init; }

    /// <summary>设备适配器名（通常比 FriendlyName 更稳定）。</summary>
    public string DeviceFriendlyName { get; init; } = string.Empty;

    /// <summary>设备当前状态。</summary>
    public DeviceState State { get; init; }

    /// <summary>数据流向：Render（输出/播放设备）或 Capture（输入/录音设备）。</summary>
    public DataFlow DataFlow { get; init; }

    /// <summary>是否为当前默认设备（Console 角色）。</summary>
    public bool IsDefault { get; init; }

    /// <summary>是否为 VB-Cable 的写入端（CABLE Input，对应 Render 设备）。</summary>
    public bool IsCableInput { get; init; }

    /// <summary>是否为 VB-Cable 的读取端（CABLE Output，对应 Capture 设备）。</summary>
    public bool IsCableOutput { get; init; }

    /// <summary>是否为任意 VB-Cable 端点。</summary>
    public bool IsVirtualCable => IsCableInput || IsCableOutput;

    /// <summary>设备方向的可读标签：Input / Output。</summary>
    public string DirectionLabel => DataFlow == DataFlow.Render ? "Output" : "Input";
}
