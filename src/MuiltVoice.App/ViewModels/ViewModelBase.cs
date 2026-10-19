// Copyright (c) 2026 wdd. Licensed under PolyForm Noncommercial 1.0.0.
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MuiltVoice.App.ViewModels;

/// <summary>
/// MVVM 基类：实现 <see cref="INotifyPropertyChanged"/>，
/// 提供 <see cref="SetProperty{T}"/> 便利方法。
/// </summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>显式式属性名触发 PropertyChanged（当属性名不是调用方时使用）。</summary>
    protected void RaisePropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
