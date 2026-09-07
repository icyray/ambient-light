// SettingsPageAudio.xaml.cs — 拾音设置: 每次 Loaded 从 ConfigStore 重读,
// 变更经 ApplyConfigDebounced 热更+落盘 (200ms 防抖); 音量表订阅 250ms 轮询。
using System.Windows;
using System.Windows.Controls;
using AmbientLight.Interop;
using AmbientLight.Services;

namespace AmbientLight.Views;

public partial class SettingsPageAudio : Page
{
    private bool _loading = true;   // InitializeComponent 解析期即有事件, 初值阶段一律忽略

    // 下拉序 ↔ audioSource 值: 0=回环(默认) 2=麦克风 1=MCU 自拾音
    private static readonly int[] SourceValues = [0, 2, 1];

    public SettingsPageAudio()
    {
        InitializeComponent();
        Controls.WheelScroll.Attach(this);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loading = true;
        LoadFromConfig();
        App.Instance.Poller.Polled += OnPolled;
        _loading = false;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        App.Instance.Poller.Polled -= OnPolled;
    }

    private void LoadFromConfig()
    {
        var dev = ConfigStore.Device;
        int idx = Array.IndexOf(SourceValues, dev.AudioSource);
        SourceCombo.SelectedIndex = idx < 0 ? 0 : idx;
        AgcToggle.IsChecked = dev.AudioAgc;
        GainSlider.Value = Math.Clamp(dev.AudioGain, 0.1, 5.0);
        SoftSlider.Value = Math.Clamp(dev.AudioSoft * 100.0, 0, 100);
        ReleaseSlider.Value = Math.Clamp(dev.AudioRelease * 1000.0, 50, 1000);
        BassSlider.Value = Math.Clamp(dev.AudioBass * 100.0, 0, 100);
        GateSlider.Value = Math.Clamp(dev.AudioGate * 10000.0, 0, 100);
        FollowVolumeToggle.IsChecked = dev.AudioFollowVolume;
        RefreshGainLabel();
        RefreshSoftLabel();
        RefreshReleaseLabel();
        RefreshBassLabel();
        RefreshGateLabel();
    }

    private void OnPolled(ACStatus st)
    {
        LevelBar.Value = st.audioLevel;
        LevelValueText.Text = $"{st.audioLevel}";
        string dev = st.AudioDev;
        AudioDevText.Text = st.audioOk switch
        {
            1 => $"音源: {dev}",
            2 => "音源重建中 (默认输出设备变化/暂不可用)…",
            _ => "音源: — (开启拾音模式后显示)",
        };
    }

    private void RefreshGainLabel() => GainValue.Text = $"×{GainSlider.Value:0.00}";
    private void RefreshSoftLabel() => SoftValue.Text = $"{(int)SoftSlider.Value}%";
    private void RefreshReleaseLabel() => ReleaseValue.Text = $"{(int)ReleaseSlider.Value} ms";
    private void RefreshBassLabel() => BassValue.Text = $"{(int)BassSlider.Value}%";
    private void RefreshGateLabel() => GateValue.Text = $"{GateSlider.Value / 100.0:0.00}%";

    private void OnSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SourceCombo.SelectedIndex < 0) return;
        ConfigStore.Device.AudioSource = SourceValues[SourceCombo.SelectedIndex];
        App.Instance.ApplyConfigDebounced();
    }

    private void OnAgcChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        ConfigStore.Device.AudioAgc = AgcToggle.IsChecked == true;
        App.Instance.ApplyConfigDebounced();
    }

    private void OnGainChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (GainValue != null) RefreshGainLabel();
        if (_loading) return;
        ConfigStore.Device.AudioGain = GainSlider.Value;
        App.Instance.ApplyConfigDebounced();
    }

    private void OnSoftChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SoftValue != null) RefreshSoftLabel();
        if (_loading) return;
        ConfigStore.Device.AudioSoft = SoftSlider.Value / 100.0;
        App.Instance.ApplyConfigDebounced();
    }

    private void OnReleaseChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ReleaseValue != null) RefreshReleaseLabel();
        if (_loading) return;
        ConfigStore.Device.AudioRelease = ReleaseSlider.Value / 1000.0;
        App.Instance.ApplyConfigDebounced();
    }

    private void OnBassChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BassValue != null) RefreshBassLabel();
        if (_loading) return;
        ConfigStore.Device.AudioBass = BassSlider.Value / 100.0;
        App.Instance.ApplyConfigDebounced();
    }

    private void OnGateChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (GateValue != null) RefreshGateLabel();
        if (_loading) return;
        ConfigStore.Device.AudioGate = GateSlider.Value / 10000.0;
        App.Instance.ApplyConfigDebounced();
    }

    private void OnFollowVolumeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        ConfigStore.Device.AudioFollowVolume = FollowVolumeToggle.IsChecked == true;
        App.Instance.ApplyConfigDebounced();
    }
}
