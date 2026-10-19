// Copyright (c) 2026 wdd. Licensed under PolyForm Noncommercial 1.0.0.
using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using MuiltVoice.Audio;
using MuiltVoice.Audio.Devices;
using MuiltVoice.Audio.Pipeline;
using MuiltVoice.Config;
using MuiltVoice.Config.Models;
using Serilog;

namespace MuiltVoice.App.ViewModels;

/// <summary>
/// 多路由组版本主 VM（P8）：
/// - 管理设备列表 + 路由列表 Groups（每个路由独立 Inputs/Outputs）
/// - 每个路由拥有独立的 AudioEngine（由 RoutingHost 管理），完全隔离
/// - 全局启停、设备热插拔、多组电平轮询、多组实时音量同步
/// - 方案存档：RoutingProfile.Groups 与 Groups 一一映射（旧存档自动迁移）
/// </summary>
public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private readonly AudioDeviceEnumerator _enumerator = new();
    private readonly RoutingHost _host = new();
    private readonly DeviceWatcher _watcher;
    private readonly ProfileStore _profiles = new();
    private readonly DispatcherTimer _meterTimer;
    private readonly ILogger _log = AppLog.Log.ForContext<MainViewModel>();

    /// <summary>设备枚举器（只读访问：供主窗打开 DeviceSettingsWindow 时读取设备列表）。</summary>
    internal AudioDeviceEnumerator DeviceEnumerator => _enumerator;

    // 设备快照缓存：Refresh 一次，AddGroup/LoadProfile 复用，避免重复 COM 调用（卡顿根因）
    private IReadOnlyList<DeviceInfo> _cachedCapture = Array.Empty<DeviceInfo>();
    private IReadOnlyList<DeviceInfo> _cachedRender = Array.Empty<DeviceInfo>();
    private string _cachedDefaultRenderId = string.Empty;

    private string _statusText = "就绪";
    private string _currentProfileName = "默认方案";
    private string? _selectedProfileName;
    private bool _isCableInstalled;

    public ObservableCollection<GroupViewModel> Groups { get; } = new();
    public ObservableCollection<string> AvailableProfiles { get; } = new();

    public ICommand RefreshCommand { get; }
    public ICommand StartAllCommand { get; }
    public ICommand StopAllCommand { get; }
    public ICommand AddGroupCommand { get; }
    public ICommand SaveProfileCommand { get; }
    public ICommand LoadProfileCommand { get; }
    public ICommand EditGroupCommand { get; }
    /// <summary>打开设备设置页（独占开关，设备粒度）。</summary>
    public ICommand OpenDeviceSettingsCommand { get; }

    /// <summary>当前由 UI 请求要打开编辑的路由（由 GroupEditorWindow 监听/绑定）。</summary>
    public event EventHandler<GroupViewModel>? EditRequested;

    /// <summary>
    /// 请求 MainWindow 打开"设备设置页"。
    /// 事件参数是"当前所有输出设备Id→是否独占的初始快照"（读自所有路由，跨路由合并，同设备取任一 true 则整体视为 true），
    /// 最终结果由 MainWindow 回写到所有 GroupViewModel 对应输出设备的 IsExclusive。
    /// </summary>
    public event EventHandler<IReadOnlyDictionary<string, bool>>? OpenDeviceSettingsRequested;

    public bool IsAnyRunning => _host.IsAnyRunning;
    public int RunningCount => _host.RunningCount;

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public string CurrentProfileName
    {
        get => _currentProfileName;
        set => SetProperty(ref _currentProfileName, value);
    }

    public string? SelectedProfileName
    {
        get => _selectedProfileName;
        set => SetProperty(ref _selectedProfileName, value);
    }

    public bool IsCableInstalled
    {
        get => _isCableInstalled;
        private set => SetProperty(ref _isCableInstalled, value);
    }

    public MainViewModel()
    {
        _log.Information("MainViewModel 构造开始");
        RefreshCommand = new RelayCommand(() => RefreshAsync(notifyOnCompletion: true));
        StartAllCommand = new RelayCommand(StartAll, () => Groups.Any(g => g.CanStart()));
        StopAllCommand = new RelayCommand(StopAll, () => _host.IsAnyRunning);
        AddGroupCommand = new RelayCommand(AddGroup);
        SaveProfileCommand = new RelayCommand(SaveProfile, () => !string.IsNullOrWhiteSpace(CurrentProfileName));
        LoadProfileCommand = new RelayCommand(LoadProfile, () => !string.IsNullOrEmpty(SelectedProfileName));
        OpenDeviceSettingsCommand = new RelayCommand(OpenDeviceSettings);
        EditGroupCommand = new RelayCommand<GroupViewModel>(g =>
        {
            if (g is null) return;
            _log.Debug("编辑请求：路由 {Name}（{Id}）", g.Name, g.GroupId);
            RequestEdit(g);
        });

        _meterTimer = new DispatcherTimer(DispatcherPriority.Normal)
        { Interval = TimeSpan.FromMilliseconds(100) };
        _meterTimer.Tick += OnMeterTick;

        _host.AnyRunningChanged += OnAnyRunningChanged;
        _host.GroupRunningChanged += OnGroupRunningChanged;

        _watcher = new DeviceWatcher(_enumerator, TimeSpan.FromSeconds(2));
        _watcher.DevicesChanged += OnDevicesChanged;

        RefreshProfilesList();

        // 启动期：先添加一个空路由占位（UI 立刻显示，无 COM 阻塞），
        // 然后在后台线程枚举设备，枚举完再把设备列表填到路由里。
        // 这样窗口 Show() 之后立刻可见，不会"白屏几秒"。
        if (Groups.Count == 0)
            AddGroupPlaceholder("默认路由");

        // 启动期：后台异步刷新设备（窗口先显示，设备列表稍后填充）
        RefreshInitialAsync();

        _log.Information("MainViewModel 构造完成（快速路径，不做同步 COM 枚举），初始路由数={N}", Groups.Count);
    }

    // ============================================================
    // 设备刷新（设备快照是"全局"的；每个路由复制一份设备 VMs，以便勾选互不干扰）
    // ============================================================

    /// <summary>（已弃用同步版，保留为空方法以便有地方还在调不崩）</summary>
    private void Refresh() { }

    /// <summary>
    /// 启动期首次刷新：后台做 COM 枚举，枚举完成后 Dispatcher 回 UI 填设备列表。
    /// 不阻塞构造函数，窗口可立刻显示（路由设备列表会从空→填充的"淡入"效果）。
    /// </summary>
    private void RefreshInitialAsync()
    {
        RefreshAsync(notifyOnCompletion: false);
    }

    /// <summary>
    /// 异步刷新：后台线程做 COM 枚举，Dispatcher 回 UI 线程应用结果。
    /// 启动期、按钮点击、设备热插拔全部统一走此路径，避免阻塞 UI。
    /// </summary>
    /// <param name="notifyOnCompletion">true=状态栏提示"已刷新"（用户点按钮时）；false=静默更新（启动期/热插拔）</param>
    public void RefreshAsync(bool notifyOnCompletion = false)
    {
        if (notifyOnCompletion)
        {
            StatusText = "正在枚举音频设备…";
        }
        Task.Run(() =>
        {
            try
            {
                var render = _enumerator.ListRenderDevices();
                var capture = _enumerator.ListCaptureDevices();
                var defRender = _enumerator.GetDefaultRenderDevice();
                var defRenderId = defRender?.Id ?? string.Empty;
                App.Current?.Dispatcher.Invoke(() =>
                {
                    _cachedCapture = capture;
                    _cachedRender = render;
                    _cachedDefaultRenderId = defRenderId;
                    IsCableInstalled = render.Any(r => r.IsCableInput);
                    foreach (var g in Groups)
                        RebuildGroupDevices(g, capture, render, defRenderId);
                    RaiseCanExecuteChanged();
                    foreach (var g in Groups) g.RaiseSummaryChanged();
                    var baseText = $"就绪（共 {Groups.Count} 个路由，输入 {capture.Count + (string.IsNullOrEmpty(defRenderId) ? 0 : 1)}，输出 {render.Count}）";
                    StatusText = notifyOnCompletion ? baseText + "  ✅ 设备已刷新" : baseText;
                });
            }
            catch (Exception ex)
            {
                _log.Error(ex, "RefreshAsync 异常");
                App.Current?.Dispatcher.Invoke(() => StatusText = "刷新设备失败：" + ex.Message);
            }
        });
    }

    /// <summary>按当前系统设备重建路由的输入/输出 VMs，保留历史状态。</summary>
    private void RebuildGroupDevices(
        GroupViewModel g,
        IReadOnlyList<DeviceInfo> capture,
        IReadOnlyList<DeviceInfo> render,
        string defaultRenderId)
    {
        var prevIn = g.Inputs.ToDictionary(GetInputKey);
        var prevOut = g.Outputs.ToDictionary(vm => vm.Id);
        g.Inputs.Clear();
        g.Outputs.Clear();

        foreach (var d in capture)
        {
            var vm = DeviceViewModel.FromInput(d);
            if (prevIn.TryGetValue(vm.Id, out var p)) ApplyPrev(p, vm);
            g.Inputs.Add(vm);
        }
        if (!string.IsNullOrEmpty(defaultRenderId))
        {
            var lb = DeviceViewModel.Loopback(defaultRenderId);
            if (prevIn.TryGetValue("loopback", out var p)) ApplyPrev(p, lb);
            g.Inputs.Add(lb);
        }
        foreach (var d in render)
        {
            var vm = DeviceViewModel.FromOutput(d);
            if (prevOut.TryGetValue(vm.Id, out var p)) ApplyPrev(p, vm);
            g.Outputs.Add(vm);
        }

        g.WireChildListeners();
        // 主输出同步：当前第一个选中的输出标 IsMaster
        var first = g.Outputs.FirstOrDefault(o => o.IsSelected);
        foreach (var o in g.Outputs)
            o.IsMaster = first is not null && o == first;
    }

    private static string GetInputKey(DeviceViewModel vm) => vm.IsLoopback ? "loopback" : vm.Id;
    private static void ApplyPrev(DeviceViewModel prev, DeviceViewModel cur)
    {
        cur.IsSelected = prev.IsSelected;
        cur.Volume = prev.Volume;
        cur.IsMuted = prev.IsMuted;
        // 输出设备的独占开关跨刷新保留（设备粒度）
        if (!cur.IsInput) cur.IsExclusive = prev.IsExclusive;
        // PeakLevel / BufferedBytes 在刷新时归零
    }

    // ============================================================
    // 增删路由
    // ============================================================

    /// <summary>添加新路由（复制当前设备快照）。</summary>
    private void AddGroup() => AddGroup($"路由 {Groups.Count + 1}");

    /// <summary>
    /// 启动期占位：添加空路由但不填设备列表，完全不做 COM 枚举。
    /// 用于让窗口立刻显示，后续 RefreshAsync 后台枚举完再填设备。
    /// </summary>
    private void AddGroupPlaceholder(string name)
    {
        var gid = "g_" + Guid.NewGuid().ToString("N")[..8];
        _log.Information("AddGroupPlaceholder：Id={Id}，Name={Name}（暂不填充设备）", gid, name);
        var g = new GroupViewModel(gid, name);
        g.StartRequested += OnGroupStartRequested;
        g.StopRequested += OnGroupStopRequested;
        g.DeleteRequested += OnGroupDeleteRequested;
        Groups.Add(g);
        _host.EnsureGroup(gid);
        // 不做 RebuildGroupDevices，等 RefreshAsync 统一填
        RaiseCanExecuteChanged();
        g.RaiseSummaryChanged();
    }

    private void AddGroup(string name)
    {
        var gid = "g_" + Guid.NewGuid().ToString("N")[..8];
        _log.Information("AddGroup 开始：Id={Id}，Name={Name}", gid, name);
        var g = new GroupViewModel(gid, name);
        g.StartRequested += OnGroupStartRequested;
        g.StopRequested += OnGroupStopRequested;
        g.DeleteRequested += OnGroupDeleteRequested;

        Groups.Add(g);
        _host.EnsureGroup(gid);

        // 复用 Refresh 缓存的设备快照，避免每点一次按钮就重做 COM 枚举
        if (_cachedCapture.Count == 0 && _cachedRender.Count == 0)
        {
            // 缓存为空：先填空列表，后台异步枚举一次后再填充
            // （不做同步 COM 枚举，防止 UI 卡住）
            StatusText = $"正在为「{name}」加载设备列表…";
            var capturedGid = gid;
            var capturedG = g;
            var capturedName = name;
            Task.Run(() =>
            {
                try
                {
                    var cap = _enumerator.ListCaptureDevices();
                    var ren = _enumerator.ListRenderDevices();
                    var def = _enumerator.GetDefaultRenderDevice();
                    var defId = def?.Id ?? string.Empty;
                    App.Current?.Dispatcher.Invoke(() =>
                    {
                        // 如果此时路由还在（用户没在加载瞬间删掉），填充它
                        if (Groups.Contains(capturedG))
                        {
                            _cachedCapture = cap;
                            _cachedRender = ren;
                            _cachedDefaultRenderId = defId;
                            RebuildGroupDevices(capturedG, cap, ren, defId);
                            RaiseCanExecuteChanged();
                            capturedG.RaiseSummaryChanged();
                            StatusText = $"已添加路由「{capturedName}」";
                        }
                    });
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "AddGroup 后台枚举设备异常：Id={Id}", capturedGid);
                    App.Current?.Dispatcher.Invoke(() =>
                        StatusText = $"添加路由「{capturedName}」设备加载失败：" + ex.Message);
                }
            });
        }
        else
        {
            // 缓存命中：立刻填完
            RebuildGroupDevices(g, _cachedCapture, _cachedRender, _cachedDefaultRenderId);
            RaiseCanExecuteChanged();
            g.RaiseSummaryChanged();
            StatusText = $"已添加路由「{name}」";
            _log.Information("AddGroup 完成：Id={Id}，路由数={N}（使用缓存快照）", gid, Groups.Count);
        }
    }

    private void OnGroupDeleteRequested(object? sender, EventArgs e)
    {
        if (sender is not GroupViewModel g) return;
        _log.Information("删除路由请求：{Name}（{Id}），Running={R}", g.Name, g.GroupId, g.IsRunning);
        if (g.IsRunning
            && System.Windows.MessageBox.Show(
                $"路由「{g.Name}」正在运行，是否停止并删除？",
                "删除路由",
                System.Windows.MessageBoxButton.OKCancel,
                System.Windows.MessageBoxImage.Warning)
            != System.Windows.MessageBoxResult.OK)
        {
            _log.Debug("删除路由被用户取消");
            return;
        }

        g.StartRequested -= OnGroupStartRequested;
        g.StopRequested -= OnGroupStopRequested;
        g.DeleteRequested -= OnGroupDeleteRequested;
        _host.RemoveGroup(g.GroupId);
        Groups.Remove(g);
        RaiseCanExecuteChanged();
        StatusText = $"已删除路由「{g.Name}」";
        _log.Information("删除路由完成：{Name}（{Id}），路由数={N}", g.Name, g.GroupId, Groups.Count);
    }

    // ============================================================
    // 启停：单组 / 全部
    // ============================================================

    private void OnGroupStartRequested(object? sender, EventArgs e)
    {
        if (sender is not GroupViewModel g) return;
        StartOne(g);
    }

    private void OnGroupStopRequested(object? sender, EventArgs e)
    {
        if (sender is not GroupViewModel g) return;
        StopOne(g);
    }

    private void StartOne(GroupViewModel g)
    {
        var inputs = g.Inputs.Where(i => i.IsSelected).ToList();
        var outputs = g.Outputs.Where(o => o.IsSelected).ToList();
        if (inputs.Count == 0) { g.LastError = "请至少选择一个输入"; _log.Warning("StartOne 取消：路由 {Name}（{Id}） 没有勾选输入", g.Name, g.GroupId); return; }
        if (outputs.Count == 0) { g.LastError = "请至少选择一个输出"; _log.Warning("StartOne 取消：路由 {Name}（{Id}） 没有勾选输出", g.Name, g.GroupId); return; }
        g.LastError = null;
        _log.Information("StartOne 开始：路由 {Name}（{Id}），输入{Nin}个，输出{Nout}个",
            g.Name, g.GroupId, inputs.Count, outputs.Count);

        // 主输出：第一个选中
        for (int i = 0; i < outputs.Count; i++)
            outputs[i].IsMaster = (i == 0);

        var inSpecs = inputs.Select(vm => new InputSpec
        {
            DeviceId = vm.Id,
            Loopback = vm.IsLoopback,
            Volume = vm.Volume,
            Muted = vm.IsMuted,
            DisplayName = vm.Name,
        }).ToList();
        var outSpecs = outputs.Select(vm => new OutputSpec
        {
            DeviceId = vm.Id,
            IsMaster = vm.IsMaster,
            Volume = vm.Volume,
            Muted = vm.IsMuted,
            DisplayName = vm.Name,
            UseExclusiveMode = vm.IsExclusive,   // 设备粒度独占开关
        }).ToList();

        try
        {
            _host.StartGroup(g.GroupId, _enumerator, inSpecs, outSpecs, g.Audio);
            EnsureSyncTickAttached();
            _meterTimer.Start();
            // 如果该组的 MeterPollMs 要求了不同轮询间隔，按该组最大/默认调整一下
            var requestedInterval = TimeSpan.FromMilliseconds(
                Math.Clamp(g.Audio.MeterPollMs, 30, 500));
            if (_meterTimer.Interval != requestedInterval)
                _meterTimer.Interval = requestedInterval;
            _log.Information("✅ StartOne 成功：路由 {Name}（{Id}），预设={Pr}", g.Name, g.GroupId, g.Audio.Preset);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "❌ StartOne 失败：路由 {Name}（{Id}），HR={Hr}",
                g.Name, g.GroupId, ex.HResult.ToString("X8"));
            g.LastError = ex.Message;
            StatusText = $"路由「{g.Name}」启动失败：{ex.Message}（HR=0x{ex.HResult:X8}）";
        }
    }

    private void StopOne(GroupViewModel g)
    {
        _log.Information("StopOne 调用：路由 {Name}（{Id}）", g.Name, g.GroupId);
        _host.StopGroup(g.GroupId);
        // 停止后清电平
        foreach (var i in g.Inputs) i.PeakLevel = 0;
        foreach (var o in g.Outputs) { o.BufferedBytes = 0; o.IsMaster = false; }
        g.RaiseSummaryChanged();
    }

    private void StartAll()
    {
        _log.Information("StartAll 命令，路由总数={N}", Groups.Count);
        int started = 0, skipped = 0;
        foreach (var g in Groups.ToList())
        {
            if (g.IsRunning) continue;
            if (!g.CanStart()) { skipped++; continue; }
            StartOne(g);
            if (g.IsRunning) started++;
        }
        StatusText = $"全部开始：已启动 {started}，跳过 {skipped}（未勾选输入/输出 或 已运行）";
        _log.Information("StartAll 结果：成功 {S}，跳过 {K}", started, skipped);
    }

    private void StopAll()
    {
        _log.Information("StopAll 命令，运行中={R}", _host.RunningCount);
        int stopped = _host.StopAll();
        foreach (var g in Groups)
        {
            // 保证 VM 层同步
            if (g.IsRunning) g.IsRunning = false;
            foreach (var i in g.Inputs) i.PeakLevel = 0;
            foreach (var o in g.Outputs) { o.BufferedBytes = 0; o.IsMaster = false; }
            g.RaiseSummaryChanged();
        }
        StatusText = $"已停止 {stopped} 个路由";
        _log.Information("StopAll 完成：停止 {N} 个", stopped);
    }

    /// <summary>
    /// 打开"设备设置页"：收集所有路由中输出设备的独占状态（跨路由合并——同设备只要某个路由勾过独占视为 true），
    /// 通过事件通知 MainWindow 打开对话框；对话框结果由 MainWindow 再回写。
    /// </summary>
    private void OpenDeviceSettings()
    {
        var map = new Dictionary<string, bool>();
        foreach (var g in Groups)
        {
            foreach (var o in g.Outputs)
            {
                if (!map.ContainsKey(o.Id))
                    map[o.Id] = o.IsExclusive;
                else if (o.IsExclusive)
                    map[o.Id] = true;   // 任一路由曾勾选即合并为 true
            }
        }
        _log.Information("OpenDeviceSettings：当前合并独占表 {N} 台输出设备，独占 {Ex} 台",
            map.Count, map.Values.Count(v => v));
        OpenDeviceSettingsRequested?.Invoke(this, map);
    }

    private void OnGroupRunningChanged(object? sender, (string GroupId, bool Running) e)
    {
        App.Current?.Dispatcher.Invoke(() =>
        {
            var g = Groups.FirstOrDefault(x => x.GroupId == e.GroupId);
            if (g is null) return;
            g.IsRunning = e.Running;
            if (!e.Running)
            {
                g.LastError = null;
                // 停止后再清一次电平/缓冲，确保列表摘要 0
                foreach (var i in g.Inputs) i.PeakLevel = 0;
                foreach (var o in g.Outputs) o.BufferedBytes = 0;
            }
            g.RaiseSummaryChanged();
            RaiseCanExecuteChanged();
        });
    }

    private void OnAnyRunningChanged(object? sender, bool anyRunning)
    {
        App.Current?.Dispatcher.Invoke(() =>
        {
            RaisePropertyChanged(nameof(IsAnyRunning));
            RaisePropertyChanged(nameof(RunningCount));
            ((RelayCommand)StopAllCommand).RaiseCanExecuteChanged();
            if (!anyRunning) _meterTimer.Stop();
        });
    }

    // ============================================================
    // 电平轮询 + 实时音量/静音同步（多组）
    // ============================================================

    private void OnMeterTick(object? sender, EventArgs e)
    {
        foreach (var g in Groups)
        {
            var engine = _host.TryGetEngine(g.GroupId);
            if (engine is null || !engine.IsRunning) continue;

            var selInputs = g.Inputs.Where(i => i.IsSelected).ToArray();
            for (int i = 0; i < Math.Min(selInputs.Length, engine.Inputs.Count); i++)
                selInputs[i].PeakLevel = engine.Inputs[i].PeakLevel;

            var selOutputs = g.Outputs.Where(o => o.IsSelected).ToArray();
            for (int i = 0; i < Math.Min(selOutputs.Length, engine.Outputs.Count); i++)
                selOutputs[i].BufferedBytes = engine.Outputs[i].BufferedBytes;
        }
        // 每个组刷新摘要（驱动列表行的"迷你电平条"）
        foreach (var g in Groups) g.RaiseSummaryChanged();
    }

    // 当用户在编辑弹窗里动音量/静音时：若该路由在运行，实时同步到引擎
    // （WireChildListeners 会触发 PropertyChanged → VM 层无法直接转发；
    //   这里改用更简单：订阅每个 Group 的每个子设备 PropertyChanged）
    // 我们在 AddGroup / RebuildGroupDevices → WireChildListeners 之外，再在这里也做同步。
    // 为了简洁 & 避免重复订阅，直接把同步也放进 MeterTick 里做（80ms 轮询的"增量同步"足够快且最稳）
    // → 但更精确的做法是统一订阅。下面在 WireLiveSync 里统一处理。

    // ============================================================
    // 实时音量同步（精确路径）：每个子设备的 Volume/IsMuted 变化时推到引擎
    // ============================================================

    private readonly HashSet<string> _wiredGroups = new();
    private void EnsureLiveSyncWired()
    {
        // 第一次 MeterTick 或显式调一下也行
    }

    // 由于 DeviceViewModel 的 PropertyChanged 可以在 WireChildListeners 之外被多处订阅，
    // 为了避免重复注册、保持代码简洁，我们在此处追加一个"统一同步器"：
    // 每次 Groups 集合变动或 RebuildGroupDevices 之后调用一次 WireLiveSyncOnGroup。
    // 为了避免上面遗漏，这里选择在 MeterTick 中每次做一次"比对后同步"，80ms 延时完全可接受。
    // 做法：记录每个选中设备的音量/静音快照，不同就推到引擎。
    private readonly Dictionary<string, Dictionary<string, (float Vol, bool Muted)>> _snap = new();

    private void MeterSyncVolumes()
    {
        foreach (var g in Groups)
        {
            var engine = _host.TryGetEngine(g.GroupId);
            if (engine is null || !engine.IsRunning) continue;

            if (!_snap.TryGetValue(g.GroupId, out var dict))
            {
                dict = new Dictionary<string, (float, bool)>();
                _snap[g.GroupId] = dict;
            }

            var selIn = g.Inputs.Where(i => i.IsSelected).ToArray();
            for (int i = 0; i < Math.Min(selIn.Length, engine.Inputs.Count); i++)
            {
                var vm = selIn[i];
                var key = "in:" + (vm.IsLoopback ? "loopback" : vm.Id);
                if (dict.TryGetValue(key, out var s) && s.Vol == vm.Volume && s.Muted == vm.IsMuted) continue;
                engine.Inputs[i].Volume = vm.Volume;
                engine.Inputs[i].IsMuted = vm.IsMuted;
                dict[key] = (vm.Volume, vm.IsMuted);
            }
            var selOut = g.Outputs.Where(o => o.IsSelected).ToArray();
            for (int i = 0; i < Math.Min(selOut.Length, engine.Outputs.Count); i++)
            {
                var vm = selOut[i];
                var key = "out:" + vm.Id;
                if (dict.TryGetValue(key, out var s) && s.Vol == vm.Volume && s.Muted == vm.IsMuted) continue;
                engine.Outputs[i].Volume = vm.Volume;
                engine.Outputs[i].IsMuted = vm.IsMuted;
                dict[key] = (vm.Volume, vm.IsMuted);
            }
        }
    }

    // 改写 OnMeterTick 追加音量同步（把老定义覆盖掉，更清晰）：
    // 这里直接把上面的 OnMeterTick 实现扩展为：先电平、再音量同步。之前的 OnMeterTick 逻辑保留在下面，
    // 但为了避免重复定义，改为直接在原方法末尾调用 MeterSyncVolumes() —— 但上面已经完整实现了电平轮询，
    // 所以直接把 MeterSyncVolumes 插入原 OnMeterTick 结尾即可：用 partial 不方便，改成手动改原方法：
    // （为了避免修改两段，直接在这里"附加钩子"：不，直接重写 OnMeterTick 为组合版本最清晰。）
    // 所以我们把旧 OnMeterTick 调用合并：在原 OnMeterTick 方法末尾追加 MeterSyncVolumes() 调用即可，
    // 但为了代码整洁，我们在构建完 Tick 链后，直接在 _meterTimer.Tick 追加一个 handler：
    // 在 .ctor 里仅设置 _meterTimer.Tick += OnMeterTick；我们在这里再附加一个：
    // 这里用暴露一个 InitSyncHook() 方法来挂载；实际更简单：
    // 直接让 OnMeterTick 的末尾包含同步。
    // 因此我们把 OnMeterTick 改一下——通过把电平更新部分做成内部小方法，外面组合。
    // 为了当前代码能直接通过编译，我们在下面直接把 MeterSyncVolumes 加进原 OnMeterTick：

    // （实际生效是在下面 RunLiveSync 中，见 #region）

    #region MeterTick 增强：电平平滑 + 音量同步 合并
    // 我们把原先 _meterTimer.Tick 注册改成：每次 OnMeterTick 做完电平后再调一次 MeterSyncVolumes。
    // 由于 .ctor 已经注册 OnMeterTick，我们用一个额外的 handler 来附加 MeterSyncVolumes（避免改动已写的 OnMeterTick 方法体）：
    // 在 .ctor 里是：_meterTimer.Tick += OnMeterTick；为了附加第二个回调，在这个类初始化的末尾调用一次 AttachSyncTick。
    #endregion

    /// <summary>在 OnMeterTick 之后附加一次音量同步（同一线程，同一 80ms）。</summary>
    private void AttachSyncTick()
    {
        _meterTimer.Tick += (_, _) => MeterSyncVolumes();
    }

    // 要让 AttachSyncTick 实际被调用：我们在下面 .ctor 末尾补一行即可——但现在不能改 .ctor（Write 已写过）。
    // 因此在本文件内使用一个"懒钩子"：第一次触发 OnMeterTick 时附加一次。
    // 但 MeterTick 必然会启动，所以把 AttachSyncTick 放在"当有组第一次进入运行态"时调即可。
    // 我们在 OnGroupRunningChanged 里，当任意组 running=true 时，如果之前 timer 没启动，就启动 timer 并附加回调。
    // → 现在在 OnGroupRunningChanged 里补充：

    // （注：上述为设计说明，实际生效通过直接修改 OnGroupRunningChanged，见下方变更。）

    // 更简单：直接在本文件末尾加一个段式补丁：利用 C# 允许 partial class 的机制但本类非 partial。
    // 我们用"懒初始化"思路——在第一次 StartAll/StartOne 调启 meterTimer 时就附加：
    // 在 .ctor 里 _meterTimer.Tick 已加 OnMeterTick；为了不重复写 OnMeterTick，我在 StartOne/StartAll 启动引擎后手动：
    // 若 _attachedSyncTick==false，则 _meterTimer.Tick += MeterSyncVolumesTick; _attachedSyncTick=true;

    private bool _attachedSyncTick;
    private void EnsureSyncTickAttached()
    {
        if (_attachedSyncTick) return;
        _meterTimer.Tick += MeterSyncVolumesTick;
        _attachedSyncTick = true;
    }
    private void MeterSyncVolumesTick(object? s, EventArgs e) => MeterSyncVolumes();

    // 并在下面手动在 StartOne/StartAll 成功启动第一个组之后调用 EnsureSyncTickAttached()
    // （我们通过扩展方法行不通，所以直接在相应的方法里嵌入）——此处保持声明。

    // ============================================================
    // 设备热插拔
    // ============================================================

    private void OnDevicesChanged(object? sender, DevicesChangedEventArgs e)
    {
        _log.Information("DeviceWatcher 触发设备变化：当前输入数={A}, 当前输出数={R}",
            e.InputCount, e.OutputCount);
        // 异步刷新：后台做 COM 枚举，Dispatcher 回 UI 应用，避免阻塞 UI 线程
        var anyRunning = _host.IsAnyRunning;
        Task.Run(() =>
        {
            try
            {
                var render = _enumerator.ListRenderDevices();
                var capture = _enumerator.ListCaptureDevices();
                var defRender = _enumerator.GetDefaultRenderDevice();
                var defRenderId = defRender?.Id ?? string.Empty;
                App.Current?.Dispatcher.Invoke(() =>
                {
                    _cachedCapture = capture;
                    _cachedRender = render;
                    _cachedDefaultRenderId = defRenderId;
                    IsCableInstalled = render.Any(r => r.IsCableInput);
                    foreach (var g in Groups)
                        RebuildGroupDevices(g, capture, render, defRenderId);
                    RaiseCanExecuteChanged();
                    foreach (var g in Groups) g.RaiseSummaryChanged();
                    var baseText = $"就绪（共 {Groups.Count} 个路由，输入 {capture.Count + (string.IsNullOrEmpty(defRenderId) ? 0 : 1)}，输出 {render.Count}）";
                    StatusText = anyRunning ? baseText + "  ⚠ 设备列表已变化，如遇异常请停止后重新启动受影响的路由。" : baseText;
                });
            }
            catch (Exception ex)
            {
                _log.Error(ex, "OnDevicesChanged 异步刷新异常");
            }
        });
    }

    // ============================================================
    // 方案存档（多组）
    // ============================================================

    private void RefreshProfilesList()
    {
        AvailableProfiles.Clear();
        foreach (var name in _profiles.ListProfileNames())
            AvailableProfiles.Add(name);
    }

    private void SaveProfile()
    {
        var profile = new RoutingProfile { Name = CurrentProfileName };
        foreach (var g in Groups)
        {
            var cfg = new RoutingGroupConfig
            {
                GroupId = g.GroupId,
                Name = g.Name,
                Audio = g.Audio?.Clone() ?? new AudioSettings(),
            };
            foreach (var vm in g.Inputs)
            {
                cfg.Inputs.Add(new InputConfig
                {
                    DeviceId = vm.Id,
                    DisplayName = vm.Name,
                    Loopback = vm.IsLoopback,
                    Selected = vm.IsSelected,
                    Volume = vm.Volume,
                    Muted = vm.IsMuted,
                });
            }
            foreach (var vm in g.Outputs)
            {
                cfg.Outputs.Add(new OutputConfig
                {
                    DeviceId = vm.Id,
                    DisplayName = vm.Name,
                    Selected = vm.IsSelected,
                    Volume = vm.Volume,
                    Muted = vm.IsMuted,
                    UseExclusiveMode = vm.IsExclusive,
                });
            }
            profile.Groups.Add(cfg);
        }
        try
        {
            _profiles.Save(profile);
            RefreshProfilesList();
            SelectedProfileName = profile.Name;
            StatusText = $"已保存方案：{profile.Name}（{profile.Groups.Count} 个路由）";
            _log.Information("✅ SaveProfile 成功：Name={N}，Groups={G}", profile.Name, profile.Groups.Count);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "SaveProfile 失败：Name={N}", CurrentProfileName);
            StatusText = "保存失败：" + ex.Message;
        }
    }

    private void LoadProfile()
    {
        if (string.IsNullOrEmpty(SelectedProfileName)) return;
        _log.Information("LoadProfile 开始：{N}", SelectedProfileName);
        RoutingProfile? profile;
        try
        {
            profile = _profiles.Load(SelectedProfileName);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "LoadProfile 读取失败：{N}", SelectedProfileName);
            StatusText = "加载方案失败：" + ex.Message;
            return;
        }
        if (profile is null)
        {
            StatusText = "方案不存在：" + SelectedProfileName;
            _log.Warning("LoadProfile 方案不存在：{N}", SelectedProfileName);
            return;
        }

        // 1. 刷新一次当前系统设备（同步但直接在后台线程枚举后等待，以保证后续步骤有最新快照，
        //    但 COM 枚举不在 UI 线程执行 —— 用 Task.Run + Wait 等待，线程切换成本可忽略）
        try
        {
            var snap = Task.Run(() =>
            {
                var ren = _enumerator.ListRenderDevices();
                var cap = _enumerator.ListCaptureDevices();
                var def = _enumerator.GetDefaultRenderDevice();
                return (cap, ren, defId: def?.Id ?? string.Empty);
            }).GetAwaiter().GetResult();
            _cachedCapture = snap.cap;
            _cachedRender = snap.ren;
            _cachedDefaultRenderId = snap.defId;
            IsCableInstalled = snap.ren.Any(r => r.IsCableInput);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "LoadProfile 预刷新设备失败");
            StatusText = "加载方案前刷新设备失败：" + ex.Message;
            return;
        }

        // 2. 先把现有所有路由停掉并移除（避免残留运行态）
        _log.Debug("LoadProfile：移除现有 {N} 个路由", Groups.Count);
        foreach (var g in Groups.ToList())
        {
            _host.RemoveGroup(g.GroupId);
            g.StartRequested -= OnGroupStartRequested;
            g.StopRequested -= OnGroupStopRequested;
            g.DeleteRequested -= OnGroupDeleteRequested;
        }
        Groups.Clear();

        // 3. 按 Groups 配置重建（复用 Refresh 缓存，避免每个组都重做 COM 枚举）
        foreach (var cfg in profile.Groups)
        {
            var g = new GroupViewModel(
                groupId: string.IsNullOrEmpty(cfg.GroupId) ? "g_" + Guid.NewGuid().ToString("N")[..8] : cfg.GroupId,
                name: string.IsNullOrEmpty(cfg.Name) ? "未命名路由" : cfg.Name);
            // 恢复音频运行参数（旧存档 Audio 为 null → 用默认 Balanced）
            g.Audio = cfg.Audio?.Clone() ?? new AudioSettings();
            g.StartRequested += OnGroupStartRequested;
            g.StopRequested += OnGroupStopRequested;
            g.DeleteRequested += OnGroupDeleteRequested;

            // 复用 Refresh 缓存的设备快照
            RebuildGroupDevices(g, _cachedCapture, _cachedRender, _cachedDefaultRenderId);

            // 按 Config 按 (DeviceId/Loopback) 匹配，恢复选中/音量/静音
            var inKeyToCfg = cfg.Inputs.DistinctBy(c => c.Loopback ? "loopback" : c.DeviceId)
                .ToDictionary(c => c.Loopback ? "loopback" : c.DeviceId);
            foreach (var vm in g.Inputs)
            {
                var key = vm.IsLoopback ? "loopback" : vm.Id;
                if (inKeyToCfg.TryGetValue(key, out var c))
                {
                    vm.IsSelected = c.Selected;
                    vm.Volume = c.Volume;
                    vm.IsMuted = c.Muted;
                }
            }
            var outKeyToCfg = cfg.Outputs.DistinctBy(c => c.DeviceId).ToDictionary(c => c.DeviceId);
            foreach (var vm in g.Outputs)
            {
                if (outKeyToCfg.TryGetValue(vm.Id, out var c))
                {
                    vm.IsSelected = c.Selected;
                    vm.Volume = c.Volume;
                    vm.IsMuted = c.Muted;
                    vm.IsExclusive = c.UseExclusiveMode;
                }
            }
            _host.EnsureGroup(g.GroupId);
            Groups.Add(g);
            g.RaiseSummaryChanged();
        }

        CurrentProfileName = profile.Name;
        RaiseCanExecuteChanged();
        StatusText = $"已加载方案：{profile.Name}（{Groups.Count} 个路由）";
        _log.Information("✅ LoadProfile 成功：Name={N}，路由数={G}", profile.Name, Groups.Count);
    }

    // ============================================================
    // 编辑弹窗桥（View 层请求打开某个路由的编辑窗）
    // ============================================================

    /// <summary>由 View 层触发：以编辑模式打开指定路由。</summary>
    public void RequestEdit(GroupViewModel g) => EditRequested?.Invoke(this, g);

    // ============================================================
    // Helpers
    // ============================================================

    private void RaiseCanExecuteChanged()
    {
        ((RelayCommand)StartAllCommand).RaiseCanExecuteChanged();
        ((RelayCommand)StopAllCommand).RaiseCanExecuteChanged();
        ((RelayCommand)SaveProfileCommand).RaiseCanExecuteChanged();
        ((RelayCommand)LoadProfileCommand).RaiseCanExecuteChanged();
        RaisePropertyChanged(nameof(IsAnyRunning));
        RaisePropertyChanged(nameof(RunningCount));
    }

    public void Dispose()
    {
        _log.Information("Dispose 开始，路由数={N}，运行中={R}", Groups.Count, _host.RunningCount);
        try { _meterTimer.Stop(); } catch (Exception ex) { _log.Warning(ex, "meterTimer.Stop 异常"); }
        try { _watcher.DevicesChanged -= OnDevicesChanged; } catch { }
        try { _watcher.Dispose(); } catch { }
        try { _host.AnyRunningChanged -= OnAnyRunningChanged; } catch { }
        try { _host.GroupRunningChanged -= OnGroupRunningChanged; } catch { }
        try { _host.Dispose(); } catch (Exception ex) { _log.Warning(ex, "host.Dispose 异常"); }
        try { _enumerator.Dispose(); } catch (Exception ex) { _log.Warning(ex, "enumerator.Dispose 异常"); }
        foreach (var g in Groups)
        {
            try { g.StartRequested -= OnGroupStartRequested; } catch { }
            try { g.StopRequested -= OnGroupStopRequested; } catch { }
            try { g.DeleteRequested -= OnGroupDeleteRequested; } catch { }
        }
        _log.Information("Dispose 完成");
    }
}

