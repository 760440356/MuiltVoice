// Copyright (c) 2026 wdd. Licensed under PolyForm Noncommercial 1.0.0.
namespace MuiltVoice.Config.Models;

/// <summary>
/// 每路由独立的音频运行参数（延迟/缓冲/电平轮询）。
/// 持久化在 RoutingGroupConfig.Audio 里，与路由一同序列化。
/// 所有值都允许 UI 修改，但只在下次 StartWithRouting 时生效
/// （WASAPI Init 后无法热改 latency，运行中改动不会影响当前路由）。
/// </summary>
public sealed class AudioSettings
{
    /// <summary>当前预设。切换预设会重写以下数值；选 Custom 则保留手填值。</summary>
    public AudioPreset Preset { get; set; } = AudioPreset.Balanced;

    /// <summary>
    /// WASAPI 共享模式渲染目标延迟（毫秒）。
    /// 范围 5-200。越小延迟越低，但太小可能触发 0x8889000A（由回退策略兜底）。
    /// </summary>
    public int RenderLatencyMs { get; set; } = 20;

    /// <summary>渲染回退 latency（毫秒）。主策略失败时使用，范围 10-500。</summary>
    public int RenderLatencyFallbackMs { get; set; } = 50;

    /// <summary>每路输入缓冲最大时长（毫秒）。范围 50-2000。越大抗抖动越强，但堆积延迟也越大。</summary>
    public int InputBufferMs { get; set; } = 200;

    /// <summary>每个 tap（扇出端）缓冲最大时长（毫秒）。范围 50-2000。</summary>
    public int TapBufferMs { get; set; } = 200;

    /// <summary>UI 电平/音量轮询间隔（毫秒）。范围 30-500。越小电平条越平滑，但 UI 占用越高。</summary>
    public int MeterPollMs { get; set; } = 100;

    /// <summary>按预设生成一份新设置（深拷贝预设值）。</summary>
    public static AudioSettings ForPreset(AudioPreset preset) => preset switch
    {
        AudioPreset.LowLatency => new AudioSettings
        {
            Preset = AudioPreset.LowLatency,
            RenderLatencyMs = 5,
            RenderLatencyFallbackMs = 10,
            InputBufferMs = 100,
            TapBufferMs = 100,
            MeterPollMs = 50,
        },
        AudioPreset.SyncOutput => new AudioSettings
        {
            Preset = AudioPreset.SyncOutput,
            RenderLatencyMs = 50,
            RenderLatencyFallbackMs = 100,
            InputBufferMs = 500,
            TapBufferMs = 500,
            MeterPollMs = 100,
        },
        _ => new AudioSettings(), // Balanced / Custom 默认值
    };

    /// <summary>切换到指定预设并覆盖各数值（Custom 不覆盖，保留当前值）。</summary>
    public void ApplyPreset(AudioPreset preset)
    {
        Preset = preset;
        if (preset == AudioPreset.Custom) return;
        var p = ForPreset(preset);
        RenderLatencyMs = p.RenderLatencyMs;
        RenderLatencyFallbackMs = p.RenderLatencyFallbackMs;
        InputBufferMs = p.InputBufferMs;
        TapBufferMs = p.TapBufferMs;
        MeterPollMs = p.MeterPollMs;
    }

    /// <summary>任意参数被手改后调用，自动标记为 Custom（保留所有当前值）。</summary>
    public void MarkCustom()
    {
        Preset = AudioPreset.Custom;
    }

    /// <summary>深拷贝。</summary>
    public AudioSettings Clone() => new()
    {
        Preset = Preset,
        RenderLatencyMs = RenderLatencyMs,
        RenderLatencyFallbackMs = RenderLatencyFallbackMs,
        InputBufferMs = InputBufferMs,
        TapBufferMs = TapBufferMs,
        MeterPollMs = MeterPollMs,
    };
}
