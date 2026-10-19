// Copyright (c) 2026 wdd. Licensed under PolyForm Noncommercial 1.0.0.
namespace MuiltVoice.Config.Models;

/// <summary>
/// 音频参数预设：常见场景一键套用，免去逐项手调。
/// 切换预设会覆盖各 Latency/Buffer 参数；选 Custom 则完全手填。
/// </summary>
public enum AudioPreset
{
    /// <summary>平衡（默认）：兼顾延迟与稳定性，适合多数设备。</summary>
    Balanced = 0,

    /// <summary>低延时模式：最小 latency，适合本地监听/弹奏。某些驱动可能触发 0x8889000A，回退策略会自动兜底。</summary>
    LowLatency = 1,

    /// <summary>输出同频：大缓冲保证多输出端时间对齐，适合多设备同步播放（如多房间音响）。延迟较高。</summary>
    SyncOutput = 2,

    /// <summary>自定义：完全手填各参数。</summary>
    Custom = 99,
}
