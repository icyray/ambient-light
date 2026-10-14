// ConfigStore.cs — config/*.json 读写; 程序目录不可写时回退 %APPDATA%\AmbientLight\config
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AmbientLight.Services;

public sealed class AppOptions
{
    public string Lang { get; set; } = "system";
    public bool StartMinimized { get; set; } = false;
    public bool AutoStart { get; set; } = false;   // shell:startup 快捷方式 (M4)
    public string SelectedDevice { get; set; } = "0_1";   // 当前编辑的设备 key (M3)
    public bool BannerGlow { get; set; } = true;          // 主页 banner 底边辉光 (M5.1 §4, 实验开关)
    /// <summary>主题 (M5.2): "dark"(默认) / "light" / "system"(跟随系统)。
    /// 未知值一律按 system 处理 (见 ThemeService.Parse, 永不抛)。</summary>
    public string Theme { get; set; } = "dark";
    /// <summary>拾色器的自定义色槽: 记住最近调出来的颜色, 最新的在前, 最多 5 个。
    /// 只存非预设色 —— 预设本身就在列表里, 再记一遍没意义。</summary>
    public List<string> CustomColors { get; set; } = [];
}

public sealed class DeviceSettings
{
    public string ComPort { get; set; } = "";          // 空 = 自动探测 VID_2E3C&PID_5740
    public int BaudRate { get; set; } = 2000000;
    public bool AutoStartCore { get; set; } = true;    // 应用启动即连接灯带并恢复上次模式 (M1 默认关, M2 验收后改为默认开)
    public int ModeIndex { get; set; } = 0;
    public int MonitorIndex { get; set; } = 0;
    public int LedCount { get; set; } = 60;
    public int Sides { get; set; } = 4;
    public int Direction { get; set; } = 0;
    public int LedRotate { get; set; } = 0;      // 灯序偏移 (M4 校准, 核心钳 ±灯数12%)
    /// <summary>把 v1.6 早期的"两个正交开关"迁移成等效档位 —— **纯函数**, 探针打真值表。
    /// (那版本地配置从未发布过; 迁移只为让用户不必重设一遍。)</summary>
    public static void MigrateLegacySmooth(DeviceSettings d)
    {
        if (d.ChaseSmoothMode == 0 && (d.ChaseFlashGuard != 0 || d.ChaseTemporalSmooth != 0))
            d.ChaseSmoothMode = d.ChaseTemporalSmooth != 0 ? 2 : 1;
    }

    /// <summary>v1.6 追光柔化平滑档: 0=关(默认) / 1=抑制闪光 / 2=抑制闪光+时域柔和。
    /// 刻意做成**包含关系的档位**而不是两个正交开关 —— 用户提出的口径:
    /// 别让用户在两个"外观难以分辨"的组合里做选择; 而 2 在行为上严格包含 1 ✓</summary>
    public int ChaseSmoothMode { get; set; } = 0;
    // 兼容: v1.6 早期本地配置里是两个正交开关 (未发布过) —— 只在 Load 时读一次做迁移, 不写回核心
    public int ChaseFlashGuard { get; set; } = 0;
    public int ChaseTemporalSmooth { get; set; } = 0;
    public int CaptureRange { get; set; } = 20;
    public int DepthOverride { get; set; } = 0;
    public int Taps { get; set; } = 8;
    public int Smoothing { get; set; } = 5;
    // 默认 60 (>60 可能有轻微抖动; 144 为实测硬上限, 见 SettingsPageCapture.FpsCapChoices 注释)
    public int FpsCap { get; set; } = 60;
    public bool BlackBars { get; set; } = true;
    public int BlackBarLevel { get; set; } = 3;

