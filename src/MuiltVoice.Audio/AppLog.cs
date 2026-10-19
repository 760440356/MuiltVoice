// Copyright (c) 2026 wdd. Licensed under PolyForm Noncommercial 1.0.0.
using System.IO;
using Serilog;
using Serilog.Events;

namespace MuiltVoice.Audio;

/// <summary>
/// 全局日志入口：基于 Serilog（Sinks.File + Console）。
///
/// 在 App 启动时调用 AppLog.Initialize()，在退出时调用 AppLog.CloseAndFlush()。
/// 其他地方直接用 AppLog.Log 拿到 ILogger（Debug/Information/Warning/Error/Fatal）。
///
/// 日志路径：%AppData%\MuiltVoice\logs\app-YYYYMMDD.log
/// 滚动策略：单文件 ≤ 10MB，保留最近 31 天；超出后自动覆盖最早一天。
/// </summary>
public static class AppLog
{
    private static ILogger? _logger;
    private static bool _initialized;
    private static readonly object Lock = new();

    /// <summary>已初始化返回 true；未初始化则返回一个“丢进黑洞”的 ILogger，避免调用方判空。</summary>
    public static ILogger Log => _logger ?? Serilog.Log.Logger;

    /// <summary>日志文件所在目录（Initialize 之后才有值；否则返回默认路径）。</summary>
    public static string LogDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MuiltVoice", "logs");

    /// <summary>
    /// 初始化全局日志。可重复调用（第二次起直接返回）。
    /// 调用会同时把 Serilog.Log 全局静态设置好。
    /// </summary>
    public static void Initialize()
    {
        if (_initialized) return;
        lock (Lock)
        {
            if (_initialized) return;

            var dir = LogDir;
            Directory.CreateDirectory(dir);
            var logFile = Path.Combine(dir, "app-.log"); // app-YYYYMMDD.log

            var config = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .Enrich.FromLogContext()
                .WriteTo.File(
                    path: logFile,
                    restrictedToMinimumLevel: LogEventLevel.Debug,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 31,
                    fileSizeLimitBytes: 10 * 1024 * 1024, // 10MB / 天
                    rollOnFileSizeLimit: true,
                    shared: false,
                    flushToDiskInterval: TimeSpan.FromSeconds(2),
                    outputTemplate:
                    "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
                .WriteTo.Console(
                    restrictedToMinimumLevel: LogEventLevel.Information,
                    outputTemplate:
                    "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}");

            _logger = config.CreateLogger();

            // 把 Serilog 全局静态也设为同一个实例，方便任何地方用 Serilog.Log
            Serilog.Log.Logger = _logger;

            _initialized = true;
            _logger.Information(
                "==========================================================");
            _logger.Information(
                "MuiltVoice 启动：PID={Pid}, OS={OS}, 64bit={Is64}, Entry={Entry}",
                Environment.ProcessId,
                Environment.OSVersion.VersionString,
                Environment.Is64BitProcess,
                Environment.ProcessPath);
            _logger.Information("日志目录：{Dir}", dir);
        }
    }

    /// <summary>关闭并刷盘所有日志。退出 App 时调用一次。</summary>
    public static void CloseAndFlush()
    {
        try
        {
            if (_logger is not null)
            {
                _logger.Information("MuiltVoice 退出，日志 Flush 完成。");
            }
            Serilog.Log.CloseAndFlush();
        }
        catch
        {
            // 关日志本身不能再抛异常
        }
        finally
        {
            _logger = null;
            _initialized = false;
        }
    }
}
