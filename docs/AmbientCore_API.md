# AmbientCore — 原生核心 C ABI

> **当前 ABI 版本：1.6.0**（`AC_Version()` 返回 `(major << 16) | (minor << 8) | patch`）
> 规范定义以 `native/AmbientCore/AmbientCore.h` 为准；本文解释**契约与行为**，两者不一致时以头文件为准。

`AmbientCore.dll` 是应用的唯一"脏活承担者"：屏幕采集、GPU 取色、颜色链、串口协议、音频包络、
电源与显示事件、可靠性状态机全部在 DLL 内。托管 UI 只做三件事：**改配置、轮询状态、画界面**。

设计约束（贯穿全篇）：

- **UI 不接触任何图形 / 串口 / 音频细节** —— 没有 D3D 设备、没有 `SerialPort`、没有 WASAPI 对象跨到托管侧；
- **没有回调**（跨 ABI 回调的生命周期管理是 bug 温床），状态靠 2–4 Hz 轮询；
- **没有跨边界内存分配**：所有字符串/缓冲由**调用方分配**，DLL 只写不还；
- 字符串一律 **UTF-8**；导出符号为 **x64、未修饰 cdecl**。

---

## 1. 总体模型

```
┌─────────────────────────── UI 进程 (C# / WPF) ──────────────────────────┐
│  设置页 / 托盘            │  每 250ms 轮询 AC_GetStatusEx(slot)        │
│  改配置 → AC_SetConfigEx() │  （无回调、无跨 ABI 分配）                 │
└──────────────┬─────────────────────────────────────────────────────────┘
               │ P/Invoke (cdecl, x64, UTF-8)
┌──────────────▼───────────────── AmbientCore.dll ───────────────────────┐
│  会话线程（每 slot 一条）：抓帧 → 着色器采样 → 颜色链 → 串口写           │
│  音频线程（0 或 1 条）：WASAPI 回环/麦克风 → 包络 → 原子音量             │
│  监视线程（1 条）：设备热插拔轮询（1s）                                  │
│  配置：原子快照交换，AC_SetConfigEx 非阻塞、即时生效                     │
└────────────────────────────────────────────────────────────────────────┘
```

**线程与并发规则**

- DLL 内部线程 = 1 条监视线程 + 每活动 slot 1 条会话线程 + 拾音时 1 条音频线程（进程单例，多会话共享包络）。
- 所有导出函数**线程安全**；最重的操作是打开/关闭串口（毫秒级，但存在下文所说的内核阻塞例外）。
- 多会话：每 slot 独立会话线程 / 配置快照 / 串口连接 / 状态机；slot 由 UI 分配，范围 `[0, AC_MAX_DEVICES)`。
  单设备接口 `AC_Start/Stop/SetConfig/GetStatus/GetLastDiag` 是 **slot 0 的别名**；`AC_Stop` 会停止**全部** slot。
- `AC_SetConfig(Ex)` 是**原子快照交换**：任意字段即时生效，无需重启。`comPort` / `monitorIndex` 变化会触发重连/重建采集。
- 设备插拔不通过事件通知，而是体现在 `ACStatus.state` 与 `ACStatus.deviceChanged` 计数器上。

---

## 2. 头文件