    // ---- JSON 落盘的浮点收敛 (纯存储美化, 2026-10-14 用户提议) ----
    // 拖滑条会存出 0.20925925925925845 这类 17 位长尾 (double 往返保真序列化的结果) ——
    // 读回/内存/计算全程不受本机制影响, 只收敛**写进 json 的文本**。
    // 位数按字段用途逐个定 (见各 [Round(n)] 旁注): 量程小或会被 UI 取整的字段位数给足,
    // 否则会出现"AUDIO 门限 0.003 被 2 位归零"这类失真。
    [AttributeUsage(AttributeTargets.Property)]
    private sealed class RoundAttribute(int digits) : JsonConverterAttribute
    {
        public override JsonConverter? CreateConverter(Type typeToConvert)
            => new RoundDoubleConverter(digits);
    }
    private sealed class RoundDoubleConverter(int digits) : JsonConverter<double>
    {
        public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => reader.GetDouble();   // 读不收敛 (写时已量化)
        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
            => writer.WriteNumberValue(Math.Round(value, digits));
    }

    [Round(3)] public double Brightness { get; set; } = 1.0;   // 3 位: UI 按整数百分比显示, 2 位会出现"重启后 +1%"跳变
    [Round(2)] public double Vibrance { get; set; } = 9.0;     // 主观强度, 0.01 步进不可感
    [Round(3)] public double Hue { get; set; } = 1.0;          // 乘数: 2 位步进 ≈2° 色相, 边缘可见
    [Round(3)] public double Gamma { get; set; } = 0.65;       // 曲线校准: 0.01 差 ≈1.5% 中间调, 校准想更细
    [Round(3)] public double RGain { get; set; } = 1.0;        // 通道增益: 1% 步进在大面积色块可能可见
    [Round(3)] public double GGain { get; set; } = 1.0;
    [Round(3)] public double BGain { get; set; } = 1.0;
    public int ColorCut { get; set; } = 1;
    public int MinBrightness { get; set; } = 1;
    public int OutputOrder { get; set; } = 0;
    public string SolidColor { get; set; } = "#FFE1D6";
    public int WaitDispPolicy { get; set; } = 2;
    public bool Colorfix { get; set; } = false;
    [Round(2)] public double Sfloor { get; set; } = 24.0;      // 8bit 门限语义, 整数即可

    // v1.3 拾音 (M3): 对齐 ACConfig.audio* 字段
    public int AudioSource { get; set; } = 0;          // 0=系统声音(主机拾音) 1=MCU 自拾音
    [Round(2)] public double AudioGain { get; set; } = 1.0;       // 灵敏度 0.1..5.0, 主观参数
    [Round(3)] public double AudioRelease { get; set; } = 0.30;   // 低端 (0.05~0.1) 2 位相对误差达 10~20%
    [Round(4)] public double AudioGate { get; set; } = 0.003;     // 量程 0..0.1: 2 位会把默认 0.003 归零!
    [Round(2)] public double AudioBass { get; set; } = 0.35;      // 低频加权 0..1, 主观
    public bool AudioAgc { get; set; } = true;         // 慢速峰值自动增益
    public bool AudioFollowVolume { get; set; } = false; // 音量字节跟随系统主音量
    [Round(2)] public double AudioSoft { get; set; } = 0.5;       // 律动柔和度 0..1, 主观
    public string AudioDevId { get; set; } = "";       // 麦克风源端点 ID, 空=跟随系统默认输入
    public string AudioOutDevId { get; set; } = "";    // 回环源端点 ID, 空=跟随系统默认输出

