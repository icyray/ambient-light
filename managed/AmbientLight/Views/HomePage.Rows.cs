// HomePage.Rows.cs — 指标条 / 布局预览 (灯环) / 显示器行 / 设备行。
// (HomePage partial 之一: 核心状态在 HomePage.xaml.cs, 本文件只放这一块职责)
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AmbientLight.Controls;
using AmbientLight.Interop;
using AmbientLight.Services;
using Wpf.Ui.Controls;

namespace AmbientLight.Views;

public partial class HomePage : Page
{
    // ---- §5 运行指标 (轮 27 用户定稿: 双层 —— 标签在上 / 数值在下) ----
    /// <summary>亮度参数对这个模式有意义吗 —— **纯函数** (探针打真值表)。
    /// MCU 特效的帧由**固件**渲染, 主机只发命令 → 亮度对我们没有落点 → UI 要禁用亮度滑块
    /// (用户 2026-10-08 提的: 免得用户以为那能调)。追光/拾音/纯色都可用
    /// (拾音时它是"最高亮度上限", 乘在音量包络上)。</summary>
    public static bool BrightnessEnabled(int mode) => !AmbientCore.IsMcuMode(mode);

    /// <summary>指标集随模式换。**纯函数** —— 探针用它打真值表: 这类"写错不报错、只会静默显示错指标"
    /// 的规则最该被回归网住。running=false → 空集 (显示一行灰字)。
    /// **不含 CPU**: `ACStatus.cpuPercent` 从 M1 起就标着"预留", native 侧从来没赋过值 (恒 0.0%);
    /// 为了一个没人会照着行动的数字去核心加采样线程, 不值得 → 拿掉。</summary>
    public static string[] MetricKeysForMode(int mode, bool running)
    {
        if (!running) return [];
        if (mode == 0) return ["proc", "wire", "fps"];
        if (mode == 1) return ["audio", "wire"];
        if (mode == 11) return ["wire", "leds", "bright"];
        if (AmbientCore.IsMcuMode(mode)) return ["cmd", "wire", "leds"];
        return ["wire"];
    }

    /// <summary>指标标签 (5.3 i18n 文案源)</summary>
    public static string MetricLabel(string key) => key switch
    {
        "proc" => "处理",
        "wire" => "串口写入",
        // 原叫"帧率"/「变化/s」: 它的物理含义是屏幕变化速率, 叫帧率会让人以为掉到 30 就是坏了
        "fps" => "画面变化",
        "audio" => "音量",
        "leds" => "灯珠",
        "bright" => "亮度",
        "cmd" => "命令",
        _ => key,
    };

    private void UpdateMetrics(ACStatus st)
    {
        var keys = MetricKeysForMode(ConfigStore.Device.ModeIndex, App.Instance.CoreRunning);
        // 灯带没开 → 整块高度动画收掉 (与拾音页「输入/输出设备」同一套机制), 不留一行占位
        AnimatedVisibility.SetExpanded(MetricsCard, keys.Length > 0);
        string sig = string.Join(",", keys);
        if (sig != _metricsSig)
        {
            _metricsSig = sig;
            RebuildMetrics(keys);
        }
        if (keys.Length == 0) return;

        var dev = ConfigStore.Device;
        for (int i = 0; i < _metricItems.Count && i < keys.Length; i++)
        {
            // 不适用 / 取不到一律显示 "—" (0 会被读成"卡住了", 空着又像没填)
            _metricItems[i].Value = keys[i] switch
            {
                "proc" => st.state == AmbientCore.StateRunning ? $"{st.procMs:F2} ms" : "—",
                "wire" => st.state == AmbientCore.StateRunning ? $"{st.wireMs:F2} ms" : "—",
                "fps" => st.state == AmbientCore.StateRunning ? $"{st.fps:F1}/s" : "—",
                "audio" => $"{st.audioLevel}/255",
                "leds" => $"{dev.LedCount}",
                "bright" => $"{(int)Math.Round(dev.Brightness * 100)}%",
                "cmd" => st.fps > 0 ? $"{st.fps:F1}/s" : "—",
                _ => "—",
            };
        }
        // 不做健康着色 (轮 27 用户定稿): 处理/串口耗时没有公认的绝对标准, 涂绿涂红只会误导
    }