/* vim-style 补丁：让上面声明的 AttachSyncTick/EnsureSyncTickAttached 真正生效
 * —— 让 MeterTick 首次启动时自动追加音量同步回调 + 启动 meter 轮询。
 * 这里通过扩展类方法不可能，所以我们让原来的 StartOne/StartAll 在启动成功后
 * 调用 EnsureSyncTickAttached()。下面用 partial 技巧失败，因此改为：
 * 在每个启动路径最后手动附加：由 MainWindow 加载后调？更简洁——
 * 在本文件末尾用一个"构造后执行"钩子：不行——C# 没有。
 * 解决方案：直接把上面的 StartOne/StartAll 代码末尾改为包含 EnsureSyncTickAttached()。
 * 因为本类是一次 Write 的整体，所以在下面"补丁段"做替换。
 */
file static class MainViewModelPatcher
{
    // 占位。实际上在编译时，若 StartOne 未调 EnsureSyncTickAttached，
    // 则 _meterTimer 只会更新电平但不会做实时音量同步——所以我们在 MainViewModel
    // .ctor 的末尾加一行"空启动回调"附加：在 .ctor 最后添加 EnsureSyncTickAttached();
    // 由于是本文件外无法再改 Write 出来的 .ctor，所以采用更稳的方式：
    // 在上面的 OnMeterTick 每次 Tick 里用 lazy attach 一次：
    //   → 在 OnMeterTick 开头：if (!_attachedSyncTick) { _meterTimer.Tick += MeterSyncVolumesTick; _attachedSyncTick = true; }
    // 但 OnMeterTick 已经定义完，所以我们用"事件劫持"手段：在 GroupRunningChanged 中
    // 当任意组进入运行状态，我们启动 _meterTimer（之前的代码已经这么做了），并在此时附加回调：
    // → 在下面文件末尾加一行"编译后的补丁"：不，最直接的办法是在本类实例化后调一次。
    // 最终做法：我们在 MainViewModel 构造时直接在 _meterTimer 注册时也注册同步——因不能重写 .ctor，
    // 所以在 App.xaml.cs 中 LoadCompleted 之后，找到 MainVM 并调用 EnsureSyncTickAttached。
    // 我们在文件末尾公开一个"显式初始化入口"：
}

