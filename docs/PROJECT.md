# PROJECT.md — 工程说明

面向要读代码 / 改代码的人（和 AI）。**产品介绍见 `README.md`，原生核心的 C ABI 见 `AmbientCore_API.md`，
检测工具见 `tools/README.md`。** 本文只讲"这个工程怎么组织、有哪些不能踩的约定"。

技术栈：**.NET 10 + WPF（WPF-UI Fluent）外壳 + C++17 原生核心 `AmbientCore.dll`**，便携发布。

非目标（明确不做）：Linux / macOS 支持、灯光分组编排、社区特效市场。

---

## 1. 仓库结构

```
ambient-light/
├─ native/AmbientCore/          C++17 原生核心 → AmbientCore.dll
│   ├─ AmbientCore.cpp/.h         导出接口 / 会话线程 / 可靠性状态机 / 配置校验
│   ├─ capture.cpp/.h             DDA 抓屏 + 计算着色器取色 + 显示器枚举 + 黑边检测
│   ├─ colorchain.cpp/.h          HSL / 伽马 / 增益 / 输出色序 / 低灰修正
│   ├─ serial.cpp/.h              定长帧协议 + 握手 + 端口枚举（带进程内访问锁）
│   ├─ audio.cpp/.h               WASAPI 回环 / 麦克风 + 音量包络
│   ├─ log.cpp/.h                 日志
│   └─ build.cmd                  vswhere 定位 MSVC，cl /O2 /LD
├─ managed/AmbientLight/        net10.0-windows WPF 客户端
│   ├─ App.xaml(.cs)              单实例守卫 / 托盘 / 核心生命周期 / 崩溃兜底
│   ├─ Interop/AmbientCore.cs     LibraryImport P/Invoke + 结构体映射
│   ├─ Services/                  配置 / 轮询 / 主题 / 语言 / 显示器映射 / 开机自启
│   ├─ Views/                     主页 + 四个设置页 + 校准窗 + 模式表 / 设备类型表
│   ├─ Controls/                  灯环预览 / 滑块皮肤 / 设置行 / 通用弹窗
│   ├─ Localization/              中英文字典
│   └─ Styles/SliderStyle.xaml    滑块控制模板（见 §7.4）
├─ managed/probe/               原生核心联调探针（开发工具，不随发布）
├─ tools/                       检测探针（见 tools/README.md）
├─ assets/                      README 截图
└─ docs/                        本文件与核心接口文档（PROJECT.md / AmbientCore_API.md）
```

依赖：`WPF-UI 4.3.0`（Fluent 控件与主题字典）、`Hardcodet.NotifyIcon.Wpf 2.0.1`（托盘）。

---

## 2. 架构与数据流

```
┌─────────────────────────── UI 进程 (C# / .NET 10 / WPF) ───────────────────────────┐
│  导航壳 + 主页 + 追光/拾音/颜色/高级设置   │  每 250ms 轮询 AC_GetStatusEx(slot)    │
│  改配置 → AC_SetConfigEx()（原子快照，即时生效）                                    │
└──────────────┬─────────────────────────────────────────────────────────────────────┘
               │ P/Invoke（cdecl, x64, UTF-8, 无回调 / 无跨边界分配）
┌──────────────▼────────────────────── AmbientCore.dll ──────────────────────────────┐
│  每 slot 1 条会话线程：抓帧 → 着色器采样 → 颜色链 → 串口写                          │
│  音频线程（0/1 条）：WASAPI → 包络 → 原子音量（进程单例，多会话共享）               │
│  监视线程（1 条）：设备热插拔轮询 / 电源与显示事件                                  │
└───────────────────────────────────────────────────────────────────────────────────┘
```

分工是硬边界：**UI 进程不接触 D3D、串口、WASAPI**。这条边界让核心可以脱离界面单独跑探针，
也让 UI 崩溃不会把灯带留在半帧状态。

界面侧的分层：

