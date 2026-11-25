# Hypera —— 面向 .NET 10 的现代化跨平台 Emuera 编译与运行时引擎

**Hypera** 是从零设计的 `.NET 10` 跨平台、100% 实例隔离的 Emuera（eramaker / EM+EE / Emuera.NET 派生脚本）静态编译器、寄存器虚拟机、Skia 渲染合成器与资源打包工具链。

它既可作为桌面端（WinForms / WPF / MAUI）与终端（`hypera` CLI）的独立执行内核，也可作为高并发多租户 Web 宿主（如 CloudEmuera）的无阻塞嵌入式引擎。

---

## 核心特性与架构亮点

- **零全局可变状态（Zero `GlobalStatic`）**：所有变量表、角色表、控制台缓冲、随机数生成器（SFMT-19937）、图形句柄、自定义字体与音频状态严格隶属于单个 `EmueraEngine` 实例；同一进程可并发运行多个互不干扰的引擎实例，并零拷贝共享不可变的编译产物 `BytecodeModule`。
- **严格多遍静态编译（4-Pass Compiler）**：完整覆盖 Emuera / EM+EE / Emuera.NET 的 **304 条指令**、**270 个内置方法**与 **171 个内置变量**；支持混合 Shift-JIS / UTF-8 / GB18030 自动编码识别、`*#*` 优先目录、`#DIM`/`#DIMS`（`CONST`/`SAVEDATA`/`CHARADATA`/`GLOBAL`/`DYNAMIC`/`REF`）、`_Rename.csv` / `_Replace.csv` / `*.erd` / `*.als` / `VarExt*.csv`，以及 `;!;`、`CompatiCALLNAME`、`CompatiRAND`、`SIF` 空行等 eramaker 兼容开关。
- **高性能平坦 16 字节寄存器虚拟机**：
  - 所有字节码采用固定 16 字节 blittable `Instruction` 布局，配合 `long[]` / `string[]` 双类型分离寄存器组实现零装箱执行。
  - 内置 `ForInit`/`ForStep`、`RepeatInit`/`RepeatStep` 循环超指令与 `Span<T>` / `Vector<long>` SIMD 数组运算：**1,000,000 次紧密循环在 Release 下约 ~25 ms**。
  - **无栈协程挂起**：执行 `INPUT` / `ONEINPUT` / `TINPUT` / `BINPUT` / `INPUTMOUSEKEY` / `WAIT` / `AWAIT` 时零阻塞 OS 线程，立即返回 `EngineStepStatus.WaitingInput` 或 `Yielded`，并通过代数校验（`GenerationId`）安全过滤过期输入。
- **权威逻辑视口与 Skia + HarfBuzz 合成渲染**：
  - 实例级 VFS `font/*.ttf` 加载，针对像素瓦片字体启用 `SKFontEdging.Alias`、整数像素对齐与 `LineHeight == FontSize` 零像素接缝渲染。
  - 原生 `SKCodec` 静态与多帧动画 **WebP** / PNG / BMP / GIF 解码；完整实现 `GCREATE` / `GDRAWG`（5×5 ColorMatrix）/ `GDRAWGWITHMASK` / `GROTATE` / `HTML_PRINT`（含任意 `<div depth>`、`HTML_PRINT_ISLAND`）/ `CBGSETBMAPG` 与 `<img srcm>` 拾色命中测试（`RESULT:3/4/6`）。
- **只读虚拟文件系统与兼容存档持久化**：
  - 统一通过 `IVirtualFileSystem` 挂载散装目录（`PhysicalDirectoryVfs`）、单文件索引资源包 `.hep`（`HepArchiveVfs` / `HepPackageWriter`，支持 Deflate 与 CRC32 校验）以及 `.zip`（`ZipArchiveVfs`）。
  - 支持传统文本 `.sav`、二进制 `.sav`（Ebdb RLE 压缩）与 GZip 压缩二进制 `.sav`（完整持久化角色表、`#DIM SAVEDATA`/`CHARADATA` 以及 `DataTable` / `XML` / `MAP` VarExt 容器），写入隔离的 `WritableSaveDirectory` 沙箱。
- **旧版 `CALLSHARP` 插件二进制兼容**：`Hypera.LegacyPluginShim`（输出 `Emuera.dll`）通过可回收 `AssemblyLoadContext` 与 `AsyncLocal` 引擎作用域，确保旧版 C# 插件在多实例并发下零串扰运行并支持卸载。

