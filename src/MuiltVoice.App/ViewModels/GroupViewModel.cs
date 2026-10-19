// Copyright (c) 2026 wdd. Licensed under PolyForm Noncommercial 1.0.0.
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using MuiltVoice.Config.Models;

namespace MuiltVoice.App.ViewModels;

/// <summary>
/// 一个路由路由的 ViewModel：
/// 1) 承担列表里"摘要行"的显示（名、输入/输出数、运行态、快速启停）；
/// 2) 持有该路由自己的输入/输出设备列表（弹窗编辑时直接绑定）；
/// 3) 把运行状态变化同步到 RoutingHost（由 MainViewModel 统一调度 StartGroup/StopGroup）。
/// </summary>
public sealed class GroupViewModel : ViewModelBase
{
    private string _name;
    private bool _isRunning;
    private string? _lastError;

    public event EventHandler? StartRequested;
    public event EventHandler? StopRequested;
    public event EventHandler? DeleteRequested;

    /// <summary>路由的稳定唯一标识（与 RoutingHost 引擎 Id、Config GroupId 对齐）。</summary>
    public string GroupId { get; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (SetProperty(ref _isRunning, value))
            {
                RaisePropertyChanged(nameof(IsNotRunning));
                RaisePropertyChanged(nameof(SummaryBadgeText));
                RaisePropertyChanged(nameof(SummaryBadgeClass));
                ((RelayCommand)StartCommand).RaiseCanExecuteChanged();
                ((RelayCommand)StopCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsNotRunning => !IsRunning;

    public string? LastError
    {
        get => _lastError;
        set => SetProperty(ref _lastError, value);
    }

    /// <summary>路由拥有的输入设备视图（与当前系统设备列表一致）。</summary>
    public ObservableCollection<DeviceViewModel> Inputs { get; } = new();
    /// <summary>路由拥有的输出设备视图。</summary>
    public ObservableCollection<DeviceViewModel> Outputs { get; } = new();

    /// <summary>
    /// 该路由的音频运行参数（延迟/缓冲/电平轮询）。
    /// 仅在下次启动路由时生效。与 Config/RoutingGroupConfig.Audio 一一映射。
    /// </summary>
    public AudioSettings Audio { get; set; } = new();

    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand DeleteCommand { get; }

    public GroupViewModel(string groupId, string name)
    {
        GroupId = groupId;
        _name = name;
        StartCommand = new RelayCommand(() => StartRequested?.Invoke(this, EventArgs.Empty), CanStart);
        StopCommand = new RelayCommand(() => StopRequested?.Invoke(this, EventArgs.Empty), () => IsRunning);
        DeleteCommand = new RelayCommand(() => DeleteRequested?.Invoke(this, EventArgs.Empty));
    }

    // ---------- 列表摘要属性（供列表行显示） ----------

    public int SelectedInputCount => Inputs.Count(i => i.IsSelected);
    public int SelectedOutputCount => Outputs.Count(o => o.IsSelected);

    /// <summary>列表摘要用：当前所有选中输入的平均峰值电平（0..1）。</summary>
    public double AveragePeakLevel
    {
        get
        {
            var sel = Inputs.Where(i => i.IsSelected && !i.IsMuted).ToList();
            if (sel.Count == 0) return 0;
            return sel.Average(i => (double)i.PeakLevel);
        }
    }

    /// <summary>列表行运行徽章文字。</summary>
    public string SummaryBadgeText => IsRunning ? "运行中" : "未启动";
    /// <summary>列表行运行徽章样式 Key（用于区分颜色）。</summary>
    public string SummaryBadgeClass => IsRunning ? "Running" : "Idle";

    /// <summary>判断当前路由参数能否启动（至少一入一出）。</summary>
    public bool CanStart()
    {
        if (IsRunning) return false;
        return SelectedInputCount >= 1 && SelectedOutputCount >= 1;
    }

    /// <summary>
    /// 由 MainViewModel 在子设备属性变化（选中/音量）或设备刷新后调用，
    /// 以便刷新摘要属性（因为 ObservableCollection 只通知集合变更，不会触发子属性级别的"重新读取属性"）。
    /// </summary>
    public void RaiseSummaryChanged()
    {
        RaisePropertyChanged(nameof(SelectedInputCount));
        RaisePropertyChanged(nameof(SelectedOutputCount));
        RaisePropertyChanged(nameof(AveragePeakLevel));
        ((RelayCommand)StartCommand).RaiseCanExecuteChanged();
    }

    /// <summary>在子 DeviceViewModel 订阅属性变化，用于触发摘要刷新。</summary>
    public void WireChildListeners()
    {
        static void Handler(object? s, PropertyChangedEventArgs e, GroupViewModel self)
        {
            if (e.PropertyName is nameof(DeviceViewModel.IsSelected)
                             or nameof(DeviceViewModel.Volume)
                             or nameof(DeviceViewModel.IsMuted)
                             or nameof(DeviceViewModel.PeakLevel)
                             or nameof(DeviceViewModel.BufferedBytes)
                             or nameof(DeviceViewModel.IsExclusive))
            {
                self.RaiseSummaryChanged();
            }
        }
        void Subscribe(IEnumerable<DeviceViewModel> list)
        {
            foreach (var d in list)
                d.PropertyChanged += (s, e) => Handler(s, e, this);
        }
        Subscribe(Inputs);
        Subscribe(Outputs);
    }
}
