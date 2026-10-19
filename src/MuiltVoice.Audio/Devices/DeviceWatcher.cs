// Copyright (c) 2026 wdd. Licensed under PolyForm Noncommercial 1.0.0.
using System.Threading;
using NAudio.CoreAudioApi;

namespace MuiltVoice.Audio.Devices;

/// <summary>
/// 设备热插拔监视器：以固定间隔轮询设备列表，检测到增删时触发
/// <see cref="DevicesChanged"/> 事件。
/// 采用轮询而非 COM 通知回调，实现简单且对 USB 插拔响应足够快（1–2s）。
/// </summary>
/// <remarks>
/// 事件在 <see cref="ThreadPool"/> 线程触发，UI 订阅者需自行 Dispatcher 切换。
/// </remarks>
public sealed class DeviceWatcher : IDisposable
{
    private readonly AudioDeviceEnumerator _enumerator;
    private readonly Timer _timer;
    private HashSet<string> _lastInputIds;
    private HashSet<string> _lastOutputIds;
    private bool _disposed;

    /// <summary>设备列表发生变化时触发（增删任一方向）。</summary>
    public event EventHandler<DevicesChangedEventArgs>? DevicesChanged;

    public DeviceWatcher(AudioDeviceEnumerator enumerator, TimeSpan? interval = null)
    {
        _enumerator = enumerator ?? throw new ArgumentNullException(nameof(enumerator));
        _lastInputIds = SnapshotIds(DataFlow.Capture);
        _lastOutputIds = SnapshotIds(DataFlow.Render);
        var due = interval ?? TimeSpan.FromSeconds(2);
        _timer = new Timer(OnTick, null, due, due);
    }

    /// <summary>立即触发一次检测（不等待下个周期）。</summary>
    public void PollNow()
    {
        OnTick(null);
    }

    private void OnTick(object? state)
    {
        if (_disposed)
        {
            return;
        }
        var inputs = SnapshotIds(DataFlow.Capture);
        var outputs = SnapshotIds(DataFlow.Render);
        bool changed = !SetEquals(_lastInputIds, inputs) || !SetEquals(_lastOutputIds, outputs);
        _lastInputIds = inputs;
        _lastOutputIds = outputs;
        if (changed)
        {
            DevicesChanged?.Invoke(this, new DevicesChangedEventArgs(inputs.Count, outputs.Count));
        }
    }

    private HashSet<string> SnapshotIds(DataFlow flow)
    {
        try
        {
            var list = flow == DataFlow.Capture
                ? _enumerator.ListCaptureDevices()
                : _enumerator.ListRenderDevices();
            return list.Select(d => d.Id).ToHashSet();
        }
        catch
        {
            return new HashSet<string>();
        }
    }

    private static bool SetEquals(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }
        foreach (var x in a)
        {
            if (!b.Contains(x))
            {
                return false;
            }
        }
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _timer.Dispose();
    }
}

/// <summary>设备变化事件参数。</summary>
public sealed class DevicesChangedEventArgs : EventArgs
{
    public int InputCount { get; }
    public int OutputCount { get; }
    public DevicesChangedEventArgs(int inputCount, int outputCount)
    {
        InputCount = inputCount;
        OutputCount = outputCount;
    }
}
