// ConfigStore.cs — config/*.json 读写; 程序目录不可写时回退 %APPDATA%\AmbientLight\config
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AmbientLight.Services;

public sealed class AppOptions
{
    public string Lang { get; set; } = "zh";
    public bool StartMinimized { get; set; } = false;
    public bool AutoStart { get; set; } = false;   // shell:startup 快捷方式 (M4)
    public string SelectedDevice { get; set; } = "0_1";   // 当前编辑的设备 key (M3)
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
    public int CaptureRange { get; set; } = 15;
    public int DepthOverride { get; set; } = 0;
    public int Taps { get; set; } = 8;
    public int Smoothing { get; set; } = 0;
    public int FpsCap { get; set; } = 0;
    public bool BlackBars { get; set; } = true;
    public int BlackBarLevel { get; set; } = 3;
    public double Brightness { get; set; } = 1.0;
    public double Vibrance { get; set; } = 9.0;
    public double Hue { get; set; } = 1.0;
    public double Gamma { get; set; } = 0.65;
    public double RGain { get; set; } = 1.0;
    public double GGain { get; set; } = 1.0;
    public double BGain { get; set; } = 1.0;
    public int ColorCut { get; set; } = 1;
    public int MinBrightness { get; set; } = 1;
    public int OutputOrder { get; set; } = 0;
    public string SolidColor { get; set; } = "#FFE1D6";
    public int WaitDispPolicy { get; set; } = 2;
    public bool Colorfix { get; set; } = false;
    public double Sfloor { get; set; } = 24.0;

    // v1.3 拾音 (M3): 对齐 ACConfig.audio* 字段
    public int AudioSource { get; set; } = 0;          // 0=系统声音(主机拾音) 1=MCU 自拾音
    public double AudioGain { get; set; } = 1.0;       // 灵敏度 0.1..5.0
    public double AudioRelease { get; set; } = 0.30;   // 回落时间常数 s 0.05..1.0
    public double AudioGate { get; set; } = 0.003;     // 底噪门限 0..0.1
    public double AudioBass { get; set; } = 0.35;      // 低频加权 0..1
    public bool AudioAgc { get; set; } = true;         // 慢速峰值自动增益
    public bool AudioFollowVolume { get; set; } = false; // 音量字节跟随系统主音量
    public double AudioSoft { get; set; } = 0.5;       // 律动柔和度 0..1 (0=硬包络 1=最平滑)

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
        Devices.Clear();
        if (Directory.Exists(ConfigDir))
        {
            foreach (var f in Directory.GetFiles(ConfigDir, "device_*.json"))
            {
                var key = Path.GetFileNameWithoutExtension(f)["device_".Length..];
                var ds = LoadJson<DeviceSettings>(f);
                if (ds != null && key.Length > 0) Devices[key] = ds;
            }
        }
        SelectedKey = string.IsNullOrWhiteSpace(App.SelectedDevice) ? "0_1" : App.SelectedDevice;
        GetOrCreate(SelectedKey);           // 首次运行: 保证至少有一个默认设备
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