| 层 | 职责 | 典型文件 |
|---|---|---|
| Views | 页面结构、绑定、事件 | `Views/*.xaml(.cs)` |
| Services | 无 UI 的可复用逻辑（配置、轮询、主题、语言、显示器映射） | `Services/*.cs` |
| Interop | 唯一允许接触原生 DLL 的地方 | `Interop/AmbientCore.cs` |
| Controls | 跨页复用控件与皮肤 | `Controls/*`、`Styles/*` |

生命周期调用（`AC_StartEx` / `AC_StopEx`）统一走 `App` 的后台串行队列：这些函数会 join 会话线程，
**绝不允许在 UI 线程直接调用**（详见 `AmbientCore_API.md §3.4`）。

---

## 3. 构建与发布

### 环境要求

| | 要求 |
|---|---|
| 操作系统 | **Windows 11 x64**（Windows 10 未验证） |
| .NET | SDK **10.0+**（Release 发布为 self-contained，终端用户无需安装运行时） |
| 原生 | Visual Studio 2022/2026，含"使用 C++ 的桌面开发"工作负载（MSVC v143+、Windows SDK 10.0.26100+） |
| 硬件 | 灯带控制器（USB CDC 虚拟串口）；默认 60 灯，固件上限 180 灯 |

### 命令

```bash
# 全量构建（原生 DLL 会在托管 Build 前自动重建，源码未变则跳过）
dotnet build AmbientLight.slnx -c Debug

# 发布 A：self-contained（自带运行时，解压即用）
dotnet publish managed/AmbientLight/AmbientLight.csproj -c Release -r win-x64 \
  -p:SelfContained=true -p:PublishSingleFile=false -o out/self-contained

# 发布 B：framework-dependent（目标机器需先装 .NET 10 Desktop Runtime）
dotnet publish managed/AmbientLight/AmbientLight.csproj -c Release -r win-x64 \
  -p:SelfContained=false -p:PublishSingleFile=false -o out/framework-dependent
```

- 原生部分由 `native/AmbientCore/build.cmd` 独立编译（`vswhere` 定位 MSVC，版本无关），也可单独执行。
- 托管工程用两个 MSBuild Target 串起来：`BuildNativeCore`（源码变化才重编）+ `CopyNativeCore`
  （把 DLL 复制到 exe 旁）。
- 两种形态的产物都是 `AmbientLight.exe` + **`AmbientCore.dll` 裸放旁边**（`config/` 运行时生成）；
  均不做单文件、不裁剪（WPF 不支持 `TrimMode=full`）。
- CI：`.github/workflows/build.yml` 一次构建两种形态并上传 artifact；`publish.yml` 手动填 tag /
  分支 / Release Note 后复用 build 并创建 Release（附两个 zip 与 `SHA256SUMS.txt`）。

### 配置与日志

| | 位置 |
|---|---|
| 配置 | `<程序目录>/config/app.json`（全局）+ `config/device_<类型>_<编号>.json`（每设备） |
| 回退 | 程序目录不可写时自动改用 `%APPDATA%\AmbientLight\config`（界面显示实际生效路径） |
| 日志 | `<程序目录>/log/ambientcore.log`、`log/crash.log` |
| 调试日志 | 环境变量 `AL_LOG_LEVEL=debug` 后重启（缺省不落盘调试级日志；未做轮转） |

配置文件是热更新的（改完立即下发核心），高频参数在滑条停手后延迟落盘，避免拖一次滑条写几十次磁盘。

---

## 4. 单一事实源（改一处必须同步另一处）