    public unsafe Interop.ACConfig ToCoreConfig()
    {
        var c = Interop.ACConfig.CreateDefault();
        c.ComPort = ComPort;
        c.baudRate = (uint)BaudRate;
        c.modeIndex = ModeIndex;
        c.monitorIndex = MonitorIndex;
        c.ledCount = LedCount;
        c.sides = Sides;
        c.direction = Direction;
        c.ledRotate = LedRotate;
        c.chaseSmoothMode = ChaseSmoothMode;
        c.captureRange = CaptureRange;
        c.depthOverride = DepthOverride;
        c.taps = Taps;
        c.smoothing = Smoothing;
        c.fpsCap = FpsCap;
        c.blackBars = BlackBars ? 1 : 0;
        c.blackBarLevel = BlackBarLevel;
        c.brightness = Brightness;
        c.vibrance = Vibrance;
        c.hueMul = Hue;
        c.gamma = Gamma;
        c.rGain = RGain;
        c.gGain = GGain;
        c.bGain = BGain;
        c.colorCut = ColorCut;
        c.minBrightness = MinBrightness;
        c.outputOrder = OutputOrder;
        var rgb = ParseColor(SolidColor, (255, 225, 214));
        c.solidRgb[0] = rgb.r;
        c.solidRgb[1] = rgb.g;
        c.solidRgb[2] = rgb.b;
        c.waitDispPolicy = WaitDispPolicy;
        c.colorfix = Colorfix ? 1 : 0;
        c.sfloor = Sfloor;
        c.audioSource = AudioSource;
        c.audioGain = AudioGain;
        c.audioRelease = AudioRelease;
        c.audioGate = AudioGate;
        c.audioBass = AudioBass;
        c.audioAgc = AudioAgc ? 1 : 0;
        c.audioFollowVolume = AudioFollowVolume ? 1 : 0;
        c.audioSoft = AudioSoft;
        c.AudioDevId = AudioDevId;
        c.AudioOutDevId = AudioOutDevId;
        return c;
    }

    public unsafe void FromCoreConfig(Interop.ACConfig c)
    {
        ComPort = c.ComPort;
        BaudRate = (int)c.baudRate;
        ModeIndex = c.modeIndex;
        MonitorIndex = c.monitorIndex;
        LedCount = c.ledCount;
        Sides = c.sides;
        Direction = c.direction;
        LedRotate = c.ledRotate;
        ChaseSmoothMode = c.chaseSmoothMode;
        CaptureRange = c.captureRange;
        DepthOverride = c.depthOverride;
        Taps = c.taps;
        Smoothing = c.smoothing;
        FpsCap = c.fpsCap;
        BlackBars = c.blackBars != 0;
        BlackBarLevel = c.blackBarLevel;
        Brightness = c.brightness;
        Vibrance = c.vibrance;
        Hue = c.hueMul;
        Gamma = c.gamma;
        RGain = c.rGain;
        GGain = c.gGain;
        BGain = c.bGain;
        ColorCut = c.colorCut;
        MinBrightness = c.minBrightness;
        OutputOrder = c.outputOrder;
        SolidColor = $"#{c.solidRgb[0]:X2}{c.solidRgb[1]:X2}{c.solidRgb[2]:X2}";
        WaitDispPolicy = c.waitDispPolicy;
        Colorfix = c.colorfix != 0;
        Sfloor = c.sfloor;
        AudioSource = c.audioSource;
        AudioGain = c.audioGain;
        AudioRelease = c.audioRelease;
        AudioGate = c.audioGate;
        AudioBass = c.audioBass;
        AudioAgc = c.audioAgc != 0;
        AudioFollowVolume = c.audioFollowVolume != 0;
        AudioSoft = c.audioSoft;
        AudioDevId = c.AudioDevId;
        AudioOutDevId = c.AudioOutDevId;
    }

    private static (byte r, byte g, byte b) ParseColor(string hex, (byte r, byte g, byte b) fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        var s = hex.TrimStart('#');
        if (s.Length == 8) s = s.Substring(2);           // #AARRGGBB → RGB
        if (s.Length != 6) return fallback;
        try
        {
            return ((byte)Convert.ToByte(s[..2], 16),
                    (byte)Convert.ToByte(s.Substring(2, 2), 16),
                    (byte)Convert.ToByte(s.Substring(4, 2), 16));
        }
        catch { return fallback; }
    }
}

