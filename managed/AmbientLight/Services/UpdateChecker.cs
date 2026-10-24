// UpdateChecker.cs — 检查 GitHub 上有没有更新版本 (只读版本号, 不下载、不自动安装)
//
// 只做一件事: 拉 `releases/latest` 的 tag_name, 与当前程序集版本比大小。
// 有新版本 → 由调用方给出 Release 页面链接, 其余交给用户 (便携软件, 不做静默更新)。
//
// 为什么单独抽一个类: 版本比较是**纯函数**, 可以在无网络的探针里跑真值表;
// 网络部分只有薄薄一层, 出错也只返回 Failed, 不抛给 UI。
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace AmbientLight.Services;

public enum UpdateStatus
{
    /// <summary>已是最新 (或本地版本更新, 例如自己编译的开发版)。</summary>
    UpToDate,
    /// <summary>远端有更新的正式版。</summary>
    Newer,
    /// <summary>网络/接口失败 —— 只影响"这次没查成", 不影响程序其它功能。</summary>
    Failed,
}

/// <param name="LatestVersion">远端版本号 (去掉前缀 v); 失败时为 null</param>
/// <param name="Url">Release 页面地址; 失败时为 null (调用方可用 <see cref="UpdateChecker.ReleasesUrl"/> 兜底)</param>
/// <param name="Detail">失败原因 (调试用, 不直接给用户看)</param>
public sealed record UpdateResult(UpdateStatus Status, string? LatestVersion = null,
                                  string? Url = null, string? Detail = null);

public static class UpdateChecker
{
    // 仓库地址写死: 这个程序只服务这一个仓库, 不做可配置的更新源 (可配置 = 多一个被改坏的点)。
    private const string Owner = "icyray";
    private const string Repo = "ambient-light";

    /// <summary>Releases 列表页 (查不到时给用户的兜底入口)。</summary>
    public const string ReleasesUrl = "https://github.com/icyray/ambient-light/releases";

    /// <summary>仓库首页 (高级页页脚的版本号指向这里)。</summary>
    public const string RepoUrl = "https://github.com/icyray/ambient-light";

    private static readonly Uri LatestApi =
        new($"https://api.github.com/repos/{Owner}/{Repo}/releases/latest");

    /// <summary>查最新正式版。永不抛异常: 出错返回 <see cref="UpdateStatus.Failed"/>;
    /// 仓库还没发过正式 Release (404) 视为 <see cref="UpdateStatus.UpToDate"/>。
    /// 注意 GitHub 的 `releases/latest` 不含预发布与草稿, 正合"只提示正式版"的口径。</summary>
    public static async Task<UpdateResult> CheckAsync(string currentVersion, CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            // GitHub API 强制要求 UA, 缺了直接 403
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"AmbientLight/{currentVersion}");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            using var resp = await http.GetAsync(LatestApi, ct);
            // 404 = 还没有正式 Release (或只有预发布) → 当作"没有可更新的版本", 而不是报错
            if (resp.StatusCode == HttpStatusCode.NotFound)
                return new(UpdateStatus.UpToDate);
            if (!resp.IsSuccessStatusCode)
                return new(UpdateStatus.Failed, Detail: $"HTTP {(int)resp.StatusCode}");

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("tag_name", out var tagEl) ||
                tagEl.GetString() is not { Length: > 0 } tag)
                return new(UpdateStatus.Failed, Detail: "no tag_name");

            string url = doc.RootElement.TryGetProperty("html_url", out var urlEl)
                ? urlEl.GetString() ?? ReleasesUrl : ReleasesUrl;

            return IsNewer(tag, currentVersion)
                ? new(UpdateStatus.Newer, Normalize(tag), url)
                : new(UpdateStatus.UpToDate, Normalize(tag));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                      or JsonException or InvalidOperationException)
        {
            return new(UpdateStatus.Failed, Detail: ex.GetType().Name);
        }
    }

    /// <summary>远端版本是否比当前版本新。任一侧解析不出来 → false (宁可漏报, 不可误报)。</summary>
    public static bool IsNewer(string? latest, string? current)
        => TryParse(latest, out var l) && TryParse(current, out var c) && l.CompareTo(c) > 0;

    /// <summary>解析 `v0.5.5` / `0.5.5` / `0.5.5-rc1` / `0.6` —— 只取前三段数字, 后缀忽略。</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string s = text.Trim();
        if (s.Length > 0 && (s[0] is 'v' or 'V')) s = s[1..];

        // 只吃前导的 "数字与点", 遇到 '-'/'+'/' ' 等后缀立刻停
        int end = 0;
        while (end < s.Length && (char.IsAsciiDigit(s[end]) || s[end] == '.')) end++;
        s = s[..end].TrimEnd('.');
        if (s.Length == 0) return false;

        var seg = s.Split('.');
        int[] n = new int[3];
        for (int i = 0; i < 3; i++)
        {
            if (i >= seg.Length) { n[i] = 0; continue; }        // "0.6" → 0.6.0
            if (!int.TryParse(seg[i], out n[i]) || n[i] < 0) return false;
        }
        // 统一成三段: 4 段tag (0.5.5.1) 只比前三段, 避免 Version 把缺省 Revision 当 -1
        version = new Version(n[0], n[1], n[2]);
        return true;
    }

    private static string Normalize(string tag) => tag.Trim().TrimStart('v', 'V');
}
