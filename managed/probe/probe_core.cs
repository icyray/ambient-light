// probe_core.cs — M3 核心冒烟: 扩展帧 ledRgb / MCU 特效透传 / 拾音 / 纯色 / 模式拒绝
// 前置: Ansnake 未运行; 灯带在位则真实下发, 不在位则状态为 DEV_LOST (仅验证流转)。
using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;

var lib = @"D:\Codes\ambient-light\managed\AmbientLight\bin\Debug\net10.0-windows\win-x64\AmbientCore.dll";
NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, (name, asm, path) =>
    NativeLibrary.Load(lib));

int pass = 0, fail = 0;
void Check(string what, bool ok, string detail = "")
{
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}{(detail.Length > 0 ? $"  ({detail})" : "")}");
    if (ok) pass++; else fail++;
}

unsafe
{
    var cfg = new ACConfig();
    cfg.structSize = (uint)sizeof(ACConfig);
    cfg.baudRate = 2000000;
    cfg.modeIndex = 0;
    cfg.ledCount = 60;
    // 颜色链默认值 (UI 侧 ConfigStore 默认; 不设则 brightness=0 → 全黑帧)
    cfg.brightness = 1.0;
    cfg.vibrance = 9.0;
    cfg.hueMul = 1.0;
    cfg.gamma = 0.65;
    cfg.rGain = cfg.gGain = cfg.bGain = 1.0;
    cfg.colorCut = 1;
    cfg.minBrightness = 1;

    Console.WriteLine($"version: 1.{(Native.AC_Version() >> 8) & 0xFF}.{Native.AC_Version() & 0xFF} (expect 1.3.0)");
    Check("version 1.3.0", Native.AC_Version() == ((1u << 16) | (3u << 8)));

    int rc = Native.AC_Start(ref cfg);
    Console.WriteLine($"AC_Start (mode 0) -> {rc}");
    var st = Poll(2500);
    Check("mode 0 running", st.state == 2, $"state={st.state} frames={st.frameCounter}");
    bool rgbLive = CountNonZero(st) > 30;
    Check("ledRgb 扩展帧有数据", rgbLive, $"nonzeroBytes={CountNonZero(st)}");

    // ---- MCU 特效 (mode 2 彩虹跳跃) ----
    cfg.modeIndex = 2;
    Check("SetConfig mode 2", Native.AC_SetConfig(ref cfg) == 0);
    uint f0 = Poll(0).frameCounter;
    st = Poll(2500);
    Check("mode 2 running (MCU 1Hz)", st.state == 2 && st.frameCounter > f0,
          $"state={st.state} frames={st.frameCounter} (delta≈2-3)");
    Check("MCU 模式 ledRgb 清零", CountNonZero(st) == 0, $"nonzeroBytes={CountNonZero(st)}");

    // ---- MCU 模式范围 (12/13; mode 1 自 M3 起为拾音) ----
    cfg.modeIndex = 13;
    Check("SetConfig mode 13", Native.AC_SetConfig(ref cfg) == 0);
    st = Poll(1200);
    Check("mode 13 running", st.state == 2, $"state={st.state}");
    cfg.modeIndex = 14;
    Check("mode 14 越界拒绝", Native.AC_SetConfig(ref cfg) == 1);

    // ---- 拾音 (mode 1, 主机拾音 audioSource=0): 5ms 节拍 Adz ----
    cfg.modeIndex = 1;
    cfg.audioSource = 0;
    cfg.audioGain = 1.0;
    cfg.audioRelease = 0.30;
    cfg.audioGate = 0.003;
    cfg.audioBass = 0.35;
    cfg.audioAgc = 1;
    cfg.audioFollowVolume = 0;
    Check("SetConfig mode 1 (主机拾音)", Native.AC_SetConfig(ref cfg) == 0);
    f0 = Poll(0).frameCounter;
    st = Poll(3000);
    Check("mode 1 running", st.state == 2, $"state={st.state}");
    Check("Adz 帧率 ≈ 170-200fps", st.fps > 120f,
          $"fps={st.fps:F0} delta={st.frameCounter - f0}");
    Check("音源会话健康 audioOk=1", st.audioOk == 1,
          $"audioOk={st.audioOk} dev='{ReadDev(st)}'");
    Console.WriteLine($"    audioLevel={st.audioLevel} (静默≈0, 播放音乐应>0; 端点='{ReadDev(st)}')");

    // ---- 拾音子模式: MCU 自拾音 (audioSource=1 → "Adb" 1Hz 心跳) ----
    cfg.audioSource = 1;
    Check("SetConfig audioSource=1 (MCU 自拾音)", Native.AC_SetConfig(ref cfg) == 0);
    f0 = Poll(0).frameCounter;
    st = Poll(2500);
    Check("Adb 心跳运行", st.state == 2 && st.frameCounter > f0,
          $"state={st.state} delta={st.frameCounter - f0} (≈2-3)");
    Check("Adb 模式 audioLevel=0", st.audioLevel == 0, $"audioLevel={st.audioLevel}");
    cfg.modeIndex = 11;
    cfg.audioSource = 0;

    // ---- 纯色 (mode 11): ledRgb 应为过颜色链后的纯色 ----
    Check("SetConfig mode 11", Native.AC_SetConfig(ref cfg) == 0);
    st = Poll(1500);
    Check("mode 11 running", st.state == 2, $"state={st.state}");
    // 纯色经颜色链 (伽马/鲜艳度) 后色值会偏移, 只验均匀性 + 非零
    bool solidOk = CountNonZero(st) > 100
                   && st.ledRgb[3] == st.ledRgb[0] && st.ledRgb[4] == st.ledRgb[1]
                   && st.ledRgb[5] == st.ledRgb[2] && st.ledRgb[6] == st.ledRgb[0];
    Check("ledRgb = 纯色帧 (全灯均匀)", solidOk,
          $"led0={st.ledRgb[0]},{st.ledRgb[1]},{st.ledRgb[2]} nonzero={CountNonZero(st)}");

    // ---- 测试图 + Stop ----
    Native.AC_SetTestPattern(2);
    st = Poll(1000);
    Check("pattern 2 (洋红) 运行", st.state == 2 && st.frameCounter > 0);
    Native.AC_SetTestPattern(0);

    Native.AC_Stop();
    Native.AC_GetStatus(ref st);
    Check("Stop → STOPPED", st.state == 0, $"state={st.state}");

    // ---- 多会话 (slot Ex API; 纯色模式避开 DDA, 只看串口属主隔离) ----
    cfg.modeIndex = 11;
    var st3 = new ACStatus();
    st3.structSize = (uint)Marshal.SizeOf<ACStatus>();
    Native.AC_GetStatusEx(3, ref st3);
    Check("未启动 slot 3 → STOPPED/未知", st3.state == 0 && st3.devType == -1,
          $"state={st3.state} devType={st3.devType}");

    Check("StartEx slot 3", Native.AC_StartEx(3, ref cfg) == 0);
    st3 = PollEx(3, 2500);
    Check("slot 3 running", st3.state == 2, $"state={st3.state} com='{ComOf(st3)}'");
    Check("重复 StartEx slot 3 → 错误 5", Native.AC_StartEx(3, ref cfg) == 5);

    Check("StartEx slot 4 (端口被 slot 3 占用)", Native.AC_StartEx(4, ref cfg) == 0);
    var st4 = PollEx(4, 2500);
    Check("slot 4 DEV_LOST (端口争用隔离)", st4.state == 4, $"state={st4.state}");
    Check("slot 3 仍在运行", PollEx(3, 0).state == 2);

    Native.AC_StopEx(3);
    st4 = PollEx(4, 4500);
    Check("slot 3 停止后 slot 4 接管端口 → RUNNING", st4.state == 2,
          $"state={st4.state} com='{ComOf(st4)}'");
    Native.AC_StopEx(4);
    Native.AC_GetStatusEx(4, ref st4);
    Check("slot 4 → STOPPED", st4.state == 0, $"state={st4.state}");
}