| 事实 | 唯一来源 | 派生/同步点 |
|---|---|---|
| 模式（名称/说明/亮度语义/UI 分区/指标集） | `Views/ModeCatalog.cs` 的 `Modes` 表 | 主页模式卡与固件特效磁贴、`Mode.*` 文案键、指标集、探针断言 |
| 设备类型（名称/图标） | `Views/DeviceTypeCatalog.cs` 的 `Types` 表 | 设备行、`Dev.*` 文案键 |
| 界面语言 | `Services/LanguageService.All`（`LangDef` 表） | 枚举成员、`Strings.<key>.xaml`、语言下拉 |
| 帧率档位 | `Views/SettingsPageCapture.xaml.cs` 的 `FpsCapChoices` + 设置页 XAML 项 + native `ValidateConfig` | 三处必须同时改（探针有"上限 == 144"断言） |
| 滑块皮肤契约 | `Styles/SliderStyle.xaml` + `Controls/SliderSkin.cs` | 见 §7.4 |
| ABI 布局 | `native/AmbientCore/AmbientCore.h` | `Interop/AmbientCore.cs` 的结构体镜像；`AmbientCore_API.md` 是解释文档 |

**表外数据必须有显式降级路径**：模式表外的模式号会走 `ModeCatalog.Unknown`（亮度禁用、只保留真实指标、
副标题说明"未识别"），不允许"猜一个最像的分支"。

> 本项目的历史教训是"把范围写死"：`zh/en` 曾写成"非中文即英文"，模式号曾写成手写数组，
> 语言清单曾写成常量元组。凡是"枚举一堆值"的地方，都应当有一张表 + 由表派生的清单。

---

## 5. 串口与设备（核心不变式）

1. **帧是定长 543 字节定界的**（3 字节头 + 180×3）。固件按字节数给帧定界，写一半会让它永久错位 →
   所有写路径都必须 `WriteAll` 补齐（预算 3s），不允许"写失败就丢掉这帧"。
2. **写超时 ≠ 掉线**：设备可能正在收尾一段内部动画，暂时不读 CDC。只有端口真没了才判 `DEV_LOST`。
3. **端口身份**：配置端口为空 → 按 VID/PID 跟随；配置端口存在 → 只用它，握手失败即失败，**不回落**；
   配置端口不存在 → 才退回按 VID/PID 跟随。两种语义不能混。
4. **端口访问必须过锁**：核心内部的串口访问统一走 `ProbeLock()`，避免监视线程与数据线程抢同一个句柄。
5. **不要相信"设备已连上"以外的状态位**：`ledStatusByte` 只有 `bit0` 有定义（设备自身供电正常），
   其余位语义未知；`bit0 = 0` 无法区分"真异常"与"这一帧状态还没读到"，所以 UI 只在 `bit0 = 1` 时提示"供电正常"。

---

## 6. 输出包络与追光平滑

所有"开灯 / 关灯 / 熄屏 / 恢复 / 切模式"都走**一条输出包络**（0..1 乘子，在发帧前相乘），
绝不把包络值写回配置。时长常量集中在 `AmbientCore.cpp` 顶部的常量区，改那里即可整体调节。

- 熄屏策略（`waitDispPolicy`）：保持最后帧 / 最暗 / 熄灭；进入状态时发一帧，之后每 2s 重发防丢帧。
- 睡眠挂起时先发关灯帧再停流，避免半帧；唤醒后从适配器开始全量重建，并给 1.5s 拓扑宽限期。
- `chaseSmoothMode` 是**追光专用**的输出层柔化：`1` = 抑制闪光（整帧平均亮度突变限幅），
  `2` = 1 + 时域柔和（逐灯逐通道指数平均，快速摇镜更顺但轻微拖影）。字段用符号承载开关语义：
  `+n` 开启档位 n、`-n` 关闭但记住档位、`0` 从未启用。
- 拾音模式的亮度不是包络乘子，而是**最高亮度上限**（乘在音量包络上）。

---

## 7. 界面约定

### 7.1 行尾与编码

- 工作副本一律 **CRLF**（`.gitattributes` 已定），索引 LF；`*.py` 例外（LF）。
- 不要用文本模式脚本批量读写 XAML（会吞 CRLF 或加 BOM）；需要时按字节读写。
- 文案字典里多行值必须写成 `<sys:String xml:space="preserve">` + `&#10;`，否则 XAML 会把换行归一成空格；
  XML 注释里不能出现 `--`。