```c
// AmbientCore.h — C99 兼容, x64, 未修饰 cdecl
#pragma once

#ifdef AMBIENTCORE_BUILD
  #define AC_API extern "C" __declspec(dllexport)
#else
  #define AC_API extern "C" __declspec(dllimport)
#endif

#include <stdint.h>

#pragma pack(push, 8)

// ---- 常量 -----------------------------------------------------------
#define AC_MAX_MONITORS    8
#define AC_MAX_DEVICES     8
#define AC_PORT_LEN        16      // "COM5"
#define AC_NAME_LEN        32
#define AC_ADAPTER_LEN     64      // 显卡型号全名（含 "GPU" 后缀），32 字节会截断
#define AC_AUDIO_ID_LEN    64      // MMDevice 端点 ID，约 54 字符
#define AC_MAX_AUDIO_DEVS  16
#define AC_MAX_LEDS        180     // 定长帧上限：3 + 180*3 = 543 字节

// 设备类型 = 握手应答字节解码 (0x80 | type << 2 | devId)
typedef enum {
    AC_DEV_SCREEN = 0,   // 屏幕灯带   (应答 0x80-0x83)
    AC_DEV_FAN    = 1,   // ARGB 风扇  (0x84-0x87)
    AC_DEV_DESK   = 2,   // 桌面灯条   (0x88-0x8B)
    AC_DEV_SOUND  = 3,   // 立柱氛围灯 (0x8C-0x8F)
} ACDevType;

typedef enum {
    AC_STATE_STOPPED   = 0,
    AC_STATE_STARTING  = 1,   // 正在探测 / 握手
    AC_STATE_RUNNING   = 2,   // 流式运行中
    AC_STATE_WAIT_DISP = 3,   // 画面暂不可用（锁屏 / 拓扑切换 / 独占全屏 / 熄屏）
                              //   串口保持连接，每 500ms 重试采集，灯带按 waitDispPolicy 处理
    AC_STATE_DEV_LOST  = 4,   // 灯带拔出 / 端口消失 / 端口被占用，内部每 2s 重试
    AC_STATE_ERROR     = 5,   // 不可恢复错误（见 AC_GetLastDiag），每 5s 慢速重试
} ACState;

// ---- 配置（POD，按值拷贝即为快照） ----------------------------------
typedef struct ACConfig {
    uint32_t structSize;            // = sizeof(ACConfig)，版本/前向兼容校验；必须精确匹配

    // 设备
    char     comPort[AC_PORT_LEN];  // "COM5"；空串 = 自动探测 VID_2E3C&PID_5740
                                    //   非空 = 只认这个端口（详见 §3.4 端口身份策略）
    uint32_t baudRate;              // 默认 2000000（原生 USB-CDC，数值不敏感）

    // 模式：0=屏幕追光 1=拾音 2..10,12,13=MCU 特效 11=纯色
    int32_t  modeIndex;

    // 采集
    int32_t  monitorIndex;      // 显示器序号（AC_GetMonitors 顺序）
    int32_t  ledCount;          // 1..512，默认 60；超过固件上限 180 的部分被忽略
    int32_t  sides;             // 2=左右 3=上+左右 4=四边，默认 4
    int32_t  direction;         // 0=逆时针右下起 1=顺时针左下起，默认 0
    int32_t  captureRange;      // 取色带深度系数：深度 = H/100 * R/2，默认 15
    int32_t  depthOverride;     // 直接指定深度（px），0 = 不用
    int32_t  taps;              // 采样网格 taps×taps，默认 8；smoothing>0 时被覆盖
    int32_t  smoothing;         // >0 = 每 S px 取一个采样点（步进采样），0 = 网格采样
    int32_t  fpsCap;            // 处理帧率上限：30/60/90/120/144，默认 60（详见 §3.3）
    int32_t  blackBars;         // 黑边自适应开关，默认 1
    int32_t  blackBarLevel;     // 黑色判定阈值，默认 3

    // 颜色链（顺序：HSL 鲜艳度/色相 → 伽马 → RGB 增益 → 低值裁剪 → 最小亮度 → 输出色序）
    double   brightness;        // 线性调光乘数（颜色链之后线性缩放 RGB），默认 1.0
                                //   拾音模式下乘在音量包络上 = 最高亮度上限
    double   vibrance;          // HSL 饱和度乘数，默认 9.0
    double   hueMul;            // HSL 色相乘数，默认 1.0
    double   gamma;             // 默认 0.65
    double   rGain, gGain, bGain;   // 默认 1.0
    int32_t  colorCut;          // 低值裁剪，默认 1
    int32_t  minBrightness;     // 最小亮度抬升，默认 1
    int32_t  outputOrder;       // 0=RGB 1=GBR 2=BRG 3=RBG 4=GRB 5=BGR

    // 纯色模式（modeIndex = 11）
    uint8_t  solidRgb[3];       // 默认 {255,225,214}

    // 熄屏/画面不可用时的灯带策略：0=保持最后帧 1=最暗（minBrightness 灰帧） 2=熄灭（全零帧），默认 2
    //   状态进入时发一帧，之后每 2s 重发，防止固件掉电丢帧
    int32_t  waitDispPolicy;

    // 低灰保真修正（默认关闭）
    int32_t  colorfix;          // 0=不修正（默认） 1=按绝对色度噪声门限钳为中性灰
    double   sfloor;            // colorfix 色度门限（8bit max-min），默认 24

    // 拾音
    int32_t  audioSource;       // 0=系统声音回环（默认） 1=MCU 自拾音（"Adb"） 2=麦克风
    double   audioGain;         // 灵敏度乘数，默认 1.0（0.1..5.0）
    double   audioRelease;      // 包络回落时间常数（秒），默认 0.30（0.05..1.0）
    double   audioGate;         // 底噪门限（AGC 之前的原始全频 RMS），默认 0.003（0..0.1）
    double   audioBass;         // 低频加权 0..1（0=全频 1=纯低频），默认 0.35
    int32_t  audioAgc;          // 慢速峰值自动增益，默认 1
    int32_t  audioFollowVolume; // 音量字节跟随系统主音量，默认 0
    double   audioSoft;         // 律动柔和度 0..1，默认 0.5（0 = 硬包络，有频闪感）
    char     audioDevId[AC_AUDIO_ID_LEN];     // 麦克风源的目标输入端点 ID；空 = 跟随系统默认
    char     audioOutDevId[AC_AUDIO_ID_LEN];  // 回环源的目标输出端点 ID；空 = 跟随系统默认

    // 校准
    int32_t  ledRotate;         // 灯序偏移：LED i 显示段 (i+rotate) mod N 的取色区
                                //   钳到 ±N*12/100（N<10 → 0），默认 0
    // 追光柔化平滑（输出层柔化，不动颜色链）
    //   **符号 = 开关，绝对值 = 档位**：+1/+2 开启档位 1/2；-1/-2 关闭但记住档位；0 = 从未启用
    //   1 = 抑制闪光（整帧平均亮度的突变限幅）  2 = 1 + 时域柔和（逐灯逐通道指数平均，轻微拖影）
    int32_t  chaseSmoothMode;

    uint32_t reserved[8];       // 必须置 0（任何非零 → 参数无效）
} ACConfig;

// ---- 状态（轮询） ----------------------------------------------------
typedef struct ACStatus {
    uint32_t structSize;        // = sizeof(ACStatus)
    int32_t  state;             // ACState
    int32_t  devType;           // ACDevType；-1 = 未知
    int32_t  devId;             // 握手应答低 2 位
    char     comPort[AC_PORT_LEN];  // 实际使用的端口（自动切换后可能与配置不同）
    uint32_t ledStatusByte;     // 周期状态字节（bit0 = 设备供电正常；其余位未定义）
    uint32_t deviceChanged;     // 热插拔事件计数器（UI 对比数值变化后重枚举设备）
    int32_t  monitorFallback;   // 1 = 配置的显示器不可用，已回退主屏
    uint32_t allBlackFrames;    // 连续全黑帧计数（独占全屏 / MPO 提示用）
    float    fps;               // 实际处理帧率
    float    procMs;            // 单帧处理平均耗时
    float    wireMs;            // 单帧串口写耗时
    float    cpuPercent;        // 核心线程 CPU%（预留）
    int32_t  margins[4];        // 黑边 {底, 右, 顶, 左}
    uint32_t frameCounter;      // 已发送帧数（UI 判断画面是否在动）
    uint8_t  ledRgb[AC_MAX_LEDS * 3];   // 最近一帧下发的 LED 颜色（RGB 序，已过颜色链与输出色序），
                                        //   前 ledCount*3 字节有效；MCU 特效模式恒为 0
    uint8_t  audioLevel;        // 最近一帧 "Adz" 音量字节（仅拾音模式非 0）
    int32_t  audioOk;           // 音源健康：0=停止 1=正常 2=重建中
    char     audioDev[AC_NAME_LEN];     // 当前捕获端点友好名（UTF-8）
} ACStatus;

// ---- 显示器 / 设备枚举 -----------------------------------------------
typedef struct ACMonitorInfo {
    char     name[AC_NAME_LEN];        // "\\.\DISPLAY5"
    char     adapter[AC_ADAPTER_LEN];  // "NVIDIA GeForce RTX 4060 Laptop GPU"
    int32_t  width, height;            // 当前模式（物理像素）
    int32_t  primary;                  // 1 = 主屏
} ACMonitorInfo;

typedef struct ACDeviceInfo {
    char     comPort[AC_PORT_LEN];
    int32_t  devType;               // ACDevType
    int32_t  devId;
    int32_t  inUse;                 // 1 = 已被本核心或其他进程占用
} ACDeviceInfo;

// 音频端点：capture=1 枚举输入（麦克风/线路输入），0 枚举输出
typedef struct ACAudioDevInfo {
    char     id[AC_AUDIO_ID_LEN];   // 配置到 ACConfig.audioDevId / audioOutDevId
    char     name[AC_NAME_LEN];     // 友好名（UTF-8）
    int32_t  isDefault;             // 1 = 当前系统默认（仅该流向集合内）
} ACAudioDevInfo;

// ---- 错误码 ---------------------------------------------------------
// 0=OK  1=参数无效  2=设备未找到  3=端口打开失败（被占用）
// 4=握手失败  5=该 slot 已有会话在运行  6=D3D/内部错误

#pragma pack(pop)

#ifdef __cplusplus
extern "C" {
#endif

// 版本：(major << 16) | (minor << 8) | patch
AC_API uint32_t   AC_Version(void);

// 生命周期（单设备接口 = slot 0）
AC_API int32_t    AC_Start(const ACConfig* cfg);     // 异步：立即返回，状态经 GetStatus 推进
AC_API void       AC_Stop(void);                     // 阻塞至全部会话退出；逐会话发关灯帧
AC_API int32_t    AC_SetConfig(const ACConfig* cfg); // 原子热更新，全字段可热改
AC_API void       AC_GetStatus(ACStatus* out);       // 非阻塞，随时可调
AC_API int32_t    AC_GetLastDiag(char* buf, uint32_t cap);  // 0=无诊断 1=有（buf 为 UTF-8）

// 多会话：每 slot 独立会话线程；slot ∈ [0, AC_MAX_DEVICES)
AC_API int32_t    AC_StartEx(uint32_t slot, const ACConfig* cfg);   // 同 slot 重复 Start → 错误码 5
AC_API void       AC_StopEx(uint32_t slot);
AC_API int32_t    AC_SetConfigEx(uint32_t slot, const ACConfig* cfg);
AC_API void       AC_GetStatusEx(uint32_t slot, ACStatus* out);     // 未启动过的 slot：STOPPED + devType=-1
AC_API int32_t    AC_GetLastDiagEx(uint32_t slot, char* buf, uint32_t cap);

// 枚举（可在 Start 之前调用）
AC_API int32_t    AC_GetMonitors(ACMonitorInfo* out, uint32_t max, int32_t* count);
AC_API int32_t    AC_GetDevices(ACDeviceInfo* out, uint32_t max, int32_t* count);   // 会短暂打开端口握手，约 200ms/台

// 音频端点枚举：只列 ACTIVE 端点，不打开任何设备
AC_API int32_t    AC_GetAudioEndPoints(int32_t capture, ACAudioDevInfo* out, uint32_t max, int32_t* count);

// 测试图（校准 / 排障）：0=关 1=四象限色块 2=洋红常亮
AC_API int32_t    AC_SetTestPattern(int32_t mode);

#ifdef __cplusplus
}
#endif
```

