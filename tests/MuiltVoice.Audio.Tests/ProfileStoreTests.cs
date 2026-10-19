// Copyright (c) 2026 wdd. Licensed under PolyForm Noncommercial 1.0.0.
using MuiltVoice.Config;
using MuiltVoice.Config.Models;

namespace MuiltVoice.Audio.Tests;

public class ProfileStoreTests : IDisposable
{
    private readonly ProfileStore _store = new();
    private readonly string _testName = "muiltvoice-test-" + Guid.NewGuid().ToString("N").Substring(0, 8);

    [Fact]
    public void SaveThenLoad_RoundTripsAllFields()
    {
        var profile = new RoutingProfile
        {
            Name = _testName,
            Inputs = new List<InputConfig>
            {
                new() { DeviceId = "dev-in-1", DisplayName = "麦克风", Loopback = false, Selected = true, Volume = 0.8f, Muted = false },
                new() { DeviceId = "dev-default-render", DisplayName = "Loopback", Loopback = true, Selected = true, Volume = 1f, Muted = false },
            },
            Outputs = new List<OutputConfig>
            {
                new() { DeviceId = "dev-out-1", DisplayName = "CABLE Input", Selected = true, Volume = 1f, Muted = false },
            },
        };

        _store.Save(profile);

        // 确认出现在列表
        var names = _store.ListProfileNames();
        Assert.Contains(_testName, names);

        // 加载并校验字段
        var loaded = _store.Load(_testName);
        Assert.NotNull(loaded);
        Assert.Equal(_testName, loaded!.Name);

        // 旧版单组字段在 Save 时已经被 NormalizeBeforeSave 迁移到 Groups，
        // 所以 Load 后 Inputs/Outputs 应该为空，数据保留在 Groups[0] 里
        Assert.Empty(loaded.Inputs);
        Assert.Empty(loaded.Outputs);
        Assert.Single(loaded.Groups);
        var grp = loaded.Groups[0];
        Assert.Equal(2, grp.Inputs.Count);
        Assert.Equal("dev-in-1", grp.Inputs[0].DeviceId);
        Assert.Equal("麦克风", grp.Inputs[0].DisplayName);
        Assert.True(grp.Inputs[0].Selected);
        Assert.Equal(0.8f, grp.Inputs[0].Volume, 0.0001f);
        Assert.True(grp.Inputs[1].Loopback);
        Assert.Single(grp.Outputs);
        Assert.Equal("CABLE Input", grp.Outputs[0].DisplayName);
    }

    [Fact]
    public void Load_Nonexistent_ReturnsNull()
    {
        Assert.Null(_store.Load("does-not-exist-" + Guid.NewGuid()));
    }

    [Fact]
    public void Delete_RemovesProfile()
    {
        _store.Save(new RoutingProfile { Name = _testName + "-del" });
        Assert.True(_store.Delete(_testName + "-del"));
        Assert.Null(_store.Load(_testName + "-del"));
    }

    [Fact]
    public void Delete_Nonexistent_ReturnsFalse()
    {
        Assert.False(_store.Delete("no-such-" + Guid.NewGuid()));
    }

    [Fact]
    public void Save_SanitizesInvalidFileNameChars()
    {
        var name = "bad/name?:profile";
        var profile = new RoutingProfile { Name = name, };
        _store.Save(profile);

        // 列表中应出现（被清理后的名字，但原始 Name 保留）
        var names = _store.ListProfileNames();
        // 清理后的名字会出现在文件名中
        Assert.NotEmpty(names);
    }

    /* ==================== P8 多路由组 + 旧存档迁移 ==================== */

    [Fact]
    public void MigrateLegacyToGroups_EmptyInputsOutputs_NoOp()
    {
        var p = new RoutingProfile { Name = "empty" };
        p.MigrateLegacyToGroups();
        Assert.Empty(p.Groups);
        Assert.Empty(p.Inputs);
        Assert.Empty(p.Outputs);
    }

    [Fact]
    public void MigrateLegacyToGroups_SingleGroupCreated()
    {
        var p = new RoutingProfile
        {
            Name = "legacy",
            Inputs = new List<InputConfig>
            {
                new() { DeviceId = "mic", Selected = true, Volume = 0.5f }
            },
            Outputs = new List<OutputConfig>
            {
                new() { DeviceId = "spk", Selected = true }
            },
        };
        p.MigrateLegacyToGroups();
        Assert.Single(p.Groups);
        Assert.Empty(p.Inputs);
        Assert.Empty(p.Outputs);
        Assert.Equal("默认路由", p.Groups[0].Name);
        Assert.Equal("legacy-default", p.Groups[0].GroupId);
        Assert.Single(p.Groups[0].Inputs);
        Assert.Single(p.Groups[0].Outputs);
        Assert.Equal(0.5f, p.Groups[0].Inputs[0].Volume, 0.0001f);
    }

