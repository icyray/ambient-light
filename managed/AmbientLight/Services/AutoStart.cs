// AutoStart.cs — 开机自启: 在 shell:startup 写/删 .url 快捷方式。
// 便携友好, 不动注册表 (PROJECT_PLAN §2 既定决策, 厂商 Ansnake 同构做法)。
// app.json 的 AutoStart 是唯一事实源; App 启动时按它自愈 (快捷方式丢失/残留)。
using System.IO;
using AmbientLight.Localization;

namespace AmbientLight.Services;

public static class AutoStart
{
    public static string ShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "AmbientLight.url");

    /// <summary>快捷方式当前是否在位 (且指向本 exe)</summary>
    public static bool IsActive()
    {
        try
        {
            return File.Exists(ShortcutPath) &&
                   File.ReadAllText(ShortcutPath).Contains(ShortcutTarget(), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>启用自启。返回 null = 成功, 否则为错误文本</summary>
    public static string? Enable()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
                return Loc.T("App.AutoStart.NoExe");
            // file:/// URI: 空格等特殊字符自动转义, 登录时经 ShellExecute 启动
            File.WriteAllText(ShortcutPath, $"[InternetShortcut]\nURL={new Uri(exe).AbsoluteUri}\n");
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>停用自启 (快捷方式不存在也算成功)。返回 null = 成功</summary>
    public static string? Disable()
    {
        try
        {
            if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static string ShortcutTarget()
    {
        var exe = Environment.ProcessPath ?? "";
        return Path.GetFileName(exe);
    }
}