---

## 3. 行为细则

### 3.1 生命周期与状态机

```
AC_Start ──► STARTING ──握手成功──► RUNNING ──► RUNNING*
                 │                    │            │
     端口占用/无设备│        重试耗尽   │ 拔出/写失败 │ 锁屏/拓扑切换/独占全屏/熄屏
                 ▼                    ▼            ▼
             DEV_LOST ◄──────────── ERROR        WAIT_DISP
                 │ 每 2s 自动重连                    │ 每 500ms 重试采集（串口保持连接）
                 └────────────成功─────────────────┘
```

- `STARTING → RUNNING` 典型耗时 300–800ms（打开端口 + 复位帧 + 握手重试 ×3）。
- `WAIT_DISP` 期间**串口不断开**：灯带按 `waitDispPolicy` 处理，采集恢复后无缝回到 `RUNNING`。
- `DEV_LOST` 重连期间不阻塞其它导出函数；`comPort` 配置变化立即触发重连。
- 恢复后 COM 号可能变化（USB 重枚举），按 §3.4 的端口身份策略自动接上新端口。

### 3.2 模式分派

| modeIndex | 模式 | 核心行为 |
|---|---|---|
| 0 | 屏幕追光 | 完整采集管线，发送 `Ada` + 543 字节定长帧 |
| 1 | 拾音律动 | `audioSource=0/2` 主机拾音：WASAPI → `Adz` + 音量×3（6 字节，5ms 节拍）；`audioSource=1` MCU 自拾音：只发 `Adb` 3 字节命令，1Hz 心跳。见 §3.8 |
| 2–10, 12, 13 | MCU 特效 | 每秒发 3 字节命令 `Ad` + `(0x61 + modeIndex)`，并读 1 字节状态；模式切换立即发，之后 1Hz 心跳，重连/唤醒后强制重发。**不进入 `WAIT_DISP`**（特效由灯带独立渲染，与显示器无关） |
| 11 | 纯色 | 主机渲染 `Adl` + 543 字节定长帧（`solidRgb` 过颜色链） |