    /// <summary>指标条的单格数据: 标签 + 值。值走 INPC → XAML 模板里的 {Binding Value} 自动刷新
    /// (code-behind 不再直接改 TextBlock)。</summary>
    public sealed class MetricCell : System.ComponentModel.INotifyPropertyChanged
    {
        private string _value = "—";

        public string Label { get; }

        public MetricCell(string label) => Label = label;

        public string Value
        {
            get => _value;
            set
            {
                _value = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Value)));
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>重建指标条 (指标集变化时)。结构在 XAML 模板里 (双层: 标签上 / 数值下, 等宽分栏);
    /// 这里只把"这一模式的指标键"变成数据项, 并切换空态占位。</summary>
    private void RebuildMetrics(string[] keys)
    {
        var items = new List<MetricCell>(keys.Length);
        foreach (var k in keys) items.Add(new MetricCell(MetricLabel(k)));
        _metricItems = items;
        MetricsPanel.ItemsSource = items;
        bool empty = keys.Length == 0;
        MetricsPanel.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        MetricsEmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- §7 布局预览 (折叠) -----------------------------------------
    private void UpdateRing(ACStatus st, int m0, int m1, int m2, int m3)
    {
        var dev = ConfigStore.Device;
        long sig = dev.ModeIndex | ((long)dev.LedCount << 5) | ((long)dev.Sides << 15)
                | ((long)dev.Direction << 18) | ((long)st.state << 20)
                | (((long)dev.LedRotate + 256) << 24);
        if (sig != _lastRingSig)
        {
            _lastRingSig = sig;
            var (mw, mh) = GetMonitorSize(dev.MonitorIndex);   // 预览按真实长宽比布局
            Ring.Configure(dev.LedCount, dev.Sides, dev.Direction, dev.LedRotate, mw, mh);
            LayoutSummary.Text = $"{dev.LedCount} 灯 · {dev.Sides} 边";
        }
        // 中心区只放**静态几何参数**: 展开这个折叠的人目的是核对灯序/边数/偏移, 要的就是几何事实。
        string center = $"{dev.LedCount} 灯 · {dev.Sides} 边 · {(dev.Direction == 0 ? "逆时针" : "顺时针")} · " +
                        $"灯序偏移 {dev.LedRotate:+0;-0;0}";
        if (center != _lastCenter)
        {
            _lastCenter = center;
            Ring.SetHint(center);
        }

        // 折叠时不重绘 (几何环收进折叠后, 原来 4Hz 重绘 60 个 Ellipse 的开销消失); 窗口不可见同理
        if (!LayoutCard.IsExpanded || !IsUiVisible()) return;
        if (st.state == AmbientCore.StateRunning && AmbientCore.IsHostFrameMode(dev.ModeIndex))
        {
            Ring.Update(st.CopyLedRgb(), dev.LedCount);
        }
        else if (st.state is AmbientCore.StateRunning or AmbientCore.StateStopped)
        {
            // 拾音/固件特效没有逐灯颜色, 停着也没有 → 清空, 不能留着上一次的画面 (会误导)
            Ring.Clear();
        }
        // WAIT_DISP/DEV_LOST: 保留最后画面 (串口策略帧/重连语义与之一致)
    }

    /// <summary>选中显示器的实际物理分辨率 (预览长宽比用); 枚举失败按 16:9</summary>
    private static (int w, int h) GetMonitorSize(int index)
    {
        try
        {
            var mons = AmbientCore.GetMonitors();
            if (mons.Length > 0)
            {
                int i = Math.Clamp(index, 0, mons.Length - 1);
                if (mons[i].width > 0 && mons[i].height > 0)
                    return (mons[i].width, mons[i].height);
            }
        }
        catch { }
        return (16, 9);
    }

    // ---- 模式/设备图标与名称 ------------------------------------------
    private static SymbolRegular DevIcon(int t) => t switch
    {
        AmbientCore.DevScreen => SymbolRegular.Desktop24,
        AmbientCore.DevFan => SymbolRegular.PuzzleCube24,
        AmbientCore.DevDesk => SymbolRegular.RectangleLandscape24,
        AmbientCore.DevSound => SymbolRegular.RectanglePortrait24,
        _ => SymbolRegular.UsbPlug24,
    };

    private static string DevTypeName(int t) => t switch
    {
        AmbientCore.DevScreen => "屏幕灯带",
        AmbientCore.DevFan => "ARGB 风扇",
        AmbientCore.DevDesk => "桌面灯条",
        AmbientCore.DevSound => "立柱氛围灯",
        _ => "未知",
    };

    private void OnDevicesChanged() => _ = RefreshDevicesAsync();

    /// <summary>会话状态变化 → 重刷设备行 (轮 33)。托盘菜单开关会让会话整体消失/重建;
    /// 灯带物理断开会让会话转 DEV_LOST —— 两种都不经过主页的按钮, 之前这一行不会更新,
    /// 用户看到的还是"运行中"。</summary>
    private void OnCoreStateChanged(int oldState, int newState) => _ = RefreshDevicesAsync();

    /// <summary>设备 / 显示器 -------------------------------------------------
    private void OnRefreshDevices(object sender, RoutedEventArgs e) => _ = RefreshDevicesAsync();

    private void OnRefreshMonitorsClick(object sender, RoutedEventArgs e) => RefreshMonitors();

    // 语义胶囊配色 (M5.2): 落在**卡片底**上 → 按当前主题取深/浅两套 (banner 那套由 PillLook 管,
    // 它点亮时不看主题、恒用深色套)。见 Services/ThemePalette.cs。
    private static ToneColors ToneBrush(Tone tone) => ThemePalette.Of(tone, ThemeService.IsDark);

    /// <summary>语义色对 (M5.2, 随主题深浅) 的胶囊 —— 与下面那个"指定画刷/资源键"的版本共存,
    /// 用具名 `left:` 调用时不会歧义 (那个版本 bg/fg 都是必填位置参数)。</summary>
    private static Border Pill(string text, ToneColors colors, double left = 0)
        => Pill(text, Frozen(colors.Background), Frozen(colors.Foreground), left: left);

    private static Border Pill(string text, Brush? bg, Brush? fg,
                               string? bgKey = null, string? fgKey = null, double left = 0)
    {
        var tb = new System.Windows.Controls.TextBlock { Text = text, FontSize = 10.5 };
        if (fg != null) tb.Foreground = fg;
        else if (fgKey != null) tb.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, fgKey);
        var b = new Border
        {
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(7, 1, 7, 1),
            Margin = new Thickness(left, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = tb,
        };
        if (bg != null) b.Background = bg;
        else if (bgKey != null) b.SetResourceReference(Border.BackgroundProperty, bgKey);
        return b;
    }

    private void RefreshMonitors()
    {
        if (MonitorsPanel == null) return;
        try
        {
            var mons = AmbientCore.GetMonitors();
            int cur = ConfigStore.Device.MonitorIndex;
            var items = new List<MonitorRowItem>(mons.Length);
            for (int i = 0; i < mons.Length; i++) items.Add(MakeMonitorRow(i, mons[i], i == cur));
            MonitorsList.ItemsSource = items;
            // 空态/提示文本是**动态状态** → 显隐仍归 code-behind (文本与结构在 XAML)
            bool any = items.Count > 0;
            MonitorsStateText.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            if (!any) MonitorsStateText.Text = "未枚举到显示器";
            // 选择器不在这里重复做 (已在「采集」页) —— 这里只做"正在采集的是哪台"的识别
            MonitorsHintText.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            MonitorsList.ItemsSource = null;
            MonitorsStateText.Text = $"枚举失败: {ex.Message}";
            MonitorsStateText.Visibility = Visibility.Visible;
            MonitorsHintText.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>显示器行的数据 (XAML 模板 MonitorRowTemplate 消费)。
    /// 「正在采集」的配色由 ThemePalette 纯函数按当前主题给, 与设备行胶囊同一套。</summary>
    public sealed class MonitorRowItem
    {
        public int Number { get; init; }
        public string ResolutionText { get; init; } = "";
        public string AdapterText { get; init; } = "";
        public bool IsPrimary { get; init; }
        public bool Capturing { get; init; }
        public Brush? CaptureBg { get; init; }
        public Brush? CaptureFg { get; init; }
    }

    private static MonitorRowItem MakeMonitorRow(int index, ACMonitorInfo m, bool capturing)
    {
        var tone = ThemePalette.Of(Tone.Blue, ThemeService.IsDark);
        return new MonitorRowItem
        {
            Number = index + 1,
            ResolutionText = m.width > 0 && m.height > 0 ? $"{m.width}×{m.height}" : "分辨率未知",
            AdapterText = m.Adapter,
            IsPrimary = m.primary != 0,
            Capturing = capturing,
            CaptureBg = Frozen(tone.Background),
            CaptureFg = Frozen(tone.Foreground),
        };
    }

    private async Task RefreshDevicesAsync()
    {
        if (DevicesPanel == null) return;
        // 设备枚举 = 逐个"独占打开端口 + 握手", 绝不能并发 (会互抢端口, 还会和会话抢)。
        // 状态变化可能连着来 (断开→重连) → 排队重跑一次, 而不是丢掉这次刷新。
        if (_devicesRefreshing)
        {
            _devicesRefreshQueued = true;
            return;
        }
        _devicesRefreshing = true;
        try
        {
            await RefreshDevicesCoreAsync();
        }
        finally
        {
            _devicesRefreshing = false;
            if (_devicesRefreshQueued)
            {
                _devicesRefreshQueued = false;
                _ = RefreshDevicesAsync();
            }
        }
    }

    /// <summary>**设备行 ↔ 会话的配对** —— 纯函数, 探针打真值表 (轮 34 补 7)。
    /// 顺序 (先到先得):
    ///   ① **设备 key 相同** —— 最可靠。会话连不上端口时 `ACStatus.comPort` 是**空的**
    ///      (端口只在握手成功时才回填), 光按端口配就认不出"这一行正是那个会话" →
    ///      行会一直显示乐观的「就绪」, 而 banner (按 key 找 slot) 早已显示"正在尝试重连"。
    ///      用户实测: 手动指定一个"在位但不是灯带"的端口后开启 → 行显示「就绪」就是这个原因。
    ///   ② 端口相同 (会话已连上, 或枚举探测没拿到类型时)。
    ///   ③ 兜底: 端口看起来被占用 + **恰有一个**非停止会话 → 按归属处理
    ///      (刚 Start 的会话端口/类型都还没回填)。多个 → 无法归属, 返回 -1。
    /// 返回 -1 = 这一行没有归属的会话。
    /// `candidates` = (slot, 该 slot 的设备 key, 会话端口, 会话状态), 由调用方取一次。</summary>
    public static int MatchSessionSlot(string rowKey, string rowPort, bool portInUse,
        IReadOnlyList<(int Slot, string Key, string SessionPort, int State)> candidates)
    {
        for (int i = 0; i < candidates.Count; i++)
            if (candidates[i].State != AmbientCore.StateStopped && candidates[i].Key == rowKey)
                return candidates[i].Slot;

        if (rowPort.Length > 0)
            for (int i = 0; i < candidates.Count; i++)
                if (candidates[i].State != AmbientCore.StateStopped
                    && string.Equals(candidates[i].SessionPort, rowPort, StringComparison.OrdinalIgnoreCase))
                    return candidates[i].Slot;

        if (!portInUse) return -1;
        int only = -1;
        for (int i = 0; i < candidates.Count; i++)
        {
            if (candidates[i].State == AmbientCore.StateStopped) continue;
            if (only >= 0) return -1;
            only = candidates[i].Slot;
        }
        return only;
    }

    private async Task RefreshDevicesCoreAsync()
    {
        DevicesPanel.Children.Clear();
        DevicesPanel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "探测中… (打开串口握手, ~0.2s/台)",
            Opacity = 0.6,
            FontSize = 12,
        });
        try
        {
            var devices = await Task.Run(AmbientCore.GetDevices).ConfigureAwait(true);
            DevicesPanel.Children.Clear();
            if (devices.Length == 0)
            {
                DevicesPanel.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = "未发现灯带 (VID_2E3C & PID_5740); 检查 USB 连接, 并确认 Ansnake 未占用端口。\n" +
                           "换芯片或 USB 转串口桥接的灯带, 可在「高级 → 连接」里手动指定端口。",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                });
                return;
            }
            // 会话快照取**一次** (原来是每行每 slot 各取一次 AC_GetStatusEx)。配对规则见 MatchSessionSlot。
            var statusBySlot = new Dictionary<int, ACStatus>();
            var sessions = new List<(int Slot, string Key, string SessionPort, int State)>();
            foreach (var (slot, key) in App.Instance.Slots)
            {
                var st0 = AmbientCore.GetStatusEx(slot);
                statusBySlot[slot] = st0;
                sessions.Add((slot, key, st0.ComPort, st0.state));
            }

            foreach (var d in devices)
            {
                // 本核心会话占用的端口必然探测为 [端口被占用][未应答] —
                // 类型/编号/运行态改从该会话的 ACStatus 取 (配对规则见 MatchSessionSlot 的注释)
                string rowKey = $"{d.devType}_{d.devId}";
                int sessSlot = MatchSessionSlot(rowKey, d.ComPort ?? "", d.inUse == 1, sessions);
                ACStatus? sess = sessSlot >= 0 ? statusBySlot[sessSlot] : null;
                DevicesPanel.Children.Add(BuildDeviceRow(d, sess, sessSlot));
            }
        }
        catch (Exception ex)
        {
            DevicesPanel.Children.Clear();
            DevicesPanel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = $"探测失败: {ex.Message}",
                FontSize = 12,
            });
        }
    }

    /// <summary>设备行胶囊的色调 (与 banner 同一套色板)</summary>
    public enum PillTone { Gray, Blue, Green, Amber, Red }

    /// <summary>设备行的语义胶囊 —— **纯函数**, 探针打真值表。
    /// 关键: 有会话时**必须看会话状态**, 不能只看"slot 存在"。
    /// 轮 33 用户反馈的两个现象都由这条规则解释:
    ///   · 灯带被拔掉 → 会话转 DEV_LOST, 但 slot 还在 → 旧代码一律显示"运行中";
    ///   · 托盘菜单关灯 → 会话整体消失, 状态转 STOPPED, 但当时没有刷新信号, 行还是旧的。
    /// 判据里 `hasSlot` = 该端口归属本核心的某个会话。
    ///
    /// 端口相关的两条 (轮 34 补 6, 用户反馈"手动指定端口后胶囊还显示就绪"):
    ///   · `portMissing` = 配置里手动指定的那个口**本机不存在** → 「端口不在位」
    ///     (不用点开启就知道连不上; 数据源是注册表 SERIALCOMM, 一次读很便宜);
    ///   · `manualPort` = 配置里手动指定了端口 → DEV_LOST 时文案改成「指定端口无应答」
    ///     (原来的「灯带不在位」暗示"灯带被拔了", 而真实原因往往是"指定的口不是灯带")。
    /// ⚠️ 故意**不做**"端口在位但不是灯带"的预检: 那得真的打开端口试握手 (~1s + 独占端口),
    /// 不该出现在 4Hz 的列表刷新里 —— 那种只能等开启后由诊断说明。</summary>
    public static (string Label, PillTone Tone) DevicePill(
        bool hasSlot, int state, bool inUse, int devType, bool likelyOurs,
        bool manualPort = false, bool portMissing = false)
    {
        if (hasSlot)
        {
            return state switch
            {
                AmbientCore.StateStarting => ("连接中…", PillTone.Blue),
                AmbientCore.StateRunning => ("运行中", PillTone.Green),
                // 锁屏/熄屏: 串口还连着, 只是画面源不可用
                AmbientCore.StateWaitDisp => ("屏幕不可用", PillTone.Gray),
                // 拔线/端口消失: 会话会自动重连, 但此刻灯带确实不在位。
                // 手动指定端口时换文案: 那不是"灯带被拔了", 而是"指定的口连不上"。
                AmbientCore.StateDevLost => (manualPort ? "指定端口无应答" : "灯带不在位", PillTone.Amber),
                AmbientCore.StateError => ("异常", PillTone.Red),
                _ => ("已停止", PillTone.Gray),
            };
        }
        if (portMissing) return ("端口不在位", PillTone.Amber);
        if (likelyOurs) return ("连接中…", PillTone.Blue);
        if (inUse) return ("端口被占用", PillTone.Amber);
        if (devType < 0) return ("未应答", PillTone.Amber);
        return ("就绪", PillTone.Gray);
    }

    /// <summary>设备行第二行的**端口**文案 —— 纯函数, 探针打真值表 (轮 34 补 5)。
    /// 要显示的是"**实际会用到**的那个口":
    ///   ① **配置里手动指定了** → 配置端口 + 「手动指定」标记 (优先级最高 —— 它是"必须用它";
    ///      会话端口只是"此刻恰好在用它": 改了端口后 native 会关掉当前口重连,
    ///      所以显示配置端口才是用户接下来会看到的结果; 也免得"刚改完端口, 行里还写着旧口");
    ///   ② 没指定但**有会话** → 会话端口 (核心正在用的事实);
    ///   ③ 都没有 → 枚举端口 (自动探测: 两台同型号时"谁先被占谁先分", 它只代表"可能连的那个")。
    /// 为什么必须有 ②③ 的区分: 以前无条件显示枚举端口, 于是"高级页给这台指定了 COM4"时,
    /// 行里写着 COM5、点开启却去连 COM4 —— **界面在说谎** (用户实测反馈)。
    /// 注: 端口是**每设备**配置 (高级页改的是当前选中那台), 不是全局设置。
    ///
    /// `Manual` = 配置里有端口 (文字加「手动指定」标记);
    /// `Warn` = 配置端口**与本行枚举到的端口不一致**才转警示色 ——
    /// 两根同型号灯带各钉自己的口、且枚举到的正是那个口时不该无谓地亮警示 (那是正常状态)。
    /// 大小写不敏感 (`com4` / `COM4` 视为同一个口)。</summary>
    public static (string Text, bool Manual, bool Warn) PortLabel(
        string? sessionPort, string? configuredPort, string? enumeratedPort)
    {
        string cfg = configuredPort?.Trim() ?? "";
        string enumPort = enumeratedPort?.Trim() ?? "";
        if (cfg.Length > 0)
        {
            bool same = enumPort.Length > 0
                && string.Equals(cfg, enumPort, StringComparison.OrdinalIgnoreCase);
            return ($"{cfg} (手动指定)", true, !same);
        }
        string sess = sessionPort?.Trim() ?? "";
        if (sess.Length > 0) return (sess, false, false);
        return (enumPort, false, false);
    }

    /// <summary>胶囊色调 → 画刷 (灰=默认底色/次要字, 其余用语义色对; 色对随主题深浅)</summary>
    private static Border TonePill(string text, PillTone tone) => tone switch
    {
        PillTone.Green => Pill(text, ToneBrush(Tone.Green), left: 8),
        PillTone.Amber => Pill(text, ToneBrush(Tone.Amber), left: 8),
        PillTone.Blue => Pill(text, ToneBrush(Tone.Blue), left: 8),
        PillTone.Red => Pill(text, ToneBrush(Tone.Red), left: 8),
        _ => Pill(text, null, null, "ControlFillColorDefaultBrush", "TextFillColorSecondaryBrush", left: 8),
    };

    // 每台设备一行 (§6): 类型图标 + 名称 + 语义胶囊 + 右侧动作; COM/供电/灯数下沉第二行小字。
    // 「当前」设备用左侧 3px 强调条与强调色标签标出 (与模式卡选中态同语言)。
    // sess = 该端口被本核心会话占用时的会话状态 (探测拿不到握手, 类型取自会话);
    // sessSlot = 那个会话的 slot —— 会话刚起时 devType 还没回填 (=-1), 组不出正确的设备 key,
    // 必须用 slot 反查 (否则"连接中"那一行点开启会去 StartDevice("-1_-1"))。
    // **public 是给探针用的**: 第二行的端口文案要断"接线"(配置里的端口有没有真的传到行里) ——
    // 纯函数真值表挡不住调用点写错 (轮 34 补 5 就是调用点多套了一个 managedHere 条件)。
    public UIElement BuildDeviceRow(ACDeviceInfo d, ACStatus? sess, int sessSlot)
    {
        bool managedHere = sess != null;
        // 类型/编号: **两边都可能还没回填** —— 会话没握手成功时它的 devType 是 -1,
        // 而"我们的会话正占着那个口"时枚举也探测不到 (probe 拿不到握手)。
        // 所以谁有值用谁, 而不是"有会话就一律信会话" (轮 34 补 7: 改成按 key 配对后,
        // 会出现"会话 devType=-1 但枚举认得出类型"的组合, 再照抄会话就会退化成"未知 · 读取中")。
        int devType = sess?.devType >= 0 ? sess!.Value.devType : d.devType;
        int devId = sess?.devId >= 0 ? sess!.Value.devId : d.devId;
        int sessState = sess?.state ?? AmbientCore.StateStopped;
        bool usable = managedHere || (d.devType >= 0 && d.inUse == 0);
        int slot = managedHere ? sessSlot : -1;
        bool connecting = managedHere && devType < 0;
        // 端口被占着但会话还没登记 → 极可能是"我们自己正在起"(应用启动时的那次总开启):
        // 会话线程是异步起的, 握手要先独占端口 → 这中间 `_slots` 里没有条目。
        // 不区分就会在首次打开时显示"未知 · id -1 + 端口被占用"(轮 27 用户实测现象)。
        bool likelyOurs = !managedHere && d.inUse == 1
                          && (App.Instance.CoreRunning || App.Instance.Starting);
        string key = managedHere
            ? (App.Instance.KeyOf(sessSlot) ?? $"{devType}_{devId}")
            : $"{devType}_{devId}";
        bool selected = usable && ConfigStore.SelectedKey == key;

        // 端口信息要在胶囊之前算好 (胶囊的文案/色调与它有关, 见 DevicePill 的两条端口规则)。
        // 只用查得到的配置, 不 GetOrCreate —— 光是"看一眼设备列表"不该给没见过的设备建配置文件。
        bool hasCfg = ConfigStore.Devices.TryGetValue(key, out var ds);
        string cfgPort = hasCfg ? (ds!.ComPort ?? "").Trim() : "";
        bool manualPort = cfgPort.Length > 0;
        // "在位"= 注册表 SERIALCOMM 里有这个口 (与高级页端口下拉同一数据源)。在位 ≠ 是灯带 ——
        // 那个只有在真去握手时才知道, 不在列表刷新里做 (见 DevicePill 的注释)。
        bool portMissing = manualPort && !SerialPorts.All()
            .Any(p => string.Equals(p, cfgPort, StringComparison.OrdinalIgnoreCase));

        // 语义胶囊 (轮 33 起口径 = 会话状态; 轮 34 补 6 起叠加两条端口规则)
        var (pillLabel, pillTone) = DevicePill(managedHere, sessState, d.inUse == 1, devType,
                                               likelyOurs, manualPort, portMissing);
        Border pill = TonePill(pillLabel, pillTone);

        // 类型图标**独立一列**、跨两行垂直居中 (轮 32 用户反馈: 原来它在 line1 里, 于是跟第一行
        // 文字同一水平线; 现在与"名称+胶囊"和"COM/供电/灯数"两行整体居中)。
        var icon = new Wpf.Ui.Controls.SymbolIcon
        {
            Symbol = DevIcon(devType),
            FontSize = 18,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),   // 与左侧 3px 强调条留出间距
        };
        icon.Foreground = ThemeBrush("TextFillColorSecondaryBrush");

        var line1 = new StackPanel { Orientation = Orientation.Horizontal };
        var name = new System.Windows.Controls.TextBlock
        {
            Text = connecting ? $"{DevTypeName(devType)} · 读取中"
                              : likelyOurs ? "读取中…"
                              : $"{DevTypeName(devType)} · id {devId}",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        line1.Children.Add(name);
        if (selected)
        {
            // ⚠️ 强调色**底**上的字要用 TextOnAccentFillColorPrimaryBrush。
            // 原来用 AccentTextFillColorPrimaryBrush = 强调色写强调色 → 用户看到的"不明橙色胶囊"
            var cur = Pill("当前", null, null, "AccentFillColorDefaultBrush",
                           "TextOnAccentFillColorPrimaryBrush", left: 8);
            line1.Children.Add(cur);
        }
        line1.Children.Add(pill);

        var line2 = new System.Windows.Controls.TextBlock
        {
            FontSize = 11,
            // 横向缩进归零: 图标已经独立成一列, 这一行天然就在信息列左缘, 不必再手算图标宽度对齐。
            // 垂直仍用 4 —— 那是用户在手调里定的行距, 别顺手改小。
            Margin = new Thickness(0, 4, 0, 0),
        };
        line2.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
                                   "TextFillColorSecondaryBrush");
        // 供电 = 灯带固件周期状态字节的 bit0 (MCU 回报, 见 PROJECT_PLAN §7.1)。
        // 语义是"该设备自己的供电是否正常": 厂商那些**真需要外接供电的型号**(桌面氛围灯/带电池的)
        // 它会跟着外接电源变; 而 USB 供电型号**连上就恒 1** (实战: 握手后 0 → 1)。
        // 所以文案用「供电 正常」而不是"外接"——本型号上它不代表外接电源已接入。
        // bit0 = 0 有两种可能: 真的供电异常, 或者"这一帧的状态字节还没读到"(会话刚起时结构体全 0),
        // 两者分不开 → **0 时什么都不显示**: 比谎报"供电 USB"诚实, 也顺手消掉了
        // "连接瞬间 供电 USB → 供电 外接"那一下极快翻转。DEV_LOST 同理不显示 (残留值;
        // 原厂断连时也是 SetStatus(0,0) 清缓存)。
        long statusByte = sess?.ledStatusByte ?? 0;
        string power = sessState == AmbientCore.StateRunning && (statusByte & 1) == 1
            ? "供电 正常" : "";
        // 只用查得到的配置, 不 GetOrCreate —— 光是"看一眼设备列表"不该给没见过的设备建配置文件
        string leds = managedHere && hasCfg ? $"{ds!.LedCount} 灯" : "";
        // 端口显示**实际会用的那个** (轮 34 补 5): 以前无条件显示枚举端口, 于是在
        // "高级页给这台手动指定了别的口"时, 行里写着 COM5、点开启却去连 COM4 —— 界面在说谎。
        // ⚠️ 查配置**不能**加 managedHere 条件 (那曾是个真 bug): 手动指定端口的场合里,
        // "还没开会话"恰恰是最常见的状态 —— 加上它就等于"无会话时永远显示枚举端口"。
        // 规则见 PortLabel (纯函数, 探针真值表 + 一条接线断言)。
        var portLabel = PortLabel(sess?.ComPort, cfgPort, d.ComPort);
        line2.Text = string.Join(" · ",
            new[] { portLabel.Text, power, leds }.Where(s => s.Length > 0));
        // 警示色只在"配置端口 ≠ 本行枚举端口"时才用 (手动指定且一致 = 正常状态, 不该报警);
        // 端口**不在位**比"不一致"更严重 → 同样转警示色。
        if (portLabel.Warn || portMissing) line2.SetResourceReference(
            System.Windows.Controls.TextBlock.ForegroundProperty, "SystemFillColorCautionBrush");

        var infoCol = new StackPanel
        {
            Margin = new Thickness(10, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        infoCol.Children.Add(line1);
        if (line2.Text.Length > 0) infoCol.Children.Add(line2);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var toggle = new Wpf.Ui.Controls.Button
        {
            Content = slot >= 0 ? "关闭" : "开启",
            Appearance = slot >= 0 ? ControlAppearance.Secondary : ControlAppearance.Primary,
            Margin = new Thickness(0, 0, 6, 0),
            IsEnabled = usable,
            MinWidth = 64,
        };
        toggle.Click += async (_, _) =>
        {
            if (App.Instance.SlotOf(key) >= 0) App.Instance.StopDevice(key);
            else App.Instance.StartDevice(key);
            await Task.Delay(600);              // 会话端口/状态回填后再刷, 避免闪现"端口被占用"
            _ = RefreshDevicesAsync();
        };
        actions.Children.Add(toggle);
        if (!selected)
        {
            var select = new Wpf.Ui.Controls.Button
            {
                Content = "设为当前",
                IsEnabled = usable,
                Appearance = ControlAppearance.Secondary,
                MinWidth = 76,
            };
            select.Click += (_, _) =>
            {
                ConfigStore.Select(key);
                _ = RefreshDevicesAsync();
            };
            actions.Children.Add(select);
        }

        var outer = new Grid { Margin = new Thickness(0, 5, 0, 5) };
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });   // 当前条
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });     // 类型图标
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });     // 动作按钮

        var bar = new Border
        {
            Width = 3,
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0, 2, 0, 2),
        };
        if (selected) bar.SetResourceReference(Border.BackgroundProperty, "AccentFillColorDefaultBrush");

        Grid.SetColumn(bar, 0);
        Grid.SetColumn(icon, 1);
        Grid.SetColumn(infoCol, 2);
        Grid.SetColumn(actions, 3);
        outer.Children.Add(bar);
        outer.Children.Add(icon);
        outer.Children.Add(infoCol);
        outer.Children.Add(actions);
        return outer;
    }
}