---

## 解决方案与包结构（`Hypera.slnx`）

| 项目 | 目标框架 / 输出 | 职责说明 |
| --- | --- | --- |
| [`src/Hypera.Core`](src/Hypera.Core) | `net10.0` (`Hypera.Core.dll`) | 编译器、多语言诊断与 `HyperaLocalizer`、16B 寄存器 VM、CSV/Config/Save 持久化、VFS（目录 / `.hep` / `.zip`）、Skia 渲染与 `HyperaGameHost` |
| [`src/Hypera.Audio`](src/Hypera.Audio) | `net10.0` (`Hypera.Audio.dll`) | 跨平台 10 路音效（SE `0..9`）+ 1 路循环 BGM 软件混音器 `CrossPlatformAudioMixer` |
| [`src/Hypera.LegacyPluginShim`](src/Hypera.LegacyPluginShim) | `net10.0` (`Emuera.dll`) | 兼容 `MinorShift.Emuera.Runtime.Utils.PluginSystem` 的 `CALLSHARP` 插件垫片（实现 `ISharpPluginHost`） |
| [`src/Hypera.Cli`](src/Hypera.Cli) | `net10.0` (`hypera`) | 命令行工具：交互式 24-bit TrueColor ANSI 终端运行、无头 PNG 帧导出、`.heb` 编译、`.hep` 打包与单文件 `HYPRBNDL` 封装 |
| [`src/Hypera.Views.WinForms`](src/Hypera.Views.WinForms) | `net10.0-windows10.0.19041.0` | DPI 感知 WinForms `HyperaSkiaControl` 宿主控件 |
| [`src/Hypera.Views.Wpf`](src/Hypera.Views.Wpf) | `net10.0-windows10.0.19041.0` | DPI 感知 WPF `HyperaSkiaElement` 宿主控件 |
| [`src/Hypera.Platform`](src/Hypera.Platform) | `net10.0` (`Hypera.Platform.dll`) | 应用目录、版本链与算法 β 智能选版、并行 `.zip`→`.hep` 编译审核流水线、二进制哈希信誉、隔离存档库与共享字节码会话管理器（EF Core SQLite） |
| [`src/Hypera.Web.UI`](src/Hypera.Web.UI) | `net10.0` (Razor Class Library) | 共享 MudBlazor 界面库（Slate & Teal 主题、多语言即时切换、锁定逻辑视口 `PlayStage`、WebAudio 1+10 通道同步与存档管理器） |
| [`src/Hypera.Server`](src/Hypera.Server) | `net10.0` (ASP.NET Core Blazor Server) | 多用户联机与社区托管服务端，默认监听 `http://0.0.0.0:5080`，提供 Cookie 鉴权、后台审核与资源/存档流式下载端点 |
| [`src/Hypera.Maui`](src/Hypera.Maui) | `net10.0-windows10.0.19041.0` / `net10.0-android` | 跨平台 `.NET MAUI Blazor Hybrid` 桌面与移动本地单人客户端（复用 `Hypera.Web.UI` 与 `Hypera.Platform`，免登录本地管理员模式） |
| [`tests/Hypera.Tests`](tests/Hypera.Tests) | `net10.0` (xUnit) | 编译器、多语言诊断、VM 运行时、Skia/WebP/瓦片字体渲染、VFS/存档、音频、插件隔离与端到端集成测试 |
| [`tests/Hypera.Platform.Tests`](tests/Hypera.Platform.Tests) | `net10.0` (xUnit) | 平台数据库、认证授权、并行编译审核、哈希信誉、版本解析、会话与 HTTP 全栈集成测试 |

详细内部格式（`.heb` / `.hep` 字节布局、指令编码与 Z-order 渲染管线）请参阅 [`docs/architecture.md`](docs/architecture.md)。

---

## 快速构建与测试

需要安装 **.NET 10 SDK**：

```powershell
# 编译整个解决方案（Release）
dotnet build Hypera.slnx -c Release

# 运行全部测试套件
dotnet test Hypera.slnx -c Release
```

---

## `hypera` CLI 命令速查

`hypera` 统一复用 `Hypera.Core` 的权威编译器与二进制编解码器，生成的 `.heb` 与 `.hep` 与内核 100% 二进制一致：