### 3.3 配置热更新与归一化

`AC_SetConfig(Ex)` 把校验后的副本写入原子槽位，流线程在帧边界取用。**全字段可热改**：

| 变更 | 生效方式 |
|---|---|
| 颜色链 / 黑边 / fpsCap / 模式 / 灯数 | 下一帧生效 |
| `monitorIndex` | 重建 Duplication 会话（约 100ms，内部处理 `ACCESS_LOST`） |
| `comPort` / `baudRate` | 关闭当前端口并重新握手 |

核心会把越界值**静默归一化**（不报错），UI 应与下表保持一致：

| 字段 | 归一化规则 |
|---|---|
| `modeIndex` | 0..13，越界 → 错误码 1（拒绝） |
| `ledCount` | 1..512（实际取 `min(ledCount, 180)`） |
| `sides` / `direction` | 2..4 / 布尔 |
| `captureRange` / `taps` | 1..50 / 1..32 |
| `fpsCap` | **>144 → 144；≤0 或 1..29 → 60**（合法档位 30/60/90/120/144） |
| `blackBarLevel` / `sfloor` | 0..128 / 0..255 |
| `baudRate` | 9600..4000000 |
| `audioSource` | 0..2；`audioGain` 0.1..5.0；`audioRelease` 0.05..1.0；`audioGate` 0..0.1；`audioBass`/`audioSoft` 0..1 |
| `ledRotate` | ±`N*12/100`（`N<10` → 0） |
| `reserved[]` | 任何非零 → 错误码 1 |

