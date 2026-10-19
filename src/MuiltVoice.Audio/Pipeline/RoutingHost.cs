// Copyright (c) 2026 wdd. Licensed under PolyForm Noncommercial 1.0.0.
using MuiltVoice.Audio.Devices;
using MuiltVoice.Config.Models;
using Serilog;

namespace MuiltVoice.Audio.Pipeline;

/// <summary>
/// 多路由组管理器：每路由持有一个独立的 <see cref="AudioEngine"/> 实例，
/// 实现「路由 N 的输入 → 路由 N 的输出」完全隔离的独立链路。
/// 提供按组启停、全局启停、索引式访问，方便上层 ViewModel 按列表一一映射。
///
/// 说明：释放后所有方法都 NoOp（不抛 ObjectDisposedException），
/// 避免 WPF 关闭时事件回调顺序不保证的 disposed crash。
/// </summary>
public sealed class RoutingHost : IDisposable
{
    private readonly Dictionary<string, AudioEngine> _engines = new();
    private readonly List<string> _order = new();
    private readonly ILogger _log = AppLog.Log.ForContext<RoutingHost>();
    private bool _disposed;

    /// <summary>任一路由组运行状态变化时触发，args = (groupId, running)。</summary>
    public event EventHandler<(string GroupId, bool Running)>? GroupRunningChanged;

    /// <summary>任何组的启停都可能改变的"全局运行态"变化时触发。</summary>
    public event EventHandler<bool>? AnyRunningChanged;

    /// <summary>当前已注册（无论是否运行）的组 ID 顺序（与列表顺序一致）。</summary>
    public IReadOnlyList<string> GroupIds => _order;

    /// <summary>至少有一个组在运行。</summary>
    public bool IsAnyRunning => _engines.Values.Any(e => e.IsRunning);

    /// <summary>运行中的组数量。</summary>
    public int RunningCount => _engines.Values.Count(e => e.IsRunning);

    /// <summary>尝试获取指定组的引擎。未注册或已释放则返回 null。</summary>
    public AudioEngine? TryGetEngine(string groupId)
    {
        if (_disposed) return null;
        return _engines.TryGetValue(groupId, out var e) ? e : null;
    }

    /// <summary>判断组是否已注册（已释放则返回 false）。</summary>
    public bool ContainsGroup(string groupId) => !_disposed && _engines.ContainsKey(groupId);

