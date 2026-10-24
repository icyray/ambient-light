# tools — 检测探针（给 AI 用，人类可以不看）

> **一句话**：本目录不是应用代码，也不是产品功能。里面全是**回归检测工具**，主要读者是 AI 助手。
> 人类只需要记住一条规则：
> **改了 UI / 文案 / 模式表 / 配置字段之后，先跑本目录的探针，出现 `FAIL` 就别提交。**

这些工具的存在理由：本项目的很多真相只有运行时才知道 —— XAML 里写错图标名只在运行时炸、
主题字典挂错位置是**静默失效**、文案键漏一个在切语言时静默显示 key 原文、模式表与磁贴不一致
不会编译报错。单元测试框架能做的有限，而它们都能用"加载真页面 + 断言规则"抓住。

---

## 0. 使用约定（AI 请照做）

- **判据永远是输出里的 `FAIL` 行数**：`0 FAIL` 才算过。`OK` 行数 = 断言总数，会随版本持续增长，
  **不要写死数字、也不要把"OK 变多"当回归**。
- **只断言"程序自己算出来的 / 有规则的"东西**：纯函数真值表、从对象自身读出的不变量（如
  banner 外圆角 − 边框厚 = 光层内圆角）、机制是否就位（模板能否渲染、资源能否解析、事件是否被接管）、
  数据驱动的计数（磁贴数 == 模式表行数，而不是写死 11）。
  **不要断言设计值**：圆心坐标、Margin、颜色字面量、分辨率/显卡型号这类文本 —— 视觉微调就会让探针
  频繁误报（这条写在 `UiLoadProbe/Program.cs` 文件头的契约里，改断言前先读它）。
- **构建时把输出指到独立目录**（`--artifacts-path <构建输出目录>`）：若主程序正在运行，默认输出目录的
  `AmbientLight.dll/.exe` 被占用会构建失败。任选一个与主工程输出不重叠的目录即可。
- **出图参数（`--dump-*`）是给人眼看的设计工具，不参与断言**；`RenderTargetBitmap.Render`
  渲染父容器内的元素会带偏移，必须经 `VisualBrush` 重绘再扫像素。
- 探针产物（`bin/`、`obj/`、`*.exe`、`*.txt`）都**不入库**，源码入库（`.gitignore` 已覆盖）。

---

## 1. `UiLoadProbe` — 无界面 UI 加载探针（主力，日常最常跑）

**干什么**：在 STA 线程里 `new App()`（**不 `Run()`**，所以单例守卫/托盘/核心都不会启动），
实例化全部页面与控件，然后跑几百条断言。它是**唯一**能提前发现"XAML 运行时才炸"和
"资源静默失效"的地方。

| | |
|---|---|
| 用途 | XAML 解析错误、主题/语言机制、表格 ⇄ 磁贴一致性、纯函数真值表、接线断言、像素级观感 |
| 依赖 | `ProjectReference` → `managed/AmbientLight`（改完主工程直接编） |
| 平台 | Windows / STA（WPF 控件必须在 STA 线程创建） |

```bash
# 构建（产物落到独立目录，避开正在运行的 exe 的文件锁）
dotnet build tools/UiLoadProbe/UiLoadProbe.csproj --artifacts-path <构建输出目录>

# 跑全量断言（输出很长，建议落文件再统计）
<构建输出目录>/bin/UiLoadProbe/debug/UiLoadProbe.exe > probe.out
grep -c '^OK'   probe.out      # 断言总数
grep    '^FAIL' probe.out      # 必须为空
```

**判读**：输出按主题分段（`---- §x 主题 ----`），每条一行 `OK   / FAIL <描述> = <实际值>`。
先看 `FAIL` 行，再看它上下的段落标题就知道是哪块出的问题。

**出图模式**（不跑断言，直接产 PNG，用于设计评审与像素对照）：

```bash
# 逐页全页图（可选语言 zh|en、可选宽度；默认 880）
UiLoadProbe.exe --dump-page  <输出前缀> [zh|en] [宽度]
# 滑块皮肤各状态图（普通 / 锚点两侧 / 色带轨道 / 禁用）
UiLoadProbe.exe --dump-slider <输出.png>
# 滑块"活体"渲染（走真实模板，不是手搓）
UiLoadProbe.exe --dump-slider-live <输出前缀>
# 灯环 / 校准窗安装示意（第二个参数 = 只画某个方向）
UiLoadProbe.exe --dump-ring  <输出.png> [0|1]
```

**已知坑**（都在踩过之后写进代码注释了）：`Page` 塞进宿主时要先摘出 `Content`；
`Loaded` 不会触发，需要手动 `RaiseEvent`；折叠容器（`CardExpander`）内模板未应用 —— 时机类逻辑验不到；
需要 `App._instance` 时先反射注入；**无头探针里不要泵 Dispatcher 消息**（放出的排队回调会抛异常）。