`structSize` 必须精确等于 `sizeof(ACConfig)`，否则返回错误码 1。

### 3.4 串口协议与端口身份

**物理层**：8N1，DTR 置位；115200–2M 均可（原生 USB-CDC，波特率数值对设备不敏感），默认 2000000。

**帧**：定长 **543 字节** = 3 字节头 + 180×3 字节 RGB。固件**按固定字节数给帧定界**（不靠分隔符/校验），
所以任何一帧只写进去一部分都会让固件**永久错位**，只有重新握手才能复位。

由此得出两条硬规则：

1. **写操作必须补写剩余字节**（`WriteAll`，预算 3s），不许"写一半就丢掉这帧"；
2. 写超时是"设备一时没收"（忙/流控）而非掉线，不能直接判 `DEV_LOST`。

**握手**：先发 1 帧 543 字节全零 `Ada` 复位固件解析器，再发 `Ad0` 并读应答字节（20/30ms 窗口，重试 3 次）：
`resp & 0x80` = 已连接，`(resp>>2) & 3` = 设备类型，`resp & 3` = 设备编号。
每次尝试前检查会话的停止事件 —— 一个"不是灯带"的端口握手三次可能耗时 > 1s，而 `AC_StopEx` 正等着 join 该线程。

**端口身份策略**：