### 7.2 主题（浅色 / 深色 / 跟随系统）

- `app.json` 的 `theme` 默认 `dark`；三种取值：跟随系统 / 浅色 / 深色。
- `App.xaml` 里的 `ThemesDictionary` 必须是 `Application.Resources.MergedDictionaries` 的**直接成员** ——
  主题切换靠替换这个字典实例生效，放错层级是**静默失败**。
- `App.xaml` 上的 `Theme="Dark"` 行必须保留：它决定第一帧的底色，删掉会闪一下浅色。
- 跟随系统 = `SystemEvents` + 窗口消息钩子；强调色只从 `WM_DWMCOLORIZATIONCOLORCHANGED` 得到
  （`SystemEvents` 收不到），`WM_SETTINGCHANGE` 需按 `ImmersiveColorSet` 过滤。消息可能连发，做 200ms 去重。
- 用 `StaticResource` 钉死的刷子**不会**因为换主题自动重算（字典实例没换时引用不变）→
  需要跟随主题的地方要订阅应用自己的 `AppearanceChanged`，或改用 `DynamicResource`。
- 说明文字统一用 `TextFillColorSecondaryBrush`；不要用 `Opacity` 压暗文字（浅色下会发虚）。
- 启动顺序：加载配置 → 应用主题/语言 → 创建主窗口。
- 硬编码颜色的合理例外：主页 banner、灯环视图、校准窗（恒深面板）、颜色设置页的色块。

### 7.3 i18n（中 / 英）

- 全部文案外化到 `Localization/Strings.<key>.xaml`；`App.xaml` 只合并一本（默认中文），
  运行时由 `LanguageService` 整本替换。
- **C# 里的文案必须订阅 `LanguageChanged` 刷新**；XAML 用 `{DynamicResource}` 的地方自动刷新。
- 加一种语言 = 枚举成员 + `LanguageService.All` 加一行 + 一个字典文件；**不允许**写"非中文即英文"的分支。
- 语言的自称（endonym）写死为该语言自身的字面量，不随界面语言变化。
- 校准窗在打开状态下切语言不会即时刷新（已知取舍，下次打开生效）。

### 7.4 滑块皮肤

上游 WPF-UI 的 Slider 模板缺进度填充的挂点、也没有禁用态触发器，所以本项目自带一份控制模板
（`Styles/SliderStyle.xaml`）+ 页面侧附加属性（`Controls/SliderSkin.cs`）。

- 三种模式：`Track`（默认，填充跟值）、`Anchor`（倍率类参数，锚点=中性值）、`None`（色带轨道，如色相/饱和度）。
- 填充只能用 WPF 自己的挂点 `PART_SelectionRange`（几何由 WPF 计算，没有时机问题）；
  承载它的 Canvas 需要负左外边距 = −拇指宽度/2（绑定 `Thumb.ActualWidth`）来抵消左端内缩。
- `IsSelectionRangeEnabled` 是权威开关：模板里置假即整层收起。
- 禁用态填充用 `TextFillColorDisabledBrush`（与轨道底 `ControlFillColorDisabledBrush` 是**不同**的键）；
  填充靠拇指那端是方角，自由端是圆角。
- **不要在运行时去模板里改元素**；`ControlTemplate.Triggers` 里 `DataTrigger` + `Binding(TemplatedParent)`
  不生效；嵌套模板的 `FindAncestor` 不跨模板边界。
- `App.xaml` 里合并皮肤字典的那一行必须排在 `ui:ControlsDictionary` **之后**，否则被上游覆盖且无任何报错。

### 7.5 常用控件坑位

- `SettingRow` 必须以 `SetResourceReference(StyleProperty, typeof(CardControl))` 应用样式，直接赋 `Style` 会丢主题绑定。
- `AnimatedVisibility` 有四个坑：同值不回调、动画结束会回落基准值、要显式 `Height=0` 钳高、
  基准 `Margin` 只在 `Loaded` 捕获一次。
