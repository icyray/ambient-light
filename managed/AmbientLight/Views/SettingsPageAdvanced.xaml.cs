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
        // TimePicker 无公开 change 事件, 用 DP 描述符监听
        System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
            Wpf.Ui.Controls.TimePicker.TimeProperty, typeof(Wpf.Ui.Controls.TimePicker))
            .AddValueChanged(AutoOnTimePicker, OnAutoOnTimeChanged);
        Loaded += OnLoaded;
        Unloaded += (_, _) => App.Instance.PatternChanged -= OnPatternChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loading = true;
        var dev = ConfigStore.Device;
        MinBrightnessSlider.Value = Math.Clamp(dev.MinBrightness, 1, 25);
        WaitDispCombo.SelectedIndex = Array.IndexOf(PolicyChoices, dev.WaitDispPolicy) is >= 0 and int i ? i : 0;
        ColorfixToggle.IsChecked = dev.Colorfix;
        SfloorSlider.Value = Math.Clamp(dev.Sfloor, 0, 255);
        AutoStartToggle.IsChecked = ConfigStore.App.AutoStart;
        AutoStartPathText.Text = AutoStart.ShortcutPath;
        StartMinimizedToggle.IsChecked = ConfigStore.App.StartMinimized;
        AutoStartCoreToggle.IsChecked = ConfigStore.Device.AutoStartCore;
        AutoOnToggle.IsChecked = ConfigStore.App.AutoOnEnabled;
        AutoOnTimePicker.Time = TimeSpan.TryParseExact(ConfigStore.App.AutoOnTime, @"hh\:mm",
            null, out var t) ? t : new TimeSpan(17, 0, 0);
        AutoOnTimePicker.IsEnabled = ConfigStore.App.AutoOnEnabled;
        ConfigDirText.Text = $"{ConfigStore.ConfigDir}  ({ConfigStore.ConfigDirSource})";
        VersionText.Text = $"AmbientLight {GetType().Assembly.GetName().Version?.ToString(3)} · AmbientCore v{(AmbientCore.Version() >> 16) & 0xFF}.{(AmbientCore.Version() >> 8) & 0xFF}.{AmbientCore.Version() & 0xFF}";
        _loading = false;

        RefreshPatternButtons();
        App.Instance.PatternChanged += OnPatternChanged;
    }

    private void OnPatternChanged() => RefreshPatternButtons();

    private void RefreshPatternButtons()
    {
        int active = App.Instance.ActivePattern;
        PatternQuadBtn.Appearance = active == 1 ? ControlAppearance.Primary : ControlAppearance.Secondary;
        PatternMagentaBtn.Appearance = active == 2 ? ControlAppearance.Primary : ControlAppearance.Secondary;
    }

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

    // ---- 定时开启 + 校准 -------------------------------------------------
    private void OnAutoOnChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        bool on = AutoOnToggle.IsChecked == true;
        ConfigStore.App.AutoOnEnabled = on;
        AutoOnTimePicker.IsEnabled = on;
        ConfigStore.SaveApp();
        // 立即对齐一次状态 (开关定时功能影响本次会话的启动语义已过, 只影响后续到点拉起)
    }

    private void OnAutoOnTimeChanged(object? sender, EventArgs e)
    {
        if (_loading) return;
        if (AutoOnTimePicker.Time is TimeSpan t)
        {
            ConfigStore.App.AutoOnTime = $"{(int)t.TotalHours:00}:{t.Minutes:00}";
            ConfigStore.SaveApp();
        }
    }

    private void OnCalibrate(object sender, RoutedEventArgs e) => CalibrateWindow.TryOpen();

    // ---- 测试图 ----------------------------------------------------------
    private void OnPatternQuad(object sender, RoutedEventArgs e) => App.Instance.SetPattern(1);

    private void OnPatternMagenta(object sender, RoutedEventArgs e) => App.Instance.SetPattern(2);

    // ---- 配置目录 --------------------------------------------------------
    private void OnOpenConfigDir(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ConfigStore.ConfigDir}\""));
        }
        catch { /* 忽略 */ }
    }
}