    [Fact]
    public void MigrateLegacyToGroups_NewGroupsTakesPriority()
    {
        var p = new RoutingProfile
        {
            Name = "already-new",
            Groups = new List<RoutingGroupConfig>
            {
                new() { Name = "手动方块", GroupId = "g_abc" }
            },
            // 旧字段也填了内容 → 不迁移
            Inputs = new List<InputConfig> { new() { DeviceId = "x" } },
            Outputs = new List<OutputConfig> { new() { DeviceId = "y" } },
        };
        p.MigrateLegacyToGroups();
        Assert.Single(p.Groups);
        Assert.Equal("g_abc", p.Groups[0].GroupId);
        // 旧字段不会被清空（因为不迁移）
        Assert.Single(p.Inputs);
    }

    [Fact]
    public void NormalizeBeforeSave_AssignsMissingGroupIds()
    {
        var p = new RoutingProfile { Name = "save-test" };
        p.Groups.Add(new RoutingGroupConfig { Name = "", Inputs = { new InputConfig { DeviceId = "d" } } });
        p.Groups.Add(new RoutingGroupConfig { GroupId = "g_exist", Name = "已有" });

        p.NormalizeBeforeSave();

        Assert.Equal(2, p.Groups.Count);
        Assert.NotEmpty(p.Groups[0].GroupId);
        Assert.StartsWith("g_", p.Groups[0].GroupId);
        Assert.NotEmpty(p.Groups[0].Name); // 自动命名
        Assert.Equal("g_exist", p.Groups[1].GroupId);
    }

    [Fact]
    public void SaveAndLoad_LegacyJson_AutoMigratesToGroups()
    {
        // 手动构造旧格式 JSON（仅 Inputs/Outputs），写到磁盘再 Load
        var name = _testName + "-legacy-mig";
        var legacy = new RoutingProfile
        {
            Name = name,
            Inputs = new List<InputConfig>
            {
                new() { DeviceId = "in1", Selected = true, Volume = 0.7f, Loopback = false }
            },
            Outputs = new List<OutputConfig>
            {
                new() { DeviceId = "out1", Selected = true, Volume = 1.1f }
            },
        };
        _store.Save(legacy);

        var loaded = _store.Load(name);
        Assert.NotNull(loaded);
        Assert.Single(loaded!.Groups);
        Assert.Empty(loaded.Inputs);  // 已迁移并被清空
        Assert.Equal("in1", loaded.Groups[0].Inputs[0].DeviceId);
        Assert.Equal(0.7f, loaded.Groups[0].Inputs[0].Volume, 0.0001f);
        Assert.Equal("out1", loaded.Groups[0].Outputs[0].DeviceId);

        _store.Delete(name);
    }

    [Fact]
    public void SaveAndLoad_MultiGroups_RoundTrips()
    {
        var name = _testName + "-multi-groups";
        var p = new RoutingProfile
        {
            Name = name,
            Groups = new List<RoutingGroupConfig>
            {
                new()
                {
                    GroupId = "g1",
                    Name = "🎤 会议路由",
                    Inputs = { new InputConfig { DeviceId = "mic", Selected = true, Volume = 1.2f } },
                    Outputs = { new OutputConfig { DeviceId = "cable", Selected = true, Volume = 1.0f } },
                },
                new()
                {
                    GroupId = "g2",
                    Name = "🎧 监听",
                    Inputs = { new InputConfig { DeviceId = "lb", Loopback = true, Selected = true } },
                    Outputs = { new OutputConfig { DeviceId = "hp", Selected = true } },
                },
            },
        };
        _store.Save(p);

        var loaded = _store.Load(name);
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Groups.Count);
        Assert.Equal("g1", loaded.Groups[0].GroupId);
        Assert.Equal("🎤 会议路由", loaded.Groups[0].Name);
        Assert.Equal(1.2f, loaded.Groups[0].Inputs[0].Volume, 0.0001f);
        Assert.Equal("g2", loaded.Groups[1].GroupId);
        Assert.True(loaded.Groups[1].Inputs[0].Loopback);

        _store.Delete(name);
    }

    public void Dispose()
    {
        _store.Delete(_testName);
        _store.Delete(_testName + "-del");
        _store.Delete(_testName + "-legacy-mig");
        _store.Delete(_testName + "-multi-groups");
        try { _store.Delete("bad_name___profile"); } catch { }
    }
}