- 横向 `WrapPanel` 不折行通常不是 WrapPanel 的问题，而是上游给了无限宽度 → 外层用 `Grid(Auto/*)`。
  `UniformGrid` 放按钮/文字时用 `MinWidth`，长文案会顶出格子压到邻居。
- 弹窗统一走 `AppDialogs`（需要 `ContentDialogHost`）；破坏性操作的确认框用 `Danger` + `DefaultButton=Close`。
- WPF 的 `Border` 不裁剪子元素圆角；解析期的事件处理器要判空；显式写 `Style` 必须 `BasedOn`。
- 线性渐变的角度用 `StartPoint=(0,0)`、`EndPoint=(cosθ, sinθ)` 表达。

### 7.6 主页（Home）

自上而下的固定顺序，改动前先对照这里：

```
banner（状态 + 实时辉光）
 └ 提示条（InfoBar）
    └ 亮度行
       └ 三张主模式卡（屏幕追光 / 拾音律动 / 纯色灯光）
          └ 纯色拾色器（仅纯色模式）
             └ 固件特效磁贴（折叠）
                └ 布局预览 · 灯环几何（折叠）
                   └ 运行指标（一行状态条）
                      └ 设备列表
```

间距口径：**卡片之间 4，并列元素之间 8，页根 `16,12,16,16`**。

- **banner**：`DEV_LOST` / `ERROR` 时**不点亮**（渐变亮起 = 确实驱动上了灯带）；辉光用常量色 + 只给
  `Opacity` 做动画（画刷只创建一次，改色用 `ColorAnimation`）；光带 `Rectangle` 必须自带
  `RadiusX/RadiusY`（WPF 的 `Border` 不裁子元素圆角）。
- **提示条**：只用 `InfoBar` 自己的 `IsOpen`，不要再叠 `AnimatedVisibility`（模板会自行设 `Collapsed`，动画无效）。
- **模式卡**：选中色用 `AccentFillColorDefaultBrush`，不要用 `SystemAccentColorPrimaryBrush`（后者是写死的强调色）；
  选中底色要铺满整卡（`Padding` 挂在 `ContentPresenter` 上）；亮度行紧贴 banner。
- **拾色器**：H + S 两根滑块，**V 由主页"亮度"行承担**（不再单独放明度滑块）；色带轨道
  `SliderTrackFill=Transparent`；自定义色槽在停手 600ms 后才写入配置。
- **固件特效**：图标磁贴网格，折叠头右侧显示「当前 XX」胶囊；固件特效不参与逐灯取色（灯环不画），
  光带用中性白灰。
- **运行指标**：压成一行；指标集由模式表决定，"不适用"显示 `—` 而不是 `0.0`。
- **设备行**：类型图标 + 名称 + 语义胶囊 + 第二行小字；「当前」用左侧 3px 强调条表达。
- **布局预览**：几何环收进折叠区，中心只放静态几何参数（不显示实时颜色）。
- **高频参数不落盘**：拖动类改动走内存热更新，停手后才保存（亮度停手 400ms 落盘）；
  亮度是**伽马之后**的线性乘数（详见 §6）。

### 7.7 设备行与状态口径

- 设备行第二行的端口，优先级固定为：**手动指定的端口 > 会话实际端口 > 枚举到的端口**。
  手动指定时用警示色，且文案要说明"仅对当前选中设备生效"。
- 状态胶囊：配置端口不在位 → 「端口不在位」；有会话但 `DEV_LOST` 且手动指定 → 「指定端口无应答」；
  其余情况才用「灯带不在位」。**不做端口预检**（"在位但不是灯带"需要真开端口握手约 1 秒并独占端口，
  不能塞进 4Hz 的列表刷新里）。