| 配置 `comPort` | 行为 |
|---|---|
| 空串 | 按 `VID_2E3C&PID_5740` 枚举，选第一个可用端口；端口号变化时自动跟随 |
| 非空，且该端口**在本机存在** | **只认它**：握手失败即失败，绝不回落到别的端口 |
| 非空，但该端口**本机不存在** | 退回按 VID/PID 跟随（含"USB 重枚举后 COM5 → COM6"的场景） |

> ⚠️ **不要在 UI 线程调用 `AC_Stop` / `AC_StopEx`**：它们会 join 会话线程，而线程可能正卡在
> "打开/握手一个不是灯带的端口"。打开一个"注册表里有但打不开"的 COM 口（蓝牙虚拟串口常见）
> 会让 `CreateFile` 阻塞数秒，且**无法中断**。托管侧应把生命周期调用放到后台串行队列里。

**状态字节**：每帧发送后若 `RxBytes > 0`，读 1 字节写入 `ACStatus.ledStatusByte`。
只有 `bit0` 有定义 = **该设备自身供电正常**（握手成功后由 0 变 1）；其余位语义未知，勿解释。

### 3.5 设备 / 显示器枚举

- `AC_GetDevices`：枚举端口类（`GUID_DEVCLASS_PORTS`）在位设备，取实例 ID 含 `VID_2E3C&PID_5740` 的 COM 口
  （`PortName` 在设备键的 `Device Parameters` 下，不在 driver key），逐个握手判型；`inUse` 用 `CreateFile` 探测。
  UI 在打开设置页时调一次，之后监听 `deviceChanged` 计数值变化再调。
- `AC_GetMonitors`：DXGI `EnumAdapters1/EnumOutputs`，只返回 `AttachedToDesktop` 的输出，**返回顺序即 `monitorIndex`**。

### 3.6 显示与电源事件矩阵

总原则：**采集侧的任何故障都不传导为串口断开**。串口连接与采集会话是两条独立生命周期；
采集失效时灯带按 `waitDispPolicy` 处理，核心自动重建采集，恢复后无缝续流。

