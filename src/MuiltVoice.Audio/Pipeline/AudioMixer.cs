// Copyright (c) 2026 wdd. Licensed under PolyForm Noncommercial 1.0.0.
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using MuiltVoice.Audio.Formats;

namespace MuiltVoice.Audio.Pipeline;

/// <summary>
/// 混音器：封装 <see cref="MixingSampleProvider"/>，把多路（48kHz stereo float）
/// 输入线性叠加为一路输出。所有输入必须为统一格式，否则 AddInput 抛异常。
/// ReadFully=true 保证时间轴对齐（某路暂无数据时填静音）。
/// </summary>
public sealed class AudioMixer : IDisposable
{
    private readonly MixingSampleProvider _mixer;
    private bool _disposed;

    /// <summary>混音输出（统一格式），供 OutputChannel 拉取。</summary>
    public ISampleProvider Output => _mixer;

    /// <summary>混音格式（恒为 48kHz stereo float）。</summary>
    public WaveFormat WaveFormat => _mixer.WaveFormat;

    /// <summary>当前输入路数。</summary>
    public int InputCount => _mixer.MixerInputs.Count();

    public AudioMixer()
    {
        _mixer = new MixingSampleProvider(AudioConstants.UnifiedFormat)
        {
            ReadFully = true, // 输入暂无数据时填静音，保持时间轴推进
        };
    }

    /// <summary>添加一路输入（必须为统一格式）。</summary>
    public void AddInput(ISampleProvider input)
    {
        ThrowIfDisposed();
        _mixer.AddMixerInput(input);
    }

    /// <summary>移除一路输入。</summary>
    public void RemoveInput(ISampleProvider input)
    {
        ThrowIfDisposed();
        _mixer.RemoveMixerInput(input);
    }

    /// <summary>清空所有输入。</summary>
    public void ClearInputs()
    {
        ThrowIfDisposed();
        ClearInputsCore();
    }

    private void ClearInputsCore()
    {
        // MixerInputs 是只读集合，逐个移除
        var snapshot = _mixer.MixerInputs.ToArray();
        foreach (var input in snapshot)
        {
            _mixer.RemoveMixerInput(input);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        ClearInputsCore();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(AudioMixer));
        }
    }
}
