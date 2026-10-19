// Copyright (c) 2026 wdd. Licensed under PolyForm Noncommercial 1.0.0.
using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using MuiltVoice.App.ViewModels;

namespace MuiltVoice.App;

/// <summary>
/// 主窗口 Code-behind：
/// - 由 App.xaml.cs 注入 ViewModel（DataContext 已设好），所以这里不再 new MainVM
/// - 监听 MainVM.EditRequested 事件，弹窗显示 GroupEditorWindow
/// - 点击 Cable 链接 → 打开官网
/// - 把每个 GroupViewModel.AveragePeakLevel 映射到卡片模板里 MiniMeterFill 边框的宽度
///   （用附加属性 + ItemContainerGenerator.Loaded 回调统一挂载）
/// </summary>
public partial class MainWindow : Window
{
    private MainViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        // 监听 DataContext 变化：因为 App.xaml.cs 会在构造后再赋值
        DataContextChanged += (_, _) => HookViewModel();
        Loaded += (_, _) => HookViewModel();
    }

    private void HookViewModel()
    {
        if (_viewModel is not null) return;
        if (DataContext is not MainViewModel vm) return;
        _viewModel = vm;
        _viewModel.EditRequested += OnEditRequested;
        _viewModel.OpenDeviceSettingsRequested += OnOpenDeviceSettingsRequested;
        _viewModel.Groups.CollectionChanged += OnGroupsChanged;
        // 现有元素如果已经存在，先给它们挂 Level→Width 绑定
        HookAllCards();
    }

    private void OnGroupsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 新增路由后，等可视化树生成再 Hook
        Dispatcher.BeginInvoke(new Action(HookAllCards), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 对列表中每个卡片，找到 MiniMeterFill Border，把它的 Width 绑定到对应 GroupViewModel.AveragePeakLevel * 卡片可用 Width（近似）。
    /// 简化：固定最大值 400（按卡片内容区宽度足够），再用 LevelToWidthConverter。
    /// </summary>
    private void HookAllCards()
    {
        var conv = new LevelToWidthConverter();
        for (int i = 0; i < GroupList.Items.Count; i++)
        {
            var container = (FrameworkElement?)GroupList.ItemContainerGenerator.ContainerFromIndex(i);
            container ??= FindVisualChild<ContentPresenter>(GroupList, i);
            if (container is null) continue;
            var fill = FindVisualChildByName<Border>(container, "MiniMeterFill");
            if (fill is null) continue;
            if (fill.ReadLocalValue(WidthProperty) != DependencyProperty.UnsetValue) continue; // 已绑定
            var g = (GroupViewModel)GroupList.Items[i];
            var binding = new System.Windows.Data.Binding(nameof(GroupViewModel.AveragePeakLevel))
            {
                Source = g,
                Mode = BindingMode.OneWay,
                Converter = conv,
                ConverterParameter = 400.0,
            };
            fill.SetBinding(WidthProperty, binding);
        }
    }

    private void OnEditRequested(object? sender, GroupViewModel g)
    {
        // 用带参构造：内部会把 PresetBox/数值框按 g.Audio 初始化，
        // 并在 Loaded 后设置 SelectedItem（避免 WPF ComboBox 容器异步生成的时序问题）。
        var dlg = new GroupEditorWindow(g)
        {
            Owner = this,
        };
        dlg.ShowDialog();
        // 对话框关闭后刷新摘要
        g.RaiseSummaryChanged();
        _viewModel?.RaiseCanExecuteChangedExternally();
    }

    /// <summary>
    /// 打开"设备设置页"：
    /// - 初始独占表（mainVM 从所有路由合并给出）传入对话框工作副本
    /// - 对话框点完成后，ResultMap 回写到所有路由（跨所有路由设备粒度同步）
    /// </summary>
    private void OnOpenDeviceSettingsRequested(object? sender, IReadOnlyDictionary<string, bool> initialMap)
    {
        if (_viewModel is null) return;
        var dlg = new DeviceSettingsWindow(_viewModel.DeviceEnumerator, initialMap)
        {
            Owner = this,
        };
        var result = dlg.ShowDialog();
        if (result != true || dlg.ResultMap is null) return;

        int changed = 0;
        foreach (var g in _viewModel.Groups)
        {
            bool summaryDirty = false;
            foreach (var o in g.Outputs)
            {
                if (dlg.ResultMap.TryGetValue(o.Id, out var shouldBe) && o.IsExclusive != shouldBe)
                {
                    o.IsExclusive = shouldBe;
                    changed++;
                    summaryDirty = true;
                }
            }
            if (summaryDirty) g.RaiseSummaryChanged();
        }
        _viewModel.StatusText =
            $"设备设置已保存：输出 {dlg.ResultMap.Count} 台，独占 {dlg.ResultMap.Values.Count(v => v)} 台，" +
            $"已同步到所有路由（共影响 {changed} 条设备绑定，仅下次启动生效）";
        _viewModel.RaiseCanExecuteChangedExternally();
    }

    /// <summary>打开 VB-Cable 官网。</summary>
    private void OpenCableWeb(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://vb-audio.com/Cable/") { UseShellExecute = true });
        }
        catch
        {
            // 忽略
        }
    }

    // ============================================================
    // 辅助：遍历可视化树
    // ============================================================
    private static T? FindVisualChild<T>(DependencyObject parent, int index) where T : DependencyObject
    {
        int seen = 0;
        return FindVisualChildInternal<T>(parent, ref seen, index);
    }
    private static T? FindVisualChildInternal<T>(DependencyObject parent, ref int seen, int index) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t)
            {
                if (index < 0 || seen == index) return t;
                seen++;
            }
            var nested = FindVisualChildInternal<T>(child, ref seen, index);
            if (nested is not null) return nested;
        }
        return null;
    }
    private static T? FindVisualChildByName<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T fe && fe.Name == name) return fe;
            var nested = FindVisualChildByName<T>(child, name);
            if (nested is not null) return nested;
        }
        return null;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.EditRequested -= OnEditRequested;
            _viewModel.Groups.CollectionChanged -= OnGroupsChanged;
        }
        base.OnClosed(e);
    }
}

/// <summary>
/// 给 MainViewModel 一个对外可用的刷新 CanExecute 入口（避免在内部反复暴露）。
/// </summary>
public static class MainViewModelEx
{
    public static void RaiseCanExecuteChangedExternally(this MainViewModel vm)
    {
        // 直接在内部方法不可见，这里触发一次全局 Requery（成本低、效果等同）
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }
}