```powershell
# 1. 直接运行散装游戏目录、.heb 字节码、.hep 资源包或 .zip 压缩包
dotnet run --project src/Hypera.Cli -c Release -- run <path> [--headless-frames <png_dir>] [--input <val>]

# 2. 将 ERH/ERB 静态编译为 .heb 字节码（魔数 "HEBC"）
dotnet run --project src/Hypera.Cli -c Release -- compile <game_dir> -o game.heb

# 3. 将资产、配置、字体与预编译 .heb 字节码打包为单文件 .hep（魔数 "HEPK"）
dotnet run --project src/Hypera.Cli -c Release -- pack <game_dir> -o game.hep [--no-compress]

# 4. 追加 24 字节 HYPRBNDL 尾部生成独立可执行单文件包（启动时自动识别并挂载）
dotnet run --project src/Hypera.Cli -c Release -- bundle game.hep --host path/to/hypera.exe -o Game.exe
```

---

## 宿主集成指南

### 1. 一行启动（`HyperaGameHost` —— 自动识别目录 / `.hep` / `.zip` / `.heb`）

```csharp
using Hypera.Audio;
using Hypera.Runtime;
using MinorShift.Emuera.Runtime.Utils.PluginSystem;

using var host = HyperaGameHost.Load(
    @"C:\Games\MyEraGame.hep", // 或散装目录 / .zip / .heb
    new HyperaGameLoadOptions
    {
        SaveDirectory = @"C:\Games\MyEraSaves",
        AudioBackend = new CrossPlatformAudioMixer(),
        AutoStartSystemFlow = true, // 自动进入 BEGIN TITLE
    });

// 可选：挂载旧版 CALLSHARP 插件桥接
using var plugins = new PluginManager(host.Engine);
plugins.LoadPlugins();
host.Engine.SharpPluginHost = plugins;

while (true)
{
    EngineStepStatus status = host.Engine.Step();
    if (status == EngineStepStatus.WaitingInput && host.Engine.PendingInputRequest is { } req)
    {
        host.Engine.SubmitInput(new InputResponse(req.GenerationId, IntValue: 0));
    }
    else if (status is EngineStepStatus.Completed or EngineStepStatus.Faulted)
    {
        break;
    }
}
```

### 2. Console / 无头渲染测试宿主

直接使用 `Hypera.Cli` 的 `AnsiConsoleHost` 或调用 `hypera run <path> --headless-frames <dir>`，可将每一帧自动渲染为高保真 Skia PNG 并输出 24-bit TrueColor ANSI 终端界面。

### 3. WinForms 桌面宿主（`Hypera.Views.WinForms`）

```csharp
var host = HyperaGameHost.Load(@"C:\Games\MyEraGame", new HyperaGameLoadOptions { AutoStartSystemFlow = true });
var view = new Hypera.Views.WinForms.HyperaSkiaControl
{
    Dock = DockStyle.Fill,
    Engine = host.Engine,
};
form.Controls.Add(view);
view.StartLoop();
```

### 4. WPF 桌面宿主（`Hypera.Views.Wpf`）

```csharp
var host = HyperaGameHost.Load(@"C:\Games\MyEraGame", new HyperaGameLoadOptions { AutoStartSystemFlow = true });
hyperaSkiaElement.Engine = host.Engine;
hyperaSkiaElement.StartLoop();
```

### 5. .NET MAUI 跨平台宿主

在 MAUI 页面中挂载 `SkiaSharp.Views.Maui.Controls.SKCanvasView`，通过 `ViewportTransform` 将触控/指针物理坐标映射回 `engine.Viewport` 逻辑坐标，在 `PaintSurface` 回调中调用 `SkiaFrameCompositor.RenderScene(e.Surface.Canvas, e.Info.Width, e.Info.Height, engine.ConsoleBuffer.CaptureScene(), engine.Graphics, engine.Sprites)`，并在 `IDispatcherTimer` 中非阻塞推进 `engine.Step()`。

### 6. Web / CloudEmuera 多租户服务端宿主（`IDisplaySnapshotSink`）

实现 `IDisplaySnapshotSink` 并传入 `HyperaGameLoadOptions.DisplaySink`：

```csharp
public sealed class WebSessionSnapshotSink(Action<DisplaySnapshot> pushToClient) : IDisplaySnapshotSink
{
    public void OnDisplaySnapshot(DisplaySnapshot snapshot) => pushToClient(snapshot);
}
```