| 事件 | 检测信号 | 核心动作 | 目标状态 |
|---|---|---|---|
| 显示器热插拔 / 内外屏切换 / Win+P | `AcquireNextFrame` 返回 `DXGI_ERROR_ACCESS_LOST`；辅以监视线程每秒比对输出列表 | **全量重建采集**：重新枚举适配器 → 输出 →（适配器可能已变，需重建 D3D 设备）→ `DuplicateOutput`。配置的 `monitorIndex` 不存在时回退主屏并置 `monitorFallback=1` | RUNNING（约 200ms）或 WAIT_DISP |
| 锁屏（安全桌面） | `AcquireNextFrame` 返回 `E_ACCESSDENIED` | 每 500ms 重试采集；串口保持连接；灯带按 `waitDispPolicy`（进入时发一帧，之后每 2s 重发） | WAIT_DISP |
| 熄屏（DPMS） | `PowerSettingRegisterNotification(GUID_MONITOR_POWER_ON)` | 同上 | WAIT_DISP → RUNNING |
| 睡眠 S3 / 休眠 S4 | `PowerRegisterSuspendResumeNotification` | 挂起：主动发关灯帧并停流（避免半帧）；恢复：**立即**全量重建（D3D → DDA → 串口重握手），500ms×5 快速期后转 2s 常规期 | STOPPED → STARTING → RUNNING |
| 恢复后 COM 号变化 | 配置端口打开失败 + 枚举发现 VID/PID 匹配的新端口 | 按 §3.4 端口身份策略接管新端口，`ACStatus.comPort` 反映实际端口 | DEV_LOST → RUNNING |
| 灯带 USB 掉线 | 串口写失败 / 端口句柄报错 | 关闭端口，2s 重试 | DEV_LOST → RUNNING |
| 独占全屏 / MPO 黑屏 | 连续 N 帧全黑且桌面持续更新 | 不自动切换后端；把 `allBlackFrames` 暴露给 UI 提示 | RUNNING |

实现要点：

- 恢复重建**必须从适配器枚举开始**：笔记本双显卡切换显示路径时适配器本身会变，只重建 Duplication 不够。
- 拓扑切换瞬间可能连续多次 `ACCESS_LOST`，重建入口加 200ms 去抖。
- 所有重建路径在核心线程内**串行**执行，禁止并发重建（双端口争用的经典事故）。

### 3.7 ABI 稳定性规则

- 结构体以 `structSize` 开头；新增字段**只能追加在末尾**。旧 UI 传旧 size 时，核心按旧布局解释。
- 导出函数一旦发布不得改签名，只能新增；错误码只增不改义。
- ABI v1 明确不做：回调、跨 ABI 分配、宽字符接口、x86 构建（仅 x64）。

### 3.8 拾音模式

**会话语义**：`modeIndex=1 && audioSource∈{0,2}`（主机拾音）时该会话**不建立 DDA 采集** ——
锁屏 / 熄屏 / 拓扑变化不影响律动（不进入 `WAIT_DISP`）；进入时释放 DDA，退出时重建。

音频端故障绝不传导为串口断开：默认设备切换 / 端点失效 → 音频会话原地重建（500ms 退避），
期间 `audioOk=2`、音量按 0 下发，串口与状态机照常。

**音源引擎**（进程单例，多会话共享同一包络快照；参数取"最后写入者"）：

- 捕获：共享模式、事件驱动；回环 = 渲染端点 + `AUDCLNT_STREAMFLAGS_LOOPBACK`，麦克风 = 采集端点普通捕获。
  混音格式 float32（其他格式拒绝并重试），1s 缓冲，事件节拍约等于设备周期（10ms）。
- 主音量标量 10Hz 轮询缓存（`audioFollowVolume=1` 时乘入输出）。
- 端点通知：`IMMNotificationClient`（无窗口）感知默认设备变化 → 重建；切换回环↔麦克风同样触发重建。
- **指定端点**：`AC_GetAudioEndPoints` 列出 ACTIVE 端点（不打开设备）。`audioSource=2` 用 `audioDevId`，
  `audioSource=0` 用 `audioOutDevId`（可指定非默认输出）。打不开（拔出/失效）自动回退系统默认并记 WARN；
  设备重插后自动切回。只有 ID 为空（跟随默认）时才响应系统默认设备切换。
- 麦克风受系统隐私设置约束：被拒绝时可能只收到静音包。

**包络算法**（对每个约 10ms 包，float32 交错样本）：