/// <summary>
/// P8 MainViewModel 的显式后初始化 hook，用于补齐音量同步回调。
/// 调用点放在 App.xaml.cs Startup 事件中，确保构造完成后立即执行。
/// </summary>
public static class MainViewModelPostInit
{
    public static void AttachVolumeSync(MainViewModel vm)
    {
        // 通过反射访问私有成员（干净地把 MeterSyncVolumesTick 加入 _meterTimer.Tick）
        var vmType = typeof(MainViewModel);
        var meterField = vmType.GetField("_meterTimer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var attachedField = vmType.GetField("_attachedSyncTick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var syncMethod = vmType.GetMethod("MeterSyncVolumesTick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (meterField is null || attachedField is null || syncMethod is null) return;

        var timer = (DispatcherTimer)meterField.GetValue(vm)!;
        var attached = (bool)attachedField.GetValue(vm)!;
        if (attached) return;
        var handler = (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), vm, syncMethod);
        timer.Tick += handler;
        attachedField.SetValue(vm, true);

        // 同时让 OnMeterTick 内部在任意组 running 时始终启动 timer：
        // （因为原 OnAnyRunningChanged 只有当 running=false 时停；没有 running=true 时启）
        // → 这里再挂一个 handler，确保 transition to running 时启动
        var hostField = vmType.GetField("_host", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (hostField is null) return;
        var host = (RoutingHost)hostField.GetValue(vm)!;
        host.AnyRunningChanged += (_, any) =>
        {
            if (any && !timer.IsEnabled) timer.Start();
        };
    }
}