每当引擎挂起等待输入或帧让出时，Hypera 自动回调 `OnDisplaySnapshot`，服务端无需常驻阻塞线程，可直接将结构化 `DisplaySnapshot`（或离屏渲染帧）经 WebSocket / SignalR 推送至浏览器客户端，收到玩家点击/按键后再调用 `engine.SubmitInput(...)` 恢复执行。

---

## 平台宿主、Web UI 与社区多语言包

### 1. `Hypera.Server`（多用户 Web 托管服务，默认 `0.0.0.0:5080`）

```powershell
# 启动多用户 Web 服务端（默认监听 http://0.0.0.0:5080，数据目录位于 ./data）
dotnet run --project src/Hypera.Server -c Release
```

- **主机模式**：`PlatformHostMode.MultiUserServer`，采用 SQLite WAL（`data/hypera.db`）与 HTTP-only Cookie 鉴权（`/api/auth/register`、`/api/auth/login`、`/api/auth/logout`）。
- **首位自动管理员**：空库下注册的首位用户自动晋升为 `Admin`；普通用户上传的 `.zip` 经并行编译后进入 `PendingReview` 审核队列，管理员审核通过后进入公共池并可标记内含二进制（`.dll`/`.exe` 等）的 `(SHA-256, ByteSize)` 信誉状态。
- **流式下载端点**：`/api/downloads/source/{submissionId}` 严格校验 `AllowSourceDownload` 权限；`/api/downloads/saves/{appId}/zip` 导出当前用户在该应用下的全部存档。

### 2. `Hypera.Maui`（Windows / Android 本地跨平台客户端）

```powershell
# 运行 Windows 桌面版 MAUI Blazor Hybrid 客户端
dotnet run --project src/Hypera.Maui -f net10.0-windows10.0.19041.0 -c Release
```

- 与 `Hypera.Server` 共享 100% 同一套 `Hypera.Web.UI` 与 `Hypera.Platform` 业务层，运行于 `PlatformHostMode.SingleUserLocalAdmin` 免登录本地管理员模式，上传的 `.zip` 并行编译后即时激活可玩。

### 3. `Hypera.Platform` 与 `Hypera.Web.UI`

- **`Hypera.Platform`**：提供 `IAuthService`、`IAppCatalogService`（含原子化 `ForkAppWithPackageAsync` 与历史版本链重排）、`IPackagePipelineService`（启动自动拉起并行编译队列并恢复中断任务）、`IBinaryReputationService`、`IVersionResolutionService`（支持算法 β `KnownLanguagePriority`、`VersionPriority` 与 `AlwaysLastRun`）、`IUserSaveService`（按 `(UserId, AppId)` 跨版本共享并强制单应用配额）以及 `IRuntimeSessionManager`（多用户共享不可变 `BytecodeModule`、单会话互斥、步数/超时防死循环熔断）。
- **`Hypera.Web.UI`**：基于 MudBlazor 的极简 Slate & Teal 界面库。`PlayStage.razor` 直连 `GetSessionRenderStateAsync`、`ReadSessionAssetAsync` 与 `HitTestAndClickAsync`，在锁定 `LogicalViewport` 舞台上原生注册 `font/*.ttf` `@font-face`、渲染精灵/动态 WebP/`g:<id>` 画布、同步 `WebAudioTracker`（1 路 BGM + 10 路 SE），并在点击 `CBGSETBMAPG` / `<img srcm>` 时精确回填 `RESULT:3/4/6`。

### 4. 热插拔社区语言包（`locales/*.json`）

- 内置默认语言为日语（`ja`），并随程序集内嵌简体中文（`zh-CN`）与英语（`en`），统一覆盖界面文案与编译器结构化诊断（`diag.ERA*`）。
- 将社区翻译文件放入运行时目录 `data/locales/<lang>.json`（例如 `data/locales/ko.json`），`HyperaLocalizer` 会通过 `FileSystemWatcher` 自动热加载并立即出现在导航栏与设置页语言列表中，无需重新编译；缺失键自动回退至 `ja`：

```json
{
  "_meta": {
    "code": "ko",
    "nativeName": "한국어",
    "englishName": "Korean"
  },
  "app.play": "플레이",
  "diag.ERA1426": "정의되지 않은 변수 또는 식별자 '{0}'입니다."
}
```