public static class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static AppOptions App { get; private set; } = new();

    // 多设备注册表 (M3): key = "<devType>_<devId>" (握手应答字节解码),
    // 每设备独立文件 device_<key>.json, 字段命名对齐厂商便于迁移。
    public static Dictionary<string, DeviceSettings> Devices { get; private set; } = new();

    public static string SelectedKey { get; private set; } = "0_1";
    public static event Action? SelectedChanged;

    public static string ConfigDir { get; private set; } = "";
    public static string ConfigDirSource { get; private set; } = "";   // "app" | "appdata"

    /// <summary>当前选中设备的设置; 设置三页/主页直接绑定此实例</summary>
    public static DeviceSettings Device => GetOrCreate(SelectedKey);

    public static string FileNameOf(string key) => $"device_{key}.json";

    public static DeviceSettings GetOrCreate(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) key = "0_1";
        if (!Devices.TryGetValue(key, out var ds))
        {
            ds = new DeviceSettings();
            Devices[key] = ds;
        }
        return ds;
    }

    /// <summary>切换当前编辑设备 (主页设备卡片触发); 设置页在下次导航时重读</summary>
    public static void Select(string key)
    {
        key ??= "0_1";
        GetOrCreate(key);
        if (SelectedKey == key) return;
        SelectedKey = key;
        SelectedChanged?.Invoke();
    }

    public static void Load()
    {
        ConfigDir = ResolveConfigDir();
        App = LoadJson<AppOptions>(Path.Combine(ConfigDir, "app.json")) ?? new AppOptions();
        App.CustomColors ??= [];            // 手改过 json / 旧文件里没有这个键时兜底
        Devices.Clear();
        if (Directory.Exists(ConfigDir))
        {
            foreach (var f in Directory.GetFiles(ConfigDir, "device_*.json"))
            {
                var key = Path.GetFileNameWithoutExtension(f)["device_".Length..];
                var ds = LoadJson<DeviceSettings>(f);
                if (ds != null && key.Length > 0)
                {
                    DeviceSettings.MigrateLegacySmooth(ds);   // v1.6 早期"两个正交开关"→ 等效档位
                    Devices[key] = ds;
                }
            }
        }
        SelectedKey = string.IsNullOrWhiteSpace(App.SelectedDevice) ? "0_1" : App.SelectedDevice;
        GetOrCreate(SelectedKey);           // 首次运行: 保证至少有一个默认设备
    }

    /// <summary>恢复出厂默认: 删掉配置目录里的 app.json / device_*.json 后重新 Load
    /// (缺文件即默认值), 并立刻按默认值落盘。
    /// **破坏性操作 —— 调用方必须先用 ContentDialog 确认** (高级页「恢复默认配置」)。
    /// 只删自己写的两种文件, 不碰目录里的其它东西。</summary>
    public static void RestoreDefaults()
    {
        try
        {
            if (Directory.Exists(ConfigDir))
            {
                var app = Path.Combine(ConfigDir, "app.json");
                if (File.Exists(app)) File.Delete(app);
                foreach (var f in Directory.GetFiles(ConfigDir, "device_*.json"))
                    File.Delete(f);
            }
        }
        catch
        {
            // 删不掉也继续: Load 对缺失文件一律回落到默认值
        }
        Load();
        SaveAll();
    }

    public static void SaveApp()
    {
        App.SelectedDevice = SelectedKey;
        WriteJson(Path.Combine(ConfigDir, "app.json"), App);
    }

    public static void SaveDevice() => WriteJson(Path.Combine(ConfigDir, FileNameOf(SelectedKey)), Device);

    public static void SaveAll()
    {
        SaveApp();
        foreach (var (key, ds) in Devices)
            WriteJson(Path.Combine(ConfigDir, FileNameOf(key)), ds);
    }

    private static string ResolveConfigDir()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "config");
        try
        {
            Directory.CreateDirectory(local);
            var probe = Path.Combine(local, ".write_probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            ConfigDirSource = "app";
            return local;
        }
        catch
        {
            var roaming = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AmbientLight", "config");
            Directory.CreateDirectory(roaming);
            ConfigDirSource = "appdata";
            return roaming;
        }
    }

    private static T? LoadJson<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOpts);
        }
        catch
        {
            return null;   // 损坏配置视为缺省, 下次保存覆盖
        }
    }

    private static void WriteJson<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOpts));
        }
        catch
        {
            // 只读目录下的写失败静默; 实际路径已回退, 不影响运行
        }
    }
}