---

## 2. `LedRateProbe` — 灯带刷新率探针（C++，需真机）

**干什么**：UFO-test 风格 —— 光团按时间相位绕圈（线速度与帧率解耦），以**可控的发送帧率**写串口，
逐档统计 `achieved / 间隔抖动 / late / 单帧写耗时`，用来实测"这根灯带在哪一档开始写阻塞"。
它的产出是本项目"帧率上限 144、默认 60"的直接依据。

| | |
|---|---|
| 构建 | `bash tools/LedRateProbe/build.sh`（**不走 vcvars**：某些受限环境会禁用 `reg.exe`，导致 `vcvars64.bat` 失败，脚本改为手工拼装 MSVC/SDK 环境后直接调 `cl`；脚本顶部写有 MSVC 与 Windows SDK 路径，按实际安装位置调整） |
| 依赖 | 复用 `native/AmbientCore/serial.cpp`（真实的握手与定长帧写路径） |
| 前置 | **必须先退出 AmbientLight** —— 应用独占串口；直连灯带控制的 COM 口 |
| 产物 | `led_rate_probe.exe`、`*.txt` 报告（均不入库） |

```bash
led_rate_probe.exe --help                          # 全部参数
led_rate_probe.exe 120 10                          # 单档：120fps 跑 10 秒
led_rate_probe.exe --scan 30,60,120,144,145 --dwell 5   # 逐档扫描
led_rate_probe.exe --dry                           # 不开串口，自检时序/相位/统计（无需硬件）
```

注意：`--out FILE` 让程序自己写 UTF-8 文件，**别用 shell 的 `>` 重定向**（Windows PowerShell 下中文会双重编码失真）；
每档开始整带**蓝闪一次**作为档位标记；`--dry` 可在没有灯带时验证时序逻辑。

---

## 3. `check_loc_keys.py` — 文案键守护网（无需构建，纯 Python）

**干什么**：把"文案"这件事做成可对账的 —— C#/XAML 的引用 ⇄ 两本字典 ⇄ 模式表 ⇄ 语言表。
九项检查，最常抓到的三类是：**漏引用的键**、**字典之间键集不一致**（切语言时那行显示 key 原文）、
**XAML 里残留中文字面量**。

| | |
|---|---|
| 运行 | `python tools/check_loc_keys.py`（任意工作目录，路径按脚本位置推导） |
| 判据 | **退出码 0 = 全过**；非 0 = 有问题（看上面哪一行不是 `✓`） |
| 设计原则 | **能反推的绝不写死**：字典清单扫目录、模式号从 `ModeCatalog.Modes` 反推、键命名域从字典键推导 |

```bash
python tools/check_loc_keys.py; echo "exit=$?"     # 期望 exit=0
```

维护含义：**加一种语言**只需"放字典文件 + 在 `LanguageService.All` 登记一行"，本脚本不用改
（除非该语言本身用汉字，要登记进 `CJK_KEYS`，否则汉字残留会误报）；**加一款固件特效**只需加进
`ModeCatalog.Modes`，脚本会自动把它纳入"文案齐备 / 磁贴对账"。

---

## 4. `managed/probe` — 原生核心联调探针（**不在本目录**，同级兄弟）

用真串口 / 真核心 DLL 跑生命周期与协议：启动、热更参数、多会话 slot、停止/重启、扩展帧、
MCU 特效透传、拾音、模式越界拒绝。**需要真机与独占端口**；`probe_core.cs` 顶部写有一个
`AmbientCore.dll` 的绝对路径，需要改成你的构建产物位置。

```bash
dotnet build managed/probe/probe_core.csproj
managed/probe/bin/Debug/net10.0-windows/probe_core.exe
```

> `probe.csproj` 是早期版本的残留（对应的 `probe.cs` 已不在仓库），**不要拿它当入口**，
> 实际跑的是 `probe_core`。

---

## 5. 维护本目录的约定

1. **改动 UI / 文案 / 表格后，探针必须 `0 FAIL` 才允许提交**；新增功能优先补一条断言。
2. 新断言写在 `UiLoadProbe/Program.cs` 的 `Run()` 里，格式统一为
   `Console.WriteLine((ok ? "OK   " : "FAIL ") + $"{描述} = {实际值}")` —— 便于 `grep '^FAIL'`。
3. **"纯函数真值表挡不住调用点写错"**：规则类逻辑除了真值表，还要补一条**接线断言**
   （真的去建一个控件/读一次配置，确认调用点接对了）。
4. 断言涉及"文案/条数"时，从**唯一事实源反推**（字典键、`ModeCatalog`、`LanguageService.All`），
   不要写死清单。
5. 新增工具请沿用本目录风格：单文件可跑、`--help` 说清参数、产物不入库，并在本文件补一节。