1. 全频能量 = 通道峰值 `|x|` 的 RMS（避免 L/R 反相抵消）；低频能量 = 均值单声道经 120Hz 双一阶低通后的 RMS；
2. 慢速峰值归一（AGC，τ≈5s，地板 0.02 / 0.006）：各自归一到 `[0,1]`，安静音源自动抬升（`audioAgc=0` 时按 1.0）；
3. 混合 = `(1−audioBass)·normFull + audioBass·normBass`（两支路独立归一，混音音量不影响权重）；
4. 绝对底噪门限 `audioGate`（AGC **之前**的原始全频 RMS）→ 低于即 0；
5. 柔和度整形（只作用在有声段）：gamma 1→0.5 抬中段（鼓点间隙不再深黑）+ 呼吸下限 0→15%（消除大幅频闪）；
6. 包络：起音平滑（10–100ms 随 `audioSoft`）、慢落（`audioRelease` 时间常数）；静默时仍按 release 落到 0；
7. 最终音量字节 = `envByte × audioGain × brightness × (audioFollowVolume ? 主音量 : 1)`，clamp 0..255。

**发送**：5ms 节拍（`timeBeginPeriod(1)`）发 6 字节 `Adz`，每帧后照常读 1 字节状态；`ACStatus.audioLevel` 随帧刷新。
固件把该字节同时当作整体渲染亮度，因此 **`brightness` 在拾音模式下的语义 = 最高亮度上限**。
`audioSource=1`（MCU 自拾音）时以上全部不适用：多数固件无麦克风，仅表现为约 0.25s 一次的闪烁（硬件限制）。

### 3.9 多会话

- 每 slot 独立：会话线程、配置快照、串口连接、状态机、`DEV_LOST` 重连。
- 全局共享：监视线程（1s 轮询，`deviceChanged` 为全局计数）、电源/显示通知、测试图（`AC_SetTestPattern`
  施加于**所有**活动会话）、`timeBeginPeriod(1)`（首个会话申请，最后一个会话释放）。
- 串口属主：两个会话探测到同一端口时，后者 `CreateFile` 失败（独占）→ 继续试下一个候选端口，
  于是会自动落到另一根灯带；若候选全被占用，则持续 `DEV_LOST` 重试，前者停止后接管。
- ⚠️ "哪个设备行对应哪根物理灯带"取决于枚举顺序 —— **UI 应为每台设备保存 `comPort` 以避免歧义**。

---

## 4. C# 侧映射

```csharp
internal static partial class AmbientCore
{
    private const string Lib = "AmbientCore.dll";

    [LibraryImport(Lib)] private static partial uint AC_Version();
    [LibraryImport(Lib)] private static partial int  AC_Start(ref ACConfig cfg);
    [LibraryImport(Lib)] private static partial void AC_Stop();
    [LibraryImport(Lib)] private static partial int  AC_SetConfig(ref ACConfig cfg);
    [LibraryImport(Lib)] private static partial void AC_GetStatus(ref ACStatus status);
    [LibraryImport(Lib)] private static partial int  AC_GetLastDiag(
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.U1)] byte[] buf, uint cap);

    // 结构体：[StructLayout(LayoutKind.Sequential, Pack = 8)]
    // 字符串字段用 [MarshalAs(UnmanagedType.ByValTStr, SizeConst = N)] + CharSet = Ansi（UTF-8 字节直传）
    // 或直接用 fixed byte[N]，保持 blittable、零 marshaling
}
```

实践建议：

- `ACConfig` 约 200 字节，打包复制成本可忽略，`ref` 直传即可。
- UI 用 250ms 定时器轮询 `AC_GetStatusEx`；`deviceChanged` 变化时才重新枚举设备。
- 退出前调 `AC_Stop`（走后台线程）以确保每个会话都发关灯帧。

---

## 5. 构建与集成

- 原生库由 `native/AmbientCore/build.cmd` 构建（MSVC，`/O2 /LD /std:c++17`，模块：
  `AmbientCore` / `capture` / `colorchain` / `serial` / `audio` / `log`）。
- 托管工程通过 `BuildNativeCore` + `CopyNativeCore` 两个 MSBuild Target 在构建前自动重建，并把
  `AmbientCore.dll` 复制到 exe 旁（裸放，不打进单文件）。
- 更完整的工程结构、构建步骤与开发约定见同目录的 `PROJECT.md`。
