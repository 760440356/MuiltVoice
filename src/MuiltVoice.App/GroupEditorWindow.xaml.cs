// Copyright (c) 2026 wdd. Licensed under PolyForm Noncommercial 1.0.0.
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using MuiltVoice.App.ViewModels;
using MuiltVoice.Config.Models;

namespace MuiltVoice.App;

/// <summary>
/// GroupEditorWindow.xaml：编辑单个路由（名称、输入/输出设备勾选、高级音频参数）。
/// AudioSettings 在打开时深拷贝到"工作副本"；点击【完成】才写回目标 GroupViewModel.Audio；
/// 点取消则不写回，保证数据安全。
/// 下次启动路由时参数生效，运行中改动不影响正在播放的路由。
/// </summary>
public partial class GroupEditorWindow : Window
{
    private readonly GroupViewModel _target;
    // 工作副本：OK 才写回 _target.Audio
    private AudioSettings _working;
    private bool _inPresetApply = false; // 预设套用时禁止数值变更回调把 Preset 改写回 Custom

    public ObservableCollection<PresetItem> PresetOptions { get; } = new()
    {
        new(AudioPreset.Balanced,   "平衡模式",   "兼顾延迟与稳定性，适合多数设备"),
        new(AudioPreset.LowLatency, "低延时模式", "最小 latency，适合本地监听 / 弹奏。若触发 0x8889000A，将自动回退到兜底 latency"),
        new(AudioPreset.SyncOutput, "输出同频",   "大缓冲保证多输出端时间对齐，适合多房间 / 多设备同步播放"),
        new(AudioPreset.Custom,     "自定义",     "以下参数都可以手填；任何一项手改后预设自动变为自定义"),
    };

    public GroupEditorWindow()
    {
        InitializeComponent();
        _target = null!;
        _working = new AudioSettings();
    }

    public GroupEditorWindow(GroupViewModel target) : this()
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        DataContext = target;
        Title = $"编辑路由 · {target.Name}";

        // 工作副本：打开时 deep-copy；取消不写回
        _working = (target.Audio ?? new AudioSettings()).Clone();

        // 关键：ComboBox ItemsSource 设置后，ItemContainer 生成是异步的。
        // 立即设置 SelectedValue 会出现"下拉为空、没选中项"的 WPF 时序问题。
        // 解决：先把数值框填好（不依赖 ComboBox 选中），订阅也先挂上，
        //      再把 SelectedValue 延迟到 Loaded 后设置（此时容器已生成）。
        RenderLatencyMsBox.Text = _working.RenderLatencyMs.ToString();
        RenderLatencyFallbackMsBox.Text = _working.RenderLatencyFallbackMs.ToString();
        InputBufferMsBox.Text = _working.InputBufferMs.ToString();
        TapBufferMsBox.Text = _working.TapBufferMs.ToString();
        MeterPollMsBox.Text = _working.MeterPollMs.ToString();

        PresetBox.ItemsSource = PresetOptions;
        PresetHint.Text = LookupDesc(_working.Preset);

        // 订阅：预设切换自动覆盖数值
        PresetBox.SelectionChanged += (_, _) =>
        {
            if (PresetBox.SelectedValue is not AudioPreset p) return;
            _inPresetApply = true;
            try
            {
                if (p != AudioPreset.Custom)
                {
                    _working.ApplyPreset(p);
                    RenderLatencyMsBox.Text = _working.RenderLatencyMs.ToString();
                    RenderLatencyFallbackMsBox.Text = _working.RenderLatencyFallbackMs.ToString();
                    InputBufferMsBox.Text = _working.InputBufferMs.ToString();
                    TapBufferMsBox.Text = _working.TapBufferMs.ToString();
                    MeterPollMsBox.Text = _working.MeterPollMs.ToString();
                }
                else
                {
                    _working.Preset = AudioPreset.Custom;
                }
                PresetHint.Text = LookupDesc(p);
            }
            finally { _inPresetApply = false; }
        };

        // 数值被手改 → 自动切到 Custom
        RenderLatencyMsBox.TextChanged += (_, _) => TryWriteNum(RenderLatencyMsBox, v => _working.RenderLatencyMs = Clamp(v, 5, 500));
        RenderLatencyFallbackMsBox.TextChanged += (_, _) => TryWriteNum(RenderLatencyFallbackMsBox, v => _working.RenderLatencyFallbackMs = Clamp(v, 5, 5000));
        InputBufferMsBox.TextChanged += (_, _) => TryWriteNum(InputBufferMsBox, v => _working.InputBufferMs = Clamp(v, 10, 5000));
        TapBufferMsBox.TextChanged += (_, _) => TryWriteNum(TapBufferMsBox, v => _working.TapBufferMs = Clamp(v, 10, 5000));
        MeterPollMsBox.TextChanged += (_, _) => TryWriteNum(MeterPollMsBox, v => _working.MeterPollMs = Clamp(v, 30, 500));

        // 在容器生成完成后再设置 SelectedValue，避免下拉为空
        Loaded += (_, _) =>
        {
            var match = PresetOptions.FirstOrDefault(o => o.Value == _working.Preset)
                       ?? PresetOptions[0];
            PresetBox.SelectedItem = match;
            PresetHint.Text = LookupDesc(_working.Preset);
        };
    }

    private void TryWriteNum(TextBox source, Action<int> write)
    {
        if (int.TryParse(source.Text, out var v))
        {
            write(v);
            if (!_inPresetApply && _working.Preset != AudioPreset.Custom)
            {
                // 手改了数值 → 预设自动变为 Custom（不覆盖其他值）
                _working.Preset = AudioPreset.Custom;
                if (PresetBox.SelectedValue is not AudioPreset.Custom)
                {
                    PresetBox.SelectedValue = AudioPreset.Custom;
                    PresetHint.Text = LookupDesc(AudioPreset.Custom);
                }
            }
        }
    }

    private static int Clamp(int v, int min, int max) { if (v < min) return min; if (v > max) return max; return v; }

    private string LookupDesc(AudioPreset p)
    {
        foreach (var it in PresetOptions) if (it.Value == p) return it.Description;
        return string.Empty;
    }

    private void OkClick(object sender, RoutedEventArgs e)
    {
        // 兜底：保证 fallback >= 主延迟
        if (_working.RenderLatencyFallbackMs < _working.RenderLatencyMs)
            _working.RenderLatencyFallbackMs = _working.RenderLatencyMs;

        // 写回目标
        _target.Audio = _working.Clone();

        DialogResult = true;
        Close();
    }

    private void CancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

/// <summary>预设下拉项（Display 名 + 描述）。</summary>
public sealed class PresetItem
{
    public AudioPreset Value { get; }
    public string Display { get; }
    public string Description { get; }
    public PresetItem(AudioPreset value, string display, string description)
    {
        Value = value; Display = display; Description = description;
    }
}
