// Copyright (c) 2026 wdd. Licensed under PolyForm Noncommercial 1.0.0.
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MuiltVoice.App.ViewModels;
using MuiltVoice.Audio.Devices;
using MuiltVoice.Config.Models;
using Serilog;
using MuiltVoice.Audio;

namespace MuiltVoice.App;

/// <summary>
/// DeviceSettingsWindow.xaml：独立页面管理每个输出设备的 WASAPI 独占模式（设备粒度）。
/// 不与路由直接耦合：构造时传入"当前独占表"做快照（工作副本）；
/// 点击【完成】后，通过 <see cref="ResultMap"/> 返回最终结果供上层（MainWindow）
/// 回写到所有路由中对应设备的 DeviceViewModel.IsExclusive。
///
/// 性能优化：
/// - 构造函数不做 COM 枚举，窗口先 Show 骨架
/// - Loaded 事件中 Task.Run 后台枚举设备，显示 Loading 遮罩（♪ 旋转指示）
/// - 刷新按钮、确定/取消按钮在 Loading 期间禁用，防重复点击
/// </summary>
public partial class DeviceSettingsWindow : Window
{
    private readonly AudioDeviceEnumerator _enumerator;
    private IReadOnlyDictionary<string, bool> _pendingInitialMap;

    private readonly DispatcherTimer? _spinnerTimer;
    private double _angle;

    /// <summary>输入设备项（只读展示）。</summary>
    public ObservableCollection<DeviceExclusiveItem> CaptureItems { get; } = new();
    /// <summary>输出设备项（每个可调 🔓/🔒）。</summary>
    public ObservableCollection<DeviceExclusiveItem> RenderItems { get; } = new();

    /// <summary>
    /// 对话框结果：key=DeviceId, value=是否独占（只包含输出设备）。
    /// 【取消】返回 null；【完成】返回最终状态映射。
    /// </summary>
    public IReadOnlyDictionary<string, bool>? ResultMap { get; private set; }

    private readonly ILogger _log = AppLog.Log.ForContext<DeviceSettingsWindow>();
    private bool _isLoading;

    public DeviceSettingsWindow(
        AudioDeviceEnumerator enumerator,
        IReadOnlyDictionary<string, bool> currentExclusiveMap)
    {
        InitializeComponent();
        _enumerator = enumerator;
        _pendingInitialMap = currentExclusiveMap;

        // 启动旋转动画计时器（♪ 缓慢自转，直观显示正在加载）
        _spinnerTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
        _spinnerTimer.Tick += (_, _) =>
        {
            _angle = (_angle + 15) % 360;
            SpinnerRotate.Angle = _angle;
        };

        // 窗口 Loaded 后再开始枚举（保证 Show()/ShowDialog() 先出 UI）
        Loaded += OnWindowLoaded;
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnWindowLoaded;
        _ = LoadDevicesAsync(_pendingInitialMap, isRefresh: false);
    }

    /// <summary>
    /// 异步加载设备：后台枚举 → Dispatcher 填列表。
    /// </summary>
    /// <param name="initialMap">初始独占状态表（保留用户已勾选的）。</param>
    /// <param name="isRefresh">true=刷新按钮触发（显示不同提示）；false=首次打开。</param>
    private async Task LoadDevicesAsync(IReadOnlyDictionary<string, bool> initialMap, bool isRefresh)
    {
        if (_isLoading) return;
        _isLoading = true;
        SetLoadingUi(true, isRefresh ? "正在刷新设备列表…" : "正在枚举音频设备…",
            isRefresh ? "请稍候…" : "首次枚举可能需要 1-3 秒");
        try
        {
            // 后台线程做 COM 枚举（NAudio MMDeviceEnumerator.EnumerateAudioEndPoints 会阻塞）
            var snapshot = await Task.Run(() =>
            {
                var cap = _enumerator.ListCaptureDevices();
                var ren = _enumerator.ListRenderDevices();
                return (cap, ren);
            });

            // 以下在 UI 线程：填 ObservableCollection（绑定列表会刷新）
            CaptureItems.Clear();
            RenderItems.Clear();
            foreach (var d in snapshot.cap)
                CaptureItems.Add(DeviceExclusiveItem.From(d, false));
            foreach (var d in snapshot.ren)
            {
                bool ex = initialMap.TryGetValue(d.Id, out var b) && b;
                RenderItems.Add(DeviceExclusiveItem.From(d, ex));
            }

            // 订阅 PropertyChanged（用于更新统计徽章）
            foreach (var item in RenderItems)
                item.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(DeviceExclusiveItem.IsExclusive))
                        UpdateStats();
                };