Console.WriteLine($"\n=== {pass} passed, {fail} failed ===");
return fail == 0 ? 0 : 1;

static unsafe int CountNonZero(ACStatus st)
{
    int n = 0;
    for (int i = 0; i < 540; i++) if (st.ledRgb[i] != 0) n++;
    return n;
}

static unsafe string ReadDev(ACStatus st)
{
    // C# 12+ inline array: 局部结构体的 fixed 缓冲已隐式固定, fixed 语句反而报 CS0213
    byte* p = st.audioDev;
    int n = 0;
    while (n < 32 && p[n] != 0) n++;
    return Encoding.UTF8.GetString(p, n);
}

static ACStatus Poll(int ms)
{
    var st = new ACStatus();
    st.structSize = (uint)Marshal.SizeOf<ACStatus>();
    var sw = Stopwatch.StartNew();
    Native.AC_GetStatus(ref st);
    while (sw.ElapsedMilliseconds < ms)
    {
        Thread.Sleep(100);
        Native.AC_GetStatus(ref st);
    }
    return st;
}

static ACStatus PollEx(int slot, int ms)
{
    var st = new ACStatus();
    st.structSize = (uint)Marshal.SizeOf<ACStatus>();
    var sw = Stopwatch.StartNew();
    Native.AC_GetStatusEx((uint)slot, ref st);
    while (sw.ElapsedMilliseconds < ms)
    {
        Thread.Sleep(100);
        Native.AC_GetStatusEx((uint)slot, ref st);
    }
    return st;
}

