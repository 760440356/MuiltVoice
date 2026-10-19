// Copyright (c) 2026 wdd. Licensed under PolyForm Noncommercial 1.0.0.
using MuiltVoice.Audio.Formats;
using MuiltVoice.Audio.Pipeline;
using MuiltVoice.Audio.Tests.Helpers;
using NAudio.Wave;

namespace MuiltVoice.Audio.Tests;

public class AudioMixerTests
{
    [Fact]
    public void NewMixer_HasUnifiedFormat_AndZeroInputs()
    {
        using var mixer = new AudioMixer();
        Assert.Equal(48000, mixer.WaveFormat.SampleRate);
        Assert.Equal(2, mixer.WaveFormat.Channels);
        Assert.Equal(WaveFormatEncoding.IeeeFloat, mixer.WaveFormat.Encoding);
        Assert.Equal(0, mixer.InputCount);
    }

    [Fact]
    public void AddInput_IncreasesCount()
    {
        using var mixer = new AudioMixer();
        var src = new ConstantSampleProvider(0.5f);
        mixer.AddInput(src);
        Assert.Equal(1, mixer.InputCount);
    }

    [Fact]
    public void MixedOutput_EqualsSumOfInputs()
    {
        // 两路常量信号 0.5 + 0.3 = 0.8
        using var mixer = new AudioMixer();
        mixer.AddInput(new ConstantSampleProvider(0.5f));
        mixer.AddInput(new ConstantSampleProvider(0.3f));

        var buf = new float[4];
        int read = mixer.Output.Read(buf, 0, 4);

        Assert.Equal(4, read);
        Assert.Equal(0.8f, buf[0], 0.0001f);
        Assert.Equal(0.8f, buf[1], 0.0001f);
        Assert.Equal(0.8f, buf[2], 0.0001f);
        Assert.Equal(0.8f, buf[3], 0.0001f);
    }

    [Fact]
    public void MixedOutput_SingleInput_PassesThrough()
    {
        using var mixer = new AudioMixer();
        mixer.AddInput(new ConstantSampleProvider(0.42f));

        var buf = new float[2];
        mixer.Output.Read(buf, 0, 2);

        Assert.Equal(0.42f, buf[0], 0.0001f);
    }

    [Fact]
    public void RemoveInput_DecreasesCount()
    {
        using var mixer = new AudioMixer();
        var src = new ConstantSampleProvider(0.5f);
        mixer.AddInput(src);
        mixer.RemoveInput(src);
        Assert.Equal(0, mixer.InputCount);
    }

    [Fact]
    public void RemoveInput_StopsContributingToMix()
    {
        using var mixer = new AudioMixer();
        var src = new ConstantSampleProvider(0.5f);
        mixer.AddInput(src);
        mixer.AddInput(new ConstantSampleProvider(0.3f));
        mixer.RemoveInput(src);

        var buf = new float[2];
        mixer.Output.Read(buf, 0, 2);
        Assert.Equal(0.3f, buf[0], 0.0001f);
    }

    [Fact]
    public void ClearInputs_RemovesAll()
    {
        using var mixer = new AudioMixer();
        mixer.AddInput(new ConstantSampleProvider(0.1f));
        mixer.AddInput(new ConstantSampleProvider(0.2f));
        mixer.AddInput(new ConstantSampleProvider(0.3f));

        mixer.ClearInputs();

        Assert.Equal(0, mixer.InputCount);
    }

    [Fact]
    public void AddInput_WrongFormat_Throws()
    {
        // MixingSampleProvider 要求输入格式与构造时一致（48k stereo float）
        using var mixer = new AudioMixer();
        // 44.1k mono，格式不符
        var bad = new ConstantSampleProvider(0.5f, sampleRate: 44100, channels: 1);

        Assert.Throws<ArgumentException>(() => mixer.AddInput(bad));
    }

    [Fact]
    public void MixedOutput_ManyInputs_AllSummed()
    {
        using var mixer = new AudioMixer();
        for (int i = 0; i < 8; i++)
        {
            mixer.AddInput(new ConstantSampleProvider(0.1f));
        }

        var buf = new float[1];
        mixer.Output.Read(buf, 0, 1);
        // 8 * 0.1 = 0.8
        Assert.Equal(0.8f, buf[0], 0.0001f);
    }

    [Fact]
    public void EmptyMixer_ReadReturnsSilence()
    {
        // MixingSampleProvider 空时 ReadFully 行为：返回静音
        using var mixer = new AudioMixer();
        var buf = new float[4];
        int read = mixer.Output.Read(buf, 0, 4);
        Assert.Equal(4, read);
        Assert.Equal(0f, buf[0]);
    }
}
