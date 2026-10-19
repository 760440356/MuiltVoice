// Copyright (c) 2026 wdd. Licensed under PolyForm Noncommercial 1.0.0.
using System.Globalization;
using NAudio.CoreAudioApi;

namespace MuiltVoice.Audio.Devices;

/// <summary>
/// 识别 VB-Cable 虚拟音频端点。VB-Cable 安装后会暴露一对虚拟设备：
/// "CABLE Input (VB-Audio Virtual Cable)" 出现在播放设备列表（Render，写入端），
/// "CABLE Output (VB-Audio Virtual Cable)" 出现在录音设备列表（Capture，读取端）。
/// </summary>
public static class VirtualCableDetector
{
    private const string CableInputMarker = "CABLE Input";
    private const string CableOutputMarker = "CABLE Output";
    private const string VBAudioMarker = "VB-Audio Virtual Cable";

    /// <summary>该设备是否为 VB-Cable 的写入端（CABLE Input，Render 设备）。</summary>
    public static bool IsCableInput(MMDevice device)
    {
        var name = GetNames(device);
        return Contains(name.friendly, CableInputMarker) || Contains(name.adapter, CableInputMarker);
    }

    /// <summary>该设备是否为 VB-Cable 的读取端（CABLE Output，Capture 设备）。</summary>
    public static bool IsCableOutput(MMDevice device)
    {
        var name = GetNames(device);
        return Contains(name.friendly, CableOutputMarker) || Contains(name.adapter, CableOutputMarker);
    }

    /// <summary>该设备是否为 VB-Cable 系列端点之一。</summary>
    public static bool IsVirtualCable(MMDevice device)
    {
        var name = GetNames(device);
        return Contains(name.friendly, VBAudioMarker)
            || Contains(name.adapter, VBAudioMarker)
            || IsCableInput(device)
            || IsCableOutput(device);
    }

    private static (string friendly, string adapter) GetNames(MMDevice device)
    {
        // FriendlyName 可能抛异常（设备未就绪），用 try/get 兜底
        string? friendly = SafeRead(() => device.FriendlyName);
        string? adapter = SafeRead(() => device.DeviceFriendlyName);
        return (friendly ?? string.Empty, adapter ?? string.Empty);
    }

    private static bool Contains(string? haystack, string needle)
    {
        if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle))
        {
            return false;
        }
        return CultureInfo.InvariantCulture.CompareInfo.IndexOf(haystack, needle, CompareOptions.OrdinalIgnoreCase) >= 0;
    }

    private static string? SafeRead(Func<string?> read)
    {
        try { return read(); }
        catch { return null; }
    }
}