    /// <summary>
    /// 注册一个组（创建空 AudioEngine，未启动）。
    /// 如果组已存在则不重复创建（返回已有的引擎）。
    /// </summary>
    public AudioEngine EnsureGroup(string groupId, int? insertIndex = null)
    {
        if (_disposed)
        {
            _log.Warning("EnsureGroup(Id={Id}) 调用于已释放的 RoutingHost（NoOp，返回哑对象）", groupId);
            return new AudioEngine();
        }
        if (_engines.TryGetValue(groupId, out var exist))
        {
            _log.Debug("EnsureGroup(Id={Id}) 已存在，直接返回", groupId);
            return exist;
        }
        var engine = new AudioEngine();
        engine.RunningChanged += (_, r) =>
        {
            if (_disposed) return;
            try
            {
                _log.Debug("组 {Id} Running={R} 事件触发", groupId, r);
                GroupRunningChanged?.Invoke(this, (groupId, r));
                AnyRunningChanged?.Invoke(this, IsAnyRunning);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "组 {Id} 事件回调异常（已吞）", groupId);
            }
        };
        _engines.Add(groupId, engine);
        if (insertIndex.HasValue && insertIndex.Value < _order.Count)
            _order.Insert(insertIndex.Value, groupId);
        else
            _order.Add(groupId);
        _log.Information("EnsureGroup：注册组 Id={Id}，index={Index}，当前总组数={Count}",
            groupId, insertIndex ?? _order.Count - 1, _order.Count);
        return engine;
    }

    /// <summary>注销组：如果在运行先停止，然后释放引擎。不存在/已释放则 NoOp。</summary>
    public void RemoveGroup(string groupId)
    {
        if (_disposed)
        {
            _log.Warning("RemoveGroup(Id={Id}) 调用于已释放的 RoutingHost（NoOp）", groupId);
            return;
        }
        if (!_engines.TryGetValue(groupId, out var engine))
        {
            _log.Warning("RemoveGroup(Id={Id}) 组不存在（NoOp）", groupId);
            return;
        }
        _log.Information("RemoveGroup(Id={Id}) 开始，IsRunning={R}", groupId, engine.IsRunning);
        try { engine.Stop(); }
        catch (Exception ex) { _log.Warning(ex, "组 {Id} Stop 异常", groupId); }
        try { engine.Dispose(); }
        catch (Exception ex) { _log.Warning(ex, "组 {Id} Dispose 异常", groupId); }
        _engines.Remove(groupId);
        _order.Remove(groupId);
        try { AnyRunningChanged?.Invoke(this, IsAnyRunning); }
        catch (Exception ex) { _log.Warning(ex, "AnyRunningChanged 回调异常（已吞）"); }
        _log.Information("RemoveGroup(Id={Id}) 完成，剩余组数={Count}", groupId, _order.Count);
    }

    /// <summary>
    /// 启动单个组：必须先 EnsureGroup。settings 为空则使用 Balanced 默认值。
    /// </summary>
    public void StartGroup(
        string groupId,
        AudioDeviceEnumerator enumerator,
        IReadOnlyList<InputSpec> inputs,
        IReadOnlyList<OutputSpec> outputs,
        AudioSettings? settings = null)
    {
        if (_disposed)
        {
            _log.Warning("StartGroup(Id={Id}) 调用于已释放的 RoutingHost（NoOp）", groupId);
            return;
        }
        _log.Information("StartGroup(Id={Id}) 调用：输入{Nin}个，输出{Nout}个，预设={Pr}",
            groupId, inputs.Count, outputs.Count, settings?.Preset ?? AudioPreset.Balanced);
        var engine = EnsureGroup(groupId);
        engine.StartWithRouting(enumerator, inputs, outputs, settings);
    }

    /// <summary>停止单个组。不存在/未运行/已释放则 NoOp。</summary>
    public void StopGroup(string groupId)
    {
        if (_disposed)
        {
            _log.Warning("StopGroup(Id={Id}) 调用于已释放的 RoutingHost（NoOp）", groupId);
            return;
        }
        _log.Information("StopGroup(Id={Id}) 调用", groupId);
        if (_engines.TryGetValue(groupId, out var engine))
        {
            try { engine.Stop(); }
            catch (Exception ex) { _log.Warning(ex, "组 {Id} Stop 异常（已吞）", groupId); }
        }
    }

    /// <summary>启动所有当前未运行、且调用方已确保可启动的组（通过 factory 按需构参）。</summary>
    public int StartAll(
        AudioDeviceEnumerator enumerator,
        Func<string, (IReadOnlyList<InputSpec> Inputs, IReadOnlyList<OutputSpec> Outputs)?> specFactory)
    {
        if (_disposed)
        {
            _log.Warning("StartAll 调用于已释放的 RoutingHost（return 0）");
            return 0;
        }
        ArgumentNullException.ThrowIfNull(specFactory);
        _log.Information("StartAll 开始，候选组数={N}", _order.Count);
        int started = 0;
        int failed = 0;
        foreach (var gid in _order.ToArray())
        {
            if (!_engines.TryGetValue(gid, out var engine)) continue;
            if (engine.IsRunning) continue;
            var spec = specFactory(gid);
            if (spec is null)
            {
                _log.Debug("StartAll：组 {Id} specFactory 返回 null，跳过", gid);
                continue;
            }
            try
            {
                engine.StartWithRouting(enumerator, spec.Value.Inputs, spec.Value.Outputs);
                started++;
            }
            catch (Exception ex)
            {
                failed++;
                _log.Error(ex, "StartAll：组 {Id} 启动失败（跳过其他路由继续）", gid);
            }
        }
        _log.Information("StartAll 完成：成功 {Ok} 个，失败 {Err} 个", started, failed);
        return started;
    }

    /// <summary>停止所有正在运行的组。返回被停止的组数量。</summary>
    public int StopAll()
    {
        if (_disposed)
        {
            _log.Warning("StopAll 调用于已释放的 RoutingHost（return 0）");
            return 0;
        }
        _log.Information("StopAll 开始，运行中={R}", RunningCount);
        int stopped = 0;
        foreach (var gid in _order.ToArray())
        {
            if (!_engines.TryGetValue(gid, out var engine)) continue;
            if (engine.IsRunning)
            {
                try { engine.Stop(); stopped++; }
                catch (Exception ex) { _log.Warning(ex, "StopAll：组 {Id} 异常（已吞）", gid); }
            }
        }
        _log.Information("StopAll 完成：停止 {N} 个", stopped);
        return stopped;
    }

    /// <summary>返回指定组的 IsRunning（未注册/已释放返回 false）。</summary>
    public bool IsGroupRunning(string groupId)
        => !_disposed && _engines.TryGetValue(groupId, out var e) && e.IsRunning;

    public void Dispose()
    {
        if (_disposed) return;
        _log.Information("Dispose 开始，剩余组数={N}，运行中={R}", _order.Count, RunningCount);
        _disposed = true;
        foreach (var gid in _order.ToArray())
            RemoveGroup(gid);
        _log.Information("Dispose 完成");
    }
}
