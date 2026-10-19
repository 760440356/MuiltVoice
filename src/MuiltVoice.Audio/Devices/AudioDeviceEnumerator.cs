// Copyright (c) 2026 wdd. Licensed under PolyForm Noncommercial 1.0.0.
using NAudio.CoreAudioApi;

namespace MuiltVoice.Audio.Devices;

/// <summary>
/// 音频设备枚举服务，包装 NAudio 的 <see cref="MMDeviceEnumerator"/>。
/// 提供渲染/捕获设备列表、默认设备查询、按 Id 取真实 MMDevice 等能力。
/// 实现 IDisposable 以释放底层 COM 枚举器。
/// </summary>
public sealed class AudioDeviceEnumerator : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private bool _disposed;

    /// <summary>列出所有处于活动状态的渲染（输出/播放）设备。</summary>
    public IReadOnlyList<DeviceInfo> ListRenderDevices()
        => ListDevices(DataFlow.Render);

    /// <summary>列出所有处于活动状态的捕获（输入/录音）设备。</summary>
    public IReadOnlyList<DeviceInfo> ListCaptureDevices()
        => ListDevices(DataFlow.Capture);

    /// <summary>获取当前默认渲染设备（Console 角色），不存在或异常返回 null。</summary>
    public DeviceInfo? GetDefaultRenderDevice()
        => GetDefault(DataFlow.Render);

    /// <summary>获取当前默认捕获设备（Console 角色），不存在或异常返回 null。</summary>
    public DeviceInfo? GetDefaultCaptureDevice()
        => GetDefault(DataFlow.Capture);

    /// <summary>
    /// 按设备 Id 获取真实的 <see cref="MMDevice"/>（COM 对象）。
    /// 调用方负责在使用后 Dispose 返回值。设备不存在或异常返回 null。
    /// </summary>
    public MMDevice? GetDeviceById(string id)
    {
        ThrowIfDisposed();
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }
        try
        {
            var dev = _enumerator.GetDevice(id);
            return dev;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>获取底层枚举器（供高级用法如注册通知回调使用）。</summary>
    public MMDeviceEnumerator Underlying
    {
        get
        {
            ThrowIfDisposed();
            return _enumerator;
        }
    }

    private IReadOnlyList<DeviceInfo> ListDevices(DataFlow flow)
    {
        ThrowIfDisposed();
        var defaultId = GetDefaultDeviceId(flow);
        var list = new List<DeviceInfo>();
        foreach (var dev in _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (dev)
            {
                list.Add(ToDeviceInfo(dev, flow, defaultId));
            }
        }
        return list;
    }

    private DeviceInfo? GetDefault(DataFlow flow)
    {
        ThrowIfDisposed();
        try
        {
            using var dev = _enumerator.GetDefaultAudioEndpoint(flow, Role.Console);
            if (dev is null)
            {
                return null;
            }
            return ToDeviceInfo(dev, flow, dev.ID);
        }
        catch
        {
            return null;
        }
    }

    private string? GetDefaultDeviceId(DataFlow flow)
    {
        try
        {
            using var dev = _enumerator.GetDefaultAudioEndpoint(flow, Role.Console);
            return dev?.ID;
        }
        catch
        {
            return null;
        }
    }

    private static DeviceInfo ToDeviceInfo(MMDevice dev, DataFlow flow, string? defaultId)
    {
        return new DeviceInfo
        {
            Id = dev.ID,
            FriendlyName = SafeRead(() => dev.FriendlyName) ?? SafeRead(() => dev.DeviceFriendlyName) ?? "<unknown>",
            DeviceFriendlyName = SafeRead(() => dev.DeviceFriendlyName) ?? string.Empty,
            State = dev.State,
            DataFlow = flow,
            IsDefault = !string.IsNullOrEmpty(defaultId) && dev.ID == defaultId,
            IsCableInput = VirtualCableDetector.IsCableInput(dev),
            IsCableOutput = VirtualCableDetector.IsCableOutput(dev),
        };
    }

    private static string? SafeRead(Func<string?> read)
    {
        try { return read(); }
        catch { return null; }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(AudioDeviceEnumerator));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _enumerator.Dispose();
    }
}
