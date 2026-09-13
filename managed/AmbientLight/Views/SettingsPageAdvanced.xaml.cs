// SettingsPageAdvanced.xaml.cs — 高级设置: minB/熄屏策略/colorfix + 测试图 + 配置目录
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using AmbientLight.Interop;
using AmbientLight.Services;
using Wpf.Ui.Controls;

namespace AmbientLight.Views;

public partial class SettingsPageAdvanced : Page
{
    // ComboBox 显示序 (熄灭/最暗/保持) ↔ 配置值 (2/1/0)
    private static readonly int[] PolicyChoices = [2, 1, 0];
    private bool _loading = true;   // InitializeComponent 解析期即有事件, 初值阶段一律忽略

    public SettingsPageAdvanced()
    {
        InitializeComponent();
        Controls.WheelScroll.Attach(this);   // 滚轮滚动由外层滚动器承担
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => ReloadFromConfig();

    /// <summary>把配置读进控件。Loaded 时走一遍; 「恢复默认配置」之后也要再走一遍。
    /// 全程 _loading=true → 各控件的事件处理器一律不回写 (否则会把刚恢复的默认值又覆盖回去)。</summary>
    private void ReloadFromConfig()
    {
        _loading = true;
        var dev = ConfigStore.Device;
        MinBrightnessSlider.Value = Math.Clamp(dev.MinBrightness, 1, 25);
        WaitDispCombo.SelectedIndex = Array.IndexOf(PolicyChoices, dev.WaitDispPolicy) is >= 0 and int i ? i : 0;
        ColorfixToggle.IsChecked = dev.Colorfix;
        SfloorSlider.Value = Math.Clamp(dev.Sfloor, 0, 255);
        AutoStartToggle.IsChecked = ConfigStore.App.AutoStart;
        StartMinimizedToggle.IsChecked = ConfigStore.App.StartMinimized;
        AutoStartCoreToggle.IsChecked = ConfigStore.Device.AutoStartCore;

        string appVer = CurrentVersion;
        uint core = AmbientCore.Version();
        VersionText.Text = $"AmbientLight {appVer} · AmbientCore v{(core >> 16) & 0xFF}.{(core >> 8) & 0xFF}.{core & 0xFF}";
        _loading = false;
    }

    private static string CurrentVersion => typeof(SettingsPageAdvanced).Assembly
        .GetName().Version?.ToString(3) ?? "?";

    private void OnMinBrightnessChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MinBrightnessValue != null) MinBrightnessValue.Text = $"{(int)e.NewValue}";
        if (_loading) return;
        ConfigStore.Device.MinBrightness = (int)e.NewValue;
        App.Instance.ApplyConfigDebounced();
    }

    private void OnWaitDispChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || WaitDispCombo.SelectedIndex < 0) return;
        ConfigStore.Device.WaitDispPolicy = PolicyChoices[WaitDispCombo.SelectedIndex];
        App.Instance.ApplyConfigDebounced();
    }

    private void OnColorfixChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        ConfigStore.Device.Colorfix = ColorfixToggle.IsChecked == true;
        App.Instance.ApplyConfigDebounced();
    }

    private void OnSfloorChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SfloorValue != null) SfloorValue.Text = $"{(int)e.NewValue}";
        if (_loading) return;
        ConfigStore.Device.Sfloor = (int)e.NewValue;
        App.Instance.ApplyConfigDebounced();
    }

    // ---- 开机自启 / 启动收托盘 ------------------------------------------
    private void OnAutoStartChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        bool on = AutoStartToggle.IsChecked == true;
        string? err = on ? AutoStart.Enable() : AutoStart.Disable();
        if (err != null)
        {
            _loading = true;                        // 失败回滚开关显示
            AutoStartToggle.IsChecked = !on;
            _loading = false;
            System.Windows.MessageBox.Show($"开机自启设置失败: {err}", "AmbientLight",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }
        ConfigStore.App.AutoStart = on;
        ConfigStore.SaveApp();
    }

    private void OnStartMinimizedChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        ConfigStore.App.StartMinimized = StartMinimizedToggle.IsChecked == true;
        ConfigStore.SaveApp();
    }

    private void OnAutoStartCoreChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        ConfigStore.Device.AutoStartCore = AutoStartCoreToggle.IsChecked == true;
        ConfigStore.SaveDevice();
    }

    // ---- 校准 ------------------------------------------------------------
    private void OnCalibrate(object sender, RoutedEventArgs e) => CalibrateWindow.TryOpen();

    // ---- 配置目录 --------------------------------------------------------
    private void OnOpenConfigDir(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ConfigStore.ConfigDir}\""));
        }
        catch { /* 忽略 */ }
    }

    // ---- 版本更新 (轮 21) -------------------------------------------------
    // 现状: 软件还没发布到 GitHub, 这里先把 UI 与对话框通道跑通。
    // 接入点 (发布后): GET https://api.github.com/repos/<owner>/<repo>/releases/latest
    //   → 取 JSON 的 tag_name, 与 CurrentVersion 做版本比较;
    //   有新版 → 同一对话框里给出新版号 + Release 页面链接; 无新版 → "已是最新版本"。
    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        await ShowDialogAsync("版本更新",
            $"当前版本 {CurrentVersion}。\n\n"
            + "软件还没有发布到 GitHub Release, 暂时无法检查更新。"
            + "发布之后, 这里会与仓库最新的 Release 版本号比对, 并给出下载入口。",
            closeText: "知道了");
    }

    // ---- 恢复默认配置 (轮 21): 藏在「配置目录」折叠内。破坏性操作, 先确认再动手 ----
    private async void OnRestoreDefaults(object sender, RoutedEventArgs e)
    {
        bool ok = await ShowDialogAsync("恢复默认配置",
            "将把「设置」与「设备配置」全部重置为出厂默认值 —— 亮度、模式、端口、采集与拾音参数、"
            + "灯序校准等都会被覆盖, 且无法撤销。\n\n确定要恢复吗?",
            primaryText: "恢复默认", primaryDanger: true, closeText: "取消");
        if (!ok) return;

        ConfigStore.RestoreDefaults();
        // 开机自启的快捷方式也要跟着回到默认 (与 App.OnStartup 的自愈规则一致, 否则会残留)
        if (!ConfigStore.App.AutoStart && AutoStart.IsActive()) AutoStart.Disable();
        App.Instance.ApplyConfig();     // 默认值立即下发给当前会话 (灯带随之变化)
        ReloadFromConfig();             // 本页控件回到默认值。其余页面未做导航缓存, 切过去时会重读
    }

    /// <summary>统一走窗口里的 ContentDialogHost。返回 true = 用户点了主按钮 (确认)。
    /// 宿主取不到 (无窗口宿主, 例如探针里) 时直接返回 false, 不做任何动作。</summary>
    private async Task<bool> ShowDialogAsync(string title, string content,
        string? primaryText = null, bool primaryDanger = false, string? closeText = null)
    {
        if (Wpf.Ui.Controls.ContentDialogHost.GetForWindow(Window.GetWindow(this)) is not { } host)
            return false;

        var dlg = new ContentDialog(host)
        {
            Title = title,
            Content = content,
            CloseButtonText = string.IsNullOrEmpty(closeText) ? "关闭" : closeText,
            // 默认焦点一律放「取消 / 关闭」: 恢复默认是不可撤销的破坏性操作, 回车不该替用户确认
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
