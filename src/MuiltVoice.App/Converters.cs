// Copyright (c) 2026 wdd. Licensed under PolyForm Noncommercial 1.0.0.
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using MuiltVoice.App.ViewModels;

namespace MuiltVoice.App;

/// <summary>true → Visible，false → Collapsed。</summary>
public sealed class BoolToVisibilityHidden : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// bool 取反 + 根据 ConverterParameter 输出两个字符串中的一个。
/// 参数格式 "FalseStr;TrueStr"（以英文分号分隔）；
/// 若 parameter 为空仅取反 bool（不常用）。
/// 例：IsExclusive → "共享;独占" → false=共享 true=独占
/// </summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value is true;
        if (parameter is string s && !string.IsNullOrEmpty(s))
        {
            var parts = s.Split([';'], 2);
            return b
                ? (parts.Length > 1 ? parts[1] : parts[0])
                : parts[0];
        }
        return !b;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>电平 PeakLevel (0..1) → 像素宽度。</summary>
public sealed class LevelToWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var level = value as double? ?? value as float? ?? System.Convert.ToDouble(value);
        if (level < 0) level = 0;
        if (level > 1) level = 1;
        double max = parameter is null ? 100.0 : System.Convert.ToDouble(parameter, culture);
        return max * level;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/* ============ 主窗口 DataTemplate 用到的 Converter/Markup ============ */

/// <summary>
/// SummaryBadgeClass string → Border 对象（运行中徽章/未启动徽章）。
/// 直接在 C# 里构造带颜色和文字的 Border，不依赖 LightTheme 里的 BadgeRunning/BadgeIdle
/// （那些 Style 用了 Setter Property="Child" 在 Border 上不被 WPF 支持）。
/// </summary>
public sealed class SummaryBadgeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value as string ?? "Idle";
        var isRunning = key == "Running";
        var border = new Border
        {
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(10, 4, 10, 4),
            MinWidth = 56,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Background = isRunning
                ? (Brush)new SolidColorBrush(Color.FromRgb(0xE4, 0xF6, 0xEE))
                : (Brush)new SolidColorBrush(Color.FromRgb(0xEE, 0xF2, 0xF7)),
            Child = new System.Windows.Controls.TextBlock
            {
                Text = isRunning ? "运行中" : "未启动",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = isRunning
                    ? (Brush)new SolidColorBrush(Color.FromRgb(0x1A, 0x8F, 0x6A))
                    : (Brush)new SolidColorBrush(Color.FromRgb(0x6C, 0x7E, 0x93)),
            },
        };
        return border;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>ObservableCollection&lt;DeviceViewModel&gt; → 已选设备名字逗号分隔字符串。</summary>
public sealed class SelectedNamesConv : MarkupExtension, IValueConverter
{
    public override object ProvideValue(IServiceProvider serviceProvider) => this;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not IEnumerable<DeviceViewModel> list) return "—";
        var names = list.Where(d => d.IsSelected)
            .Select(d =>
            {
                var tag = d.IsLoopback ? "(系统声音)" : d.IsCable ? "(Cable)" : "";
                return $"{d.Name}{tag}";
            })
            .ToList();
        if (names.Count == 0) return "（未选择）";
        return string.Join("，", names);
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Cable 安装状态 → 提示文本。</summary>
public sealed class CableTipConv : MarkupExtension, IValueConverter
{
    public override object ProvideValue(IServiceProvider serviceProvider) => this;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var installed = value as bool? ?? false;
        return installed
            ? "✅ 已检测到 VB-Cable，可在输出中选择「CABLE Input」"
            : "⚠️  未检测到 VB-Cable。需要『虚拟麦克风』时请先安装。";
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