            UpdateStats();
            _log.Information("DeviceSettingsWindow {Op}：输入 {Ni}，输出 {No}，初始独占 {Ex} 个",
                isRefresh ? "刷新" : "加载",
                CaptureItems.Count, RenderItems.Count, RenderItems.Count(i => i.IsExclusive));
        }
        catch (Exception ex)
        {
            _log.Error(ex, "DeviceSettingsWindow LoadDevicesAsync 异常（isRefresh={R}）", isRefresh);
            MessageBox.Show(this,
                "枚举音频设备失败：" + ex.Message,
                "设备设置",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            SetLoadingUi(false, null, null);
            _isLoading = false;
        }
    }

    /// <summary>
    /// 切换 Loading UI：
    /// - 遮罩显隐
    /// - 旋转计时器启停
    /// - 所有交互按钮（一键全部/刷新/取消/完成）禁用
    /// </summary>
    private void SetLoadingUi(bool loading, string? mainText, string? subText)
    {
        if (loading)
        {
            if (mainText != null) LoadingText.Text = mainText;
            if (subText != null) LoadingSubText.Text = subText;
            LoadingMask.Visibility = Visibility.Visible;
            _spinnerTimer?.Start();
            BtnAllShared.IsEnabled = false;
            BtnAllExclusive.IsEnabled = false;
            BtnRefresh.IsEnabled = false;
            BtnOk.IsEnabled = false;
            // Loading 时取消按钮仍可点，让用户能"不耐烦就关闭"
            BtnCancel.IsEnabled = true;
        }
        else
        {
            LoadingMask.Visibility = Visibility.Collapsed;
            _spinnerTimer?.Stop();
            BtnAllShared.IsEnabled = true;
            BtnAllExclusive.IsEnabled = true;
            BtnRefresh.IsEnabled = true;
            BtnOk.IsEnabled = true;
            BtnCancel.IsEnabled = true;
        }
    }

    private void UpdateStats()
    {
        int total = RenderItems.Count;
        int ex = RenderItems.Count(i => i.IsExclusive);
        TotalOutputCountText.Text = total.ToString();
        ExclusiveCountText.Text = ex.ToString();
    }

    private void AllShared_Click(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        foreach (var i in RenderItems) i.IsExclusive = false;
        _log.Debug("一键：全部共享");
    }

    private void AllExclusive_Click(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        foreach (var i in RenderItems) i.IsExclusive = true;
        _log.Debug("一键：全部独占");
    }

    private async void RefreshDevices_Click(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        // 保留当前已设置的独占状态（按设备 Id 对齐）
        var current = RenderItems.ToDictionary(i => i.DeviceId, i => i.IsExclusive);
        await LoadDevicesAsync(current, isRefresh: true);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        ResultMap = null;
        DialogResult = false;
        Close();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        ResultMap = RenderItems.ToDictionary(i => i.DeviceId, i => i.IsExclusive);
        int ex = ResultMap.Count(kvp => kvp.Value);
        _log.Information("DeviceSettingsWindow 完成：输出 {N} 台，其中独占 {Ex} 台", ResultMap.Count, ex);
        DialogResult = true;
        Close();
    }
}
