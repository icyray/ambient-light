// AppDialogs.cs — 统一对话框 (轮 34 补)
//
// 为什么不要用 `MessageBox`: 它是系统样式的 Win32 弹窗, 跟 Fluent 界面两套观感
// (而且不随主题变)。应用里的"提示 / 警告 / 确认"一律走这里 → 主窗口的 ContentDialogHost。
//
// ⚠️ 唯一的例外是**崩溃兜底**(App.xaml.cs 的 DispatcherUnhandledException) —— 那条路径上
// UI 线程已经在异常里, 再走 ContentDialog 有二次出错的风险, 所以那里保留 MessageBox。
using System.Windows;
using AmbientLight.Localization;
using Wpf.Ui.Controls;

namespace AmbientLight.Controls;

public static class AppDialogs
{
    /// <summary>在主窗口的 ContentDialogHost 上弹一个 ContentDialog。
    /// 返回 true = 用户点了主按钮 (需要确认的场景才给主按钮)。
    /// 拿不到主窗/宿主时 (无窗口宿主, 例如探针里) 直接返回 false, 不做任何动作。</summary>
    public static async Task<bool> ShowAsync(string title, string content,
        string? primaryText = null, bool primaryDanger = false, string? closeText = null)
    {
        var app = Application.Current;
        if (app is null) return false;
        // 可能从托盘菜单 / 非 UI 线程进来 → 先切回 UI 线程
        if (!app.Dispatcher.CheckAccess())
            return await app.Dispatcher.InvokeAsync(
                () => ShowAsync(title, content, primaryText, primaryDanger, closeText)).Task.Unwrap();

        if (App.Instance?.MainWin is not { } win) return false;
        if (ContentDialogHost.GetForWindow(win) is not { } host) return false;

        var dlg = new ContentDialog(host)
        {
            Title = title,
            Content = content,
            CloseButtonText = string.IsNullOrEmpty(closeText) ? Loc.T("Common.Close") : closeText,
            // 默认焦点一律放「关闭 / 取消」: 主按钮都是"删除/重置/恢复"这类动作,
            // 回车不该替用户确认。
            DefaultButton = ContentDialogButton.Close,
        };
        if (primaryText != null)
        {
            dlg.PrimaryButtonText = primaryText;
            if (primaryDanger) dlg.PrimaryButtonAppearance = ControlAppearance.Danger;
        }
        return await dlg.ShowAsync() == ContentDialogResult.Primary;
    }
}
