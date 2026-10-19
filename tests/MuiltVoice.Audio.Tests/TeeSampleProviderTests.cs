// Copyright (c) 2026 wdd. Licensed under PolyForm Noncommercial 1.0.0.
using MuiltVoice.Audio.Formats;
using MuiltVoice.Audio.Pipeline;
using MuiltVoice.Audio.Tests.Helpers;
using NAudio.Wave;

namespace MuiltVoice.Audio.Tests;

public class TeeSampleProviderTests
{
    private static BufferedWaveProvider NewTap()
        => new(AudioConstants.UnifiedFormat)
        {
            ReadFully = true,
            DiscardOnBufferOverflow = true,
        };

    [Fact]
    public void WaveFormat_MatchesSource()
    {
        var source = new ConstantSampleProvider(0.5f);
        var tee = new TeeSampleProvider(source);
        Assert.Equal(48000, tee.WaveFormat.SampleRate);
        Assert.Equal(2, tee.WaveFormat.Channels);
    }

    [Fact]
    public void Construct_NonFloatSource_Throws()
    {
        // 用 LyingFormatSampleProvider 谎报 PCM16 格式（标准 ISampleProvider 总是 float）
        var real = new ConstantSampleProvider(0.5f);
        var lying = new LyingFormatSampleProvider(real, new WaveFormat(48000, 16, 2));
        Assert.Throws<ArgumentException>(() => new TeeSampleProvider(lying));
    }

    [Fact]
    public void TapCount_TracksAddRemove()
    {
        var tee = new TeeSampleProvider(new ConstantSampleProvider(0.5f));
        Assert.Equal(0, tee.TapCount);

        var t1 = NewTap();
        var t2 = NewTap();
        tee.AddTap(t1);
        tee.AddTap(t2);
        Assert.Equal(2, tee.TapCount);

        tee.RemoveTap(t1);
        Assert.Equal(1, tee.TapCount);

        tee.ClearTaps();
        Assert.Equal(0, tee.TapCount);
    }

    [Fact]
    public void Read_NoTaps_PassesSourceThroughUnchanged()
    {
        var source = new ConstantSampleProvider(0.42f);
        var tee = new TeeSampleProvider(source);

        var buf = new float[8];
        int n = tee.Read(buf, 0, 8);

        Assert.Equal(8, n);
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(0.42f, buf[i], 0.0001f);
        }
    }

    [Fact]
    public void Read_WithTap_TapReceivesIdenticalCopy()
    {
        var source = new ConstantSampleProvider(0.5f);
        var tee = new TeeSampleProvider(source);
        var tap = NewTap();
        tee.AddTap(tap);

        var buf = new float[8];
        int n = tee.Read(buf, 0, 8);

        // 主路径数据不变
        Assert.Equal(8, n);
        Assert.Equal(0.5f, buf[0], 0.0001f);

        // tap 收到相同数据
        var tapBuf = new byte[32]; // 8 floats = 32 bytes
        int tapRead = tap.Read(tapBuf, 0, 32);
        Assert.Equal(32, tapRead);
        float tapVal = BitConverter.ToSingle(tapBuf, 0);
        Assert.Equal(0.5f, tapVal, 0.0001f);
    }

    [Fact]
    public void Read_MultipleTaps_AllReceiveIdenticalCopy()
    {
        var source = new ConstantSampleProvider(0.7f);
        var tee = new TeeSampleProvider(source);
        var tap1 = NewTap();
        var tap2 = NewTap();
        var tap3 = NewTap();
        tee.AddTap(tap1);
        tee.AddTap(tap2);
        tee.AddTap(tap3);

        var buf = new float[4];
        tee.Read(buf, 0, 4);

        foreach (var tap in new[] { tap1, tap2, tap3 })
        {
            var tapBuf = new byte[16];
            int r = tap.Read(tapBuf, 0, 16);
            Assert.Equal(16, r);
            Assert.Equal(0.7f, BitConverter.ToSingle(tapBuf, 0), 0.0001f);
        }
    }

    [Fact]
    public void AddTap_WrongFormat_Throws()
    {
        var tee = new TeeSampleProvider(new ConstantSampleProvider(0.5f));
        // 44.1k mono，格式不符
        var bad = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(44100, 1));
        Assert.Throws<ArgumentException>(() => tee.AddTap(bad));
    }

    [Fact]
    public void Read_SourceExhausted_PropagatesZeroRead()
    {
        // 固定序列：4 个样本后结束
        var source = new FixedSequenceSampleProvider(new float[] { 0.1f, 0.2f, 0.3f, 0.4f });
        var tee = new TeeSampleProvider(source);

        var buf = new float[8];
        int n = tee.Read(buf, 0, 8);
        // 只能读到 4 个
        Assert.Equal(4, n);
        Assert.Equal(0.1f, buf[0], 0.0001f);
        Assert.Equal(0.4f, buf[3], 0.0001f);
    }

    [Fact]
    public void Read_PushesToTap_EvenWhenPartialRead()
    {
        var source = new FixedSequenceSampleProvider(new float[] { 0.9f, 0.8f });
        var tee = new TeeSampleProvider(source);
        var tap = NewTap();
        tee.AddTap(tap);

        var buf = new float[8];
        int n = tee.Read(buf, 0, 8);
        Assert.Equal(2, n);

        // tap 也应收到 2 个样本（8 字节）
        var tapBuf = new byte[8];
        int tapRead = tap.Read(tapBuf, 0, 8);
        Assert.Equal(8, tapRead);
        Assert.Equal(0.9f, BitConverter.ToSingle(tapBuf, 0), 0.0001f);
    }
}