static unsafe string ComOf(ACStatus st)
{
    byte* p = st.comPort;
    int n = 0;
    while (n < 16 && p[n] != 0) n++;
    return Encoding.UTF8.GetString(p, n);
}

internal static unsafe partial class Native
{
    [DllImport("AmbientCore.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int AC_Start(ref ACConfig cfg);

    [DllImport("AmbientCore.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void AC_Stop();

    [DllImport("AmbientCore.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int AC_SetConfig(ref ACConfig cfg);

    [DllImport("AmbientCore.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void AC_GetStatus(ref ACStatus st);

    [DllImport("AmbientCore.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int AC_SetTestPattern(int mode);

    [DllImport("AmbientCore.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint AC_Version();

    [DllImport("AmbientCore.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int AC_StartEx(uint slot, ref ACConfig cfg);

    [DllImport("AmbientCore.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void AC_StopEx(uint slot);

    [DllImport("AmbientCore.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void AC_GetStatusEx(uint slot, ref ACStatus st);
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal unsafe struct ACConfig
{
    public uint structSize;
    public fixed byte comPort[16];
    public uint baudRate;
    public int modeIndex;
    public int monitorIndex;
    public int ledCount;
    public int sides;
    public int direction;
    public int captureRange;
    public int depthOverride;
    public int taps;
    public int smoothing;
    public int fpsCap;
    public int blackBars;
    public int blackBarLevel;
    public double brightness;
    public double vibrance;
    public double hueMul;
    public double gamma;
    public double rGain;
    public double gGain;
    public double bGain;
    public int colorCut;
    public int minBrightness;
    public int outputOrder;
    public fixed byte solidRgb[3];
    public int waitDispPolicy;
    public int colorfix;
    public double sfloor;
    public int audioSource;
    public double audioGain;
    public double audioRelease;
    public double audioGate;
    public double audioBass;
    public int audioAgc;
    public int audioFollowVolume;
    public fixed uint reserved[8];
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal unsafe struct ACStatus
{
    public uint structSize;
    public int state;
    public int devType;
    public int devId;
    public fixed byte comPort[16];
    public uint ledStatusByte;
    public uint deviceChanged;
    public int monitorFallback;
    public uint allBlackFrames;
    public float fps;
    public float procMs;
    public float wireMs;
    public float cpuPercent;
    public fixed int margins[4];
    public uint frameCounter;
    public fixed byte ledRgb[540];
    public byte audioLevel;
    public int audioOk;
    public fixed byte audioDev[32];
}
