// probe_core.cs — M2 核心冒烟: 扩展帧 ledRgb / MCU 特效透传 / 纯色 / 模式拒绝
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

    Console.WriteLine($"version: 1.{(Native.AC_Version() >> 8) & 0xFF}.{Native.AC_Version() & 0xFF} (expect 1.2.0)");
    Check("version 1.2.0", Native.AC_Version() == ((1u << 16) | (2u << 8)));

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

    // ---- MCU 模式范围 (12/13 + 拒绝 1) ----
    cfg.modeIndex = 13;
    Check("SetConfig mode 13", Native.AC_SetConfig(ref cfg) == 0);
    st = Poll(1200);
    Check("mode 13 running", st.state == 2, $"state={st.state}");
    cfg.modeIndex = 1;
    Check("mode 1 (拾音 M3) 拒绝", Native.AC_SetConfig(ref cfg) == 1);
    cfg.modeIndex = 14;
    Check("mode 14 越界拒绝", Native.AC_SetConfig(ref cfg) == 1);
    cfg.modeIndex = 11;

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
}

Console.WriteLine($"\n=== {pass} passed, {fail} failed ===");
return fail == 0 ? 0 : 1;

static unsafe int CountNonZero(ACStatus st)
{
    int n = 0;
    for (int i = 0; i < 540; i++) if (st.ledRgb[i] != 0) n++;
    return n;
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
}
