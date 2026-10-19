// Copyright (c) 2026 wdd. Licensed under PolyForm Noncommercial 1.0.0.
using System.IO;
using System.Windows;
using System.Windows.Threading;
using MuiltVoice.App.ViewModels;
using MuiltVoice.Audio;
using Serilog;

namespace MuiltVoice.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private MainViewModel? _vm;

    private void App_OnStartup(object sender, StartupEventArgs e)
    {
        // 0. 日志必须最先初始化（保证早期崩溃也能记录）
        AppLog.Initialize();
        AppLog.Log.Information("App_OnStartup begin");

        // 1. 全局异常兜底：把任何未捕获异常写到日志
        DispatcherUnhandledException += OnDispatcherUnhandled;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandled;
        TaskScheduler.UnobservedTaskException += OnUnobservedTask;
        Exit += OnAppExit;

        try
        {
            AppLog.Log.Information("创建 MainViewModel...");
            _vm = new MainViewModel();
            MainViewModelPostInit.AttachVolumeSync(_vm);
            AppLog.Log.Information("创建 MainViewModel 完成");

            var win = new MainWindow
            {
                DataContext = _vm,
            };
            win.Closed += (_, _) =>
            {
                AppLog.Log.Information("MainWindow.Closed 触发，开始 Dispose MainViewModel");
                try { _vm.Dispose(); }
                catch (Exception ex) { AppLog.Log.Warning(ex, "Dispose MainViewModel 时异常"); }
            };
            win.Show();
            AppLog.Log.Information("MainWindow 已显示，标题：{Title}", win.Title);
        }
        catch (Exception ex)
        {
            // 启动期异常：写日志（Fatal 级别） + 弹框
            AppLog.Log.Fatal(ex, "启动期 FATAL 异常");
            Log.CloseAndFlush();

            var logPath = Path.Combine(AppLog.LogDir, $"app-{DateTime.Today:yyyyMMdd}.log");
            MessageBox.Show(
                "程序启动失败：" + ex.Message + "\n\n" + ex.StackTrace + "\n\n详细日志：\n" + logPath,
                "MuiltVoice 启动错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void OnAppExit(object sender, ExitEventArgs e)
    {
        AppLog.Log.Information("App Exit 触发，ApplicationExitCode={Code}", e.ApplicationExitCode);
        AppLog.CloseAndFlush();
    }

    private void OnDispatcherUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Log.Error(e.Exception, "DispatcherUnhandledException（未处理 UI 线程异常）");
        // 不让 WPF 默默吞掉 → 设为已处理但弹框提示
        MessageBox.Show(
            "发生未处理异常：" + e.Exception.Message + "\n\n" + e.Exception.GetType().Name +
            "\n详细日志见 %AppData%\\MuiltVoice\\logs\\",
            "MuiltVoice 异常提示",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Handled = true; // 阻止进程崩溃，让用户能继续操作或保存配置
    }

    private void OnDomainUnhandled(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        if (ex is not null)
            AppLog.Log.Fatal(ex, "AppDomain.UnhandledException（非 UI 线程崩溃，IsTerminating={IsTerminating})",
                e.IsTerminating);
        else
            AppLog.Log.Fatal("AppDomain.UnhandledException（对象不是 Exception）：{Obj}",
                e.ExceptionObject?.ToString() ?? "(null)");
        AppLog.CloseAndFlush();
    }

    private void OnUnobservedTask(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLog.Log.Error(e.Exception, "UnobservedTaskException（Task 未观察异常，已 SetObserved）");
        e.SetObserved();
    }
}