- 设备行与会话的配对：**按设备 key 优先**，其次按端口，最后才用"端口被占用 + 恰好一个非停止会话"兜底。
  key 优先是硬要求 —— 否则会话连不上时（状态里还没有端口）整行会认不到自己。
- `ConfigStore.SelectedChanged` 一类"当前设备切换"的事件，注意主页的亮度/模式/拾色器只在加载时读一次配置；
  多设备场景下要显式刷新，否则会出现"banner 说纯色、模式卡勾着追光"。

---

## 8. 检测工具与提交纪律

| 工具 | 用途 |
|---|---|
| `tools/UiLoadProbe` | 无界面加载全部页面并断言（XAML 解析、主题/语言机制、表格一致性、纯函数真值表、接线断言） |
| `tools/LedRateProbe` | 实测灯带刷新率与写阻塞点（需要真机与独占串口） |
| `tools/check_loc_keys.py` | 文案键对账（引用 ⇄ 字典 ⇄ 模式表 ⇄ 语言表） |
| `managed/probe` | 原生核心生命周期与协议联调 |

提交前的要求：

1. **UI / 文案 / 表格类改动，先跑 `UiLoadProbe`，`0 FAIL` 才提交**；`OK` 数量会随版本增长，不要写死。
2. 断言只针对"程序自己算出来的 / 有规则的"东西（真值表、不变量、机制是否就位、数据驱动计数），
   **不要断言设计值**（坐标、边距、颜色字面量）—— 视觉微调会让探针天天误报。
3. **纯函数真值表挡不住"调用点写错"**：规则类逻辑除了真值表，还要有一条接线断言
   （真的建一个控件 / 读一次配置，确认调用点接上了）。
4. 文案与计数从**唯一事实源反推**（字典键、`ModeCatalog`、`LanguageService.All`），不要写死清单。

细节见 `tools/README.md`。

---

## 9. 已知限制与路线图

**限制**

- 采集后端目前只有 **DDA**；独占全屏 / MPO 下可能出现全黑帧（界面会提示）。
- 设备按 **VID/PID** 枚举：更换芯片方案或走 USB 转串口桥接（CH340 / CP2102 等）的设备枚举不到，
  需要在高级页手动指定端口；且"手动端口"目前仍以枚举为前提，不是独立入口。
- 设备身份 = 设备类型 + 握手返回的编号（2 位），**两根同型号设备可能共用同一份配置**（未在多设备上验证）。
- 多设备时诊断信息取 slot 0，可能张冠李戴。
- 不做自动更新（便携软件，手动升级）；不提供应用内自选强调色（跟随系统）。
- 原生 ABI 只在 x64 下验证；不做 x86 / ARM64。

**路线图**

- **WGC 采集后端**：采集后端抽象 + `DispatcherQueue` 专用线程 + `FramePool` + `CreateForMonitor`；
  可在 Win11 下关闭采集边框；配置项 `captureBackend` + 采集页下拉 + 探针用例。
- **拾音增强**：把"声音驱动亮度"扩展成追光 / 纯色的动态效果；纯色呼吸。
- **颜色校准**：更精确的低灰与色域校正。
- ~~Windows 动态光效接入~~（暂不实施）。

---

## 10. 术语

| 术语 | 含义 |
|---|---|
| MCU / 固件 | 灯带控制器上的单片机与其固件；**固件特效**指由它自己渲染、主机只发"选哪一款"的模式 |
| 主机模式 | 主机逐灯计算颜色并下发帧的模式（屏幕追光、纯色） |
| 会话 / slot | 一台设备对应的一条独立运行上下文；最多 `AC_MAX_DEVICES` 条 |
| 追光 | 屏幕边缘取色 → 灯带（screen sync） |
| 拾音 | 系统声音 / 麦克风 → 音量包络 → 灯带（audio reactive） |
| 灯序偏移 | 灯带物理起点与协议起点之间的错位补偿（`ledRotate`） |
| 输出包络 | 乘在最终发帧前的一条 0..1 曲线，负责淡入淡出与熄屏过渡 |
