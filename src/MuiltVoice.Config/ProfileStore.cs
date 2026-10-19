// Copyright (c) 2026 wdd. Licensed under PolyForm Noncommercial 1.0.0.
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MuiltVoice.Config.Models;

namespace MuiltVoice.Config;

/// <summary>
/// 路由方案的 JSON 存档服务。文件存放在
/// <c>%AppData%\MuiltVoice\profiles\&lt;name&gt;.json</c>。
/// 线程安全：所有方法加锁串行化文件访问。
/// </summary>
public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string ProfilesDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MuiltVoice", "profiles");

    private readonly object _lock = new();

    /// <summary>确保 profiles 目录存在。</summary>
    public static string EnsureProfilesDirectory()
    {
        var dir = ProfilesDir;
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>列出所有已保存方案名（不含扩展名）。</summary>
    public IReadOnlyList<string> ListProfileNames()
    {
        lock (_lock)
        {
            var dir = ProfilesDir;
            if (!Directory.Exists(dir))
            {
                return Array.Empty<string>();
            }
            return Directory.GetFiles(dir, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(s => !string.IsNullOrEmpty(s))
                .Select(s => s!)
                .OrderBy(s => s)
                .ToList();
        }
    }

    /// <summary>加载指定方案。不存在返回 null。</summary>
    public RoutingProfile? Load(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }
        lock (_lock)
        {
            var path = GetPath(name);
            if (!File.Exists(path))
            {
                return null;
            }
            var json = File.ReadAllText(path);
            var profile = JsonSerializer.Deserialize<RoutingProfile>(json, JsonOptions);
            // 读取旧存档时立即迁移为 Groups 结构（就地修改）
            profile?.MigrateLegacyToGroups();
            return profile;
        }
    }

    /// <summary>保存方案。方案名中的非法文件名字符会被替换。</summary>
    public void Save(RoutingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var safeName = SanitizeFileName(profile.Name);
        if (string.IsNullOrEmpty(safeName))
        {
            safeName = "profile";
            profile.Name = safeName;
        }
        lock (_lock)
        {
            EnsureProfilesDirectory();
            // 保存前统一规范化：迁移旧结构 + 生成 Id
            profile.NormalizeBeforeSave();
            profile.UpdatedAt = DateTime.UtcNow;
            var path = GetPath(safeName);
            var json = JsonSerializer.Serialize(profile, JsonOptions);
            File.WriteAllText(path, json);
        }
    }

    /// <summary>删除指定方案。不存在返回 false。</summary>
    public bool Delete(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }
        lock (_lock)
        {
            var path = GetPath(name);
            if (!File.Exists(path))
            {
                return false;
            }
            File.Delete(path);
            return true;
        }
    }

    private static string GetPath(string name)
        => Path.Combine(ProfilesDir, SanitizeFileName(name) + ".json");

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name.Trim())
        {
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }
        return sb.ToString();
    }
}
