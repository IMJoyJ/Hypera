[简体中文](README.md) | **日本語**

# Hypera —— .NET 10 向けモダン・クロスプラットフォーム Emuera コンパイラ＆ランタイムエンジン

**Hypera** は、`.NET 10` 上でゼロから設計された、クロスプラットフォームかつ完全インスタンス分離の Emuera（eramaker / EM+EE / Emuera.NET 派生スクリプト）静的コンパイラ、レジスタ仮想マシン（Register VM）、Skia 描画コンポジタおよびリソースパッケージングツールチェーンです。

デスクトップ実行クライアント（`Hypera.Standalone` / WinForms / WPF / MAUI）やターミナル（`hypera` CLI）のスタンドアロン実行エンジンとしてはもちろん、高並列マルチテナント Web ホスト（`Hypera.Server` / CloudEmuera）向けのノンブロッキング組込みランタイムとしても動作します。

> [!NOTE]
> **中立エンジンに関する注意**：本リポジトリおよび GitHub Releases で配布されるバイナリは、中立なコンパイラ・パッケージャ・各種実行クライアントおよび Web ホスト基盤のみを含みます。**特定のサードパーティ製ゲームスクリプトやゲームアセットは一切同梱・配布していません。** ご自身で適法に入手・管理されているゲームディレクトリ、`.hep` パッケージ、`.heb` バイトコード、または `.zip` アーカイブを読み込んでご利用ください。

---

## 主な特徴とアーキテクチャ

- **グローバル可変状態の完全排除（Zero `GlobalStatic`）**：変数テーブル、キャラクター配列、コンソールバッファ、乱数生成器（SFMT-19937）、グラフィックハンドル、カスタムフォントおよびオーディオ状態はすべて個別の `EmueraEngine` インスタンスに厳密に閉じ込められています。同一プロセス内で複数インスタンスを干渉なく同時実行でき、イミュータブルなコンパイル成果物 `BytecodeModule` をゼロコピーで共有可能です。
- **厳格なマルチパス静的コンパイラ（4-Pass Compiler）**：Emuera / EM+EE / Emuera.NET の **304 命令**、**270 式中関数**、**171 組み込み変数**を網羅。Shift-JIS / UTF-8 / GB18030 の混在自動文字コード判定、`*#*` 優先ディレクトリ、`#DIM`/`#DIMS`（`CONST`/`SAVEDATA`/`CHARADATA`/`GLOBAL`/`DYNAMIC`/`REF`）、`_Rename.csv` / `_Replace.csv` / `*.erd` / `*.als` / `VarExt*.csv`、さらに `;!;`、`CompatiCALLNAME`、`CompatiRAND`、`SIF` 空行などの eramaker 互換スイッチをサポートします。
- **高速フラット 16 バイト・レジスタ仮想マシン**：
  - 全命令を固定 16 バイトの blittable な `Instruction` 構造体として配置し、`long[]` / `string[]` の型分離レジスタファイルによってボクシングゼロで実行します。
  - `ForInit`/`ForStep`、`RepeatInit`/`RepeatStep` などのループスーパー命令と `Span<T>` / `Vector<long>` SIMD 配列演算を内蔵し、**100 万回のタイトループを Release ビルドで約 ~25 ms** で処理します。
  - **スタックレス・コルーチン中断**：`INPUT` / `ONEINPUT` / `TINPUT` / `BINPUT` / `INPUTMOUSEKEY` / `WAIT` / `AWAIT` 実行時に OS スレッドを一切ブロックせず、即座に `EngineStepStatus.WaitingInput` または `Yielded` を返却。世代 ID（`GenerationId`）による検証で遅延・期限切れ入力を安全に破棄します。
  - **遅延メモリ確保（Lazy Materialization）**：変数ストア、キャラクター配列、シンボルテーブル、スプライトデコードを必要時まで遅延確保し、有界 LRU キャッシュと組み合わせて大規模スクリプトのメモリ使用量と起動時間を大幅に削減します。
- **論理ビューポート基準の Skia + HarfBuzz 合成レンダリング**：
  - インスタンス単位の VFS から `font/*.ttf` を読み込み、ドット絵タイルフォント向けに `SKFontEdging.Alias`、整数ピクセル整列、`LineHeight == FontSize` の隙間ゼロ描画を行います。
  - `SKCodec` による静止画・アニメーション **WebP** / PNG / BMP / GIF デコードに対応。`GCREATE` / `GDRAWG`（5×5 ColorMatrix）/ `GDRAWGWITHMASK` / `GROTATE` / `HTML_PRINT`（任意の `<div depth>`・`HTML_PRINT_ISLAND` 対応）/ `CBGSETBMAPG` および `<img srcm>` の色ピック当たり判定（`RESULT:3/4/6`）を完全実装しています。
- **読み取り専用 VFS と互換セーブデータ永続化**：
  - `IVirtualFileSystem` により、展開済みディレクトリ（`PhysicalDirectoryVfs`）、単一ファイル索引アーカイブ `.hep`（`HepArchiveVfs` / `HepPackageWriter`、Deflate 圧縮・CRC32 検証対応）、`.zip`（`ZipArchiveVfs`）を統一的にマウント可能。さらに外部ディレクトリによる実行時 Overlay 上書きに対応します。
  - 従来のテキスト `.sav`、バイナリ `.sav`（Ebdb RLE 圧縮）、GZip 圧縮バイナリ `.sav` に対応し、キャラクターデータ、`#DIM SAVEDATA`/`CHARADATA`、`DataTable` / `XML` / `MAP` の VarExt コンテナを分離された書き込み用ストレージに保存します。
- **旧 `CALLSHARP` プラグインのバイナリ互換**：`Hypera.LegacyPluginShim`（出力 `Emuera.dll`）はアンロード可能な `AssemblyLoadContext` と `AsyncLocal` エンジンスコープを用い、既存 C# プラグインをマルチインスタンス環境下でも混線ゼロで動作させます。

---

## ビルド済みバイナリ（GitHub Releases）

[Releases](../../releases) ページにて、.NET 10 ランタイムの事前インストール不要な自己完結型（Self-Contained）の中立バイナリと `SHA256SUMS.txt` を配布しています：

| 配布アーカイブ | 対応プラットフォーム | 内容・用途 |
| --- | --- | --- |
| `hypera-standalone-<ver>-win-x64.zip` | Windows x64 | スタンドアロンデスクトップ実行クライアント `Hypera.Standalone.exe`。展開後すぐに `.hep` / `.heb` / `.zip` やゲームフォルダを起動可能。`hypera bundle --host` のホストテンプレートとしても利用可能 |
| `hypera-cli-<ver>-<rid>.zip` / `.tar.gz` | Windows (`x64`/`arm64`)、Linux (`x64`/`arm64`)、macOS (`arm64`/`x64`) | `hypera` CLI コンパイラ、`.hep` パッケージャ、単一実行ファイル `HYPRBNDL` バンドラ、24-bit TrueColor ANSI コンソール実行器 |
| `hypera-server-<ver>-<rid>.zip` / `.tar.gz` | Windows (`x64`)、Linux (`x64`/`arm64`)、macOS (`arm64`) | `Hypera.Server` マルチユーザー Web ホストサーバー（既定待受 `0.0.0.0:5080`、SQLite 永続化） |
| `hypera-maui-<ver>-win-x64.zip` | Windows x64 | `Hypera.Maui` ローカルデスクトップクライアント（Unpackaged 形式、ログイン不要のローカルライブラリ管理＆プレイ） |
| `hypera-maui-<ver>-android.apk` | Android 7.0+ (API 24+) | `Hypera.Maui` Android ローカルクライアント APK |

---

## ソリューションとプロジェクト構成（`Hypera.slnx`）

| プロジェクト | ターゲットフレームワーク / 出力 | 役割 |
| --- | --- | --- |
| [`src/Hypera.Core`](src/Hypera.Core) | `net10.0` (`Hypera.Core.dll`) | コンパイラ、多言語診断・`HyperaLocalizer`、16B レジスタ VM、CSV/Config/Save 永続化、VFS（ディレクトリ / `.hep` / `.zip` / Overlay）、Skia 描画、`HyperaGameHost` |
| [`src/Hypera.Audio`](src/Hypera.Audio) | `net10.0` (`Hypera.Audio.dll`) | クロスプラットフォーム 10ch 効果音（SE `0..9`）＋ 1ch ループ BGM ソフトウェアミキサー `CrossPlatformAudioMixer` |
| [`src/Hypera.LegacyPluginShim`](src/Hypera.LegacyPluginShim) | `net10.0` (`Emuera.dll`) | `MinorShift.Emuera.Runtime.Utils.PluginSystem` 互換の `CALLSHARP` プラグインシム（`ISharpPluginHost` 実装） |
| [`src/Hypera.Cli`](src/Hypera.Cli) | `net10.0` (`hypera`) | CLI ツール：24-bit TrueColor ANSI 対話実行、ヘッドレス PNG フレーム出力、`.heb` コンパイル、`.hep` パック、単一実行ファイル `HYPRBNDL` 生成 |
| [`src/Hypera.Standalone`](src/Hypera.Standalone) | `net10.0-windows10.0.19041.0` (`Hypera.Standalone.exe`) | Windows 独立デスクトップ実行クライアント：ダブルクリック自動検出、ドラッグ＆ドロップ起動、外部リソース／設定の Overlay 上書き、`config.json` ウィンドウ状態保存、リアルタイム Debug コンソール |
| [`src/Hypera.Views.WinForms`](src/Hypera.Views.WinForms) | `net10.0-windows10.0.19041.0` | 高 DPI 対応 WinForms ホストコントロール `HyperaSkiaControl` |
| [`src/Hypera.Views.Wpf`](src/Hypera.Views.Wpf) | `net10.0-windows10.0.19041.0` | 高 DPI 対応 WPF ホストコントロール `HyperaSkiaElement` |
| [`src/Hypera.Platform`](src/Hypera.Platform) | `net10.0` (`Hypera.Platform.dll`) | アプリカタログ、バージョン系列とアルゴリズム β 自動バージョン選択、並列 `.zip`→`.hep` コンパイル審査パイプライン、バイナリハッシュ評価、隔離セーブ管理、共有バイトコードセッション管理（EF Core SQLite） |
| [`src/Hypera.Web.UI`](src/Hypera.Web.UI) | `net10.0` (Razor Class Library) | 共通 MudBlazor UI ライブラリ（Slate & Teal テーマ、即時言語切替、論理ビューポート固定 `PlayStage`、WebAudio 1+10ch 同期、セーブデータ管理） |
| [`src/Hypera.Server`](src/Hypera.Server) | `net10.0` (ASP.NET Core Blazor Server) | マルチユーザー＆コミュニティ向けホストサーバー（既定 `http://0.0.0.0:5080`、Cookie 認証、審査キュー、ソース／セーブのストリーミングダウンロード） |
| [`src/Hypera.Maui`](src/Hypera.Maui) | `net10.0-windows10.0.19041.0` / `net10.0-android` | `.NET MAUI Blazor Hybrid` クロスプラットフォーム単体クライアント（`Hypera.Web.UI` と `Hypera.Platform` を共有、ログイン不要のローカル管理者モード） |
| [`tests/Hypera.Tests`](tests/Hypera.Tests) | `net10.0` (xUnit) | コンパイラ、多言語診断、VM ランタイム、Skia/WebP/ドットフォント描画、VFS/セーブ、オーディオ、プラグイン分離および E2E 統合テスト |
| [`tests/Hypera.Platform.Tests`](tests/Hypera.Platform.Tests) | `net10.0` (xUnit) | プラットフォーム DB、認証・認可、並列ビルド審査、ハッシュ評価、バージョン解決、セッションおよび HTTP 統合テスト |

内部バイナリ形式（`.heb` / `.hep` レイアウト、命令エンコーディング、Z-order 描画パイプライン）の詳細は [`docs/architecture.md`](docs/architecture.md) を参照してください。

---

## ビルドとテスト

**.NET 10 SDK** が必要です（`Hypera.Maui` をビルドする場合は対象プラットフォームの MAUI ワークロードも必要です）：

```powershell
# コアおよびプラットフォームのテストスイートをビルド＆実行（MAUI ワークロード不要）
dotnet test tests/Hypera.Tests/Hypera.Tests.csproj -c Release
dotnet test tests/Hypera.Platform.Tests/Hypera.Platform.Tests.csproj -c Release

# MAUI ワークロード導入済み環境でソリューション全体をビルド
dotnet build Hypera.slnx -c Release
```

---

## `hypera` CLI コマンドリファレンス

`hypera` は `Hypera.Core` の正規コンパイラおよびバイナリコーデックをそのまま利用するため、出力される `.heb` / `.hep` はランタイムと 100% バイナリ互換です：

```powershell
# バージョン確認
hypera --version

# 1. ゲームフォルダ、.heb バイトコード、.hep パッケージ、または .zip を直接実行
hypera run <path> [--headless-frames <png_dir>] [--input <val>]

# 2. ERH/ERB スクリプトを .heb バイトコード（マジック "HEBC"）へ静的コンパイル
hypera compile <game_dir> -o game.heb

# 3. アセット・設定・フォントとビルド済み .heb を単一 .hep（マジック "HEPK"）にパック
#    --compiled-only で元 ERB/ERH ソースを除外してコンパイル済み配布物のみを収録可能、--exclude で任意パスを除外
hypera pack <game_dir> -o game.hep [--no-compress] [--compiled-only] [--exclude <path>]

# 4. 24 バイトの HYPRBNDL トレイラを付与し、起動時に自動マウントされる単一実行ファイルを生成
hypera bundle game.hep --host path/to/Hypera.Standalone.exe -o Game.exe
```

> ソースコードから直接実行する場合は、`hypera` を `dotnet run --project src/Hypera.Cli -c Release --` に置き換えてください。

---

## `Hypera.Standalone` デスクトップ実行クライアント（Windows）

`Hypera.Standalone` は、従来の Emuera と同じゼロ設定ダブルクリック起動の操作性を維持しつつ、コンパイル済みバイナリ形式を優先実行する Windows デスクトップクライアントです：

1. **ターゲット自動検出の優先順位**：
   - 実行ファイル末尾に `HYPRBNDL` トレイラが埋め込まれている場合、内蔵 `.hep` / `.heb` を最優先でマウント。
   - コマンドライン引数、またはエクスプローラーからのドラッグ＆ドロップで `.hep` / `.heb` / `.zip` / フォルダを指定して起動。
   - 引数なしで起動した場合、同フォルダ内を `<実行ファイル名>.hep` → `game.hep` → 単一の `*.hep` → 同順の `.heb` → 従来構成（`ERB/` または `CSV/`）の順に自動探索。
2. **外部 Overlay 上書き**：`.hep` や `.zip` を展開することなく、同フォルダにカスタム `emuera.config` や `resources/` 配下の差分ファイルを置くだけで自動的に上書き適用されます（`--no-overlay` で無効化可能）。
3. **設定・セーブデータ**：ウィンドウの最大化状態や位置・サイズは `config.json` に自動保存され、セーブデータは既定で同フォルダの `sav/` に書き出されます（`--save-dir <dir>` で変更可能）。
4. **主なショートカットキー**：`F11` フルスクリーン切替、`F12` リアルタイム Debug コンソール、`Ctrl+O` パッケージを開く、`Ctrl+R` 再起動、`Ctrl+S` ログ出力、`Ctrl+M` ミュート、右クリック長押しで早送りスキップ。
5. **ヘッドレス検証（CI・回帰テスト用）**：
   ```powershell
   Hypera.Standalone.exe <target> --headless-smoke output.png [--input 0] [--debug]
   ```

---

## ホスト統合ガイド

### 1. 1 行で起動（`HyperaGameHost` —— ディレクトリ / `.hep` / `.zip` / `.heb` を自動判別）

```csharp
using Hypera.Audio;
using Hypera.Runtime;
using MinorShift.Emuera.Runtime.Utils.PluginSystem;

using var host = HyperaGameHost.Load(
    @"C:\Games\MyEraGame.hep", // フォルダ / .zip / .heb も指定可能
    new HyperaGameLoadOptions
    {
        SaveDirectory = @"C:\Games\MyEraSaves",
        AudioBackend = new CrossPlatformAudioMixer(),
        AutoStartSystemFlow = true, // 自動で BEGIN TITLE を開始
    });

// 任意：旧 CALLSHARP プラグインブリッジを接続
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

### 2. コンソール / ヘッドレス描画ホスト

`Hypera.Cli` の `AnsiConsoleHost` または `hypera run <path> --headless-frames <dir>` を利用すると、各フレームを高精細 Skia PNG として出力しつつ、24-bit TrueColor ANSI ターミナル UI で実行できます。

### 3. WinForms デスクトップホスト（`Hypera.Views.WinForms`）

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

### 4. WPF デスクトップホスト（`Hypera.Views.Wpf`）

```csharp
var host = HyperaGameHost.Load(@"C:\Games\MyEraGame", new HyperaGameLoadOptions { AutoStartSystemFlow = true });
hyperaSkiaElement.Engine = host.Engine;
hyperaSkiaElement.StartLoop();
```

### 5. .NET MAUI クロスプラットフォームホスト

MAUI ページ上に `SkiaSharp.Views.Maui.Controls.SKCanvasView` を配置し、`ViewportTransform` でタッチ・ポインタの物理座標を `engine.Viewport` 論理座標へ変換します。`PaintSurface` で `SkiaFrameCompositor.RenderScene(e.Surface.Canvas, e.Info.Width, e.Info.Height, engine.ConsoleBuffer.CaptureScene(), engine.Graphics, engine.Sprites)` を呼び出し、`IDispatcherTimer` からノンブロッキングに `engine.Step()` を進めてください。

### 6. Web / CloudEmuera マルチテナントサーバーホスト（`IDisplaySnapshotSink`）

`IDisplaySnapshotSink` を実装し、`HyperaGameLoadOptions.DisplaySink` に渡します：

```csharp
public sealed class WebSessionSnapshotSink(Action<DisplaySnapshot> pushToClient) : IDisplaySnapshotSink
{
    public void OnDisplaySnapshot(DisplaySnapshot snapshot) => pushToClient(snapshot);
}
```

入力待ちやフレーム譲歩のたびに `OnDisplaySnapshot` が呼び出されるため、サーバー側でスレッドを占有することなく、構造化された `DisplaySnapshot`（またはオフスクリーン描画フレーム）を WebSocket / SignalR 経由でブラウザへ送信し、入力受信時に `engine.SubmitInput(...)` で実行を再開できます。

---

## プラットフォームホスト・Web UI・コミュニティ言語パック

### 1. `Hypera.Server`（マルチユーザー Web ホストサーバー、既定 `0.0.0.0:5080`）

```powershell
# マルチユーザー Web サーバーを起動（既定 http://0.0.0.0:5080、データ格納先 ./data）
dotnet run --project src/Hypera.Server -c Release
```

- **ホストモード**：`PlatformHostMode.MultiUserServer`。SQLite WAL（`data/hypera.db`）および HTTP-only Cookie 認証（`/api/auth/register`、`/api/auth/login`、`/api/auth/logout`）を使用します。
- **初回ユーザー自動管理者化**：空データベースで最初に登録されたユーザーは自動的に `Admin` 権限が付与されます。一般ユーザーがアップロードした `.zip` は並列コンパイル後 `PendingReview` 審査キューへ入り、管理者が承認すると公開プールに反映されます。内包バイナリ（`.dll`/`.exe` 等）の `(SHA-256, ByteSize)` 信頼ステータス管理も備えています。
- **ストリーミングダウンロード**：`/api/downloads/source/{submissionId}` は `AllowSourceDownload` 権限を厳密に検証し、`/api/downloads/saves/{appId}/zip` は当該アプリのユーザーセーブ一式を ZIP でエクスポートします。

### 2. `Hypera.Maui`（Windows / Android ローカルクライアント）

```powershell
# Windows デスクトップ版 MAUI Blazor Hybrid クライアントを起動
dotnet run --project src/Hypera.Maui -f net10.0-windows10.0.19041.0 -c Release
```

- `Hypera.Server` と 100% 同一の `Hypera.Web.UI` および `Hypera.Platform` を共有し、`PlatformHostMode.SingleUserLocalAdmin`（ログイン不要のローカル管理者モード）で動作します。アップロードした `.zip` は並列コンパイル完了後ただちにプレイ可能になります。

### 3. `Hypera.Platform` と `Hypera.Web.UI`

- **`Hypera.Platform`**：`IAuthService`、`IAppCatalogService`（アトミックな `ForkAppWithPackageAsync` およびバージョン順再編成対応）、`IPackagePipelineService`（起動時の並列コンパイルキュー開始と中断タスク復旧）、`IBinaryReputationService`、`IVersionResolutionService`（アルゴリズム β `KnownLanguagePriority`・`VersionPriority`・`AlwaysLastRun` 対応）、`IUserSaveService`（`(UserId, AppId)` 単位でのバージョン間セーブ共有・容量制限）、`IRuntimeSessionManager`（イミュータブル `BytecodeModule` 共有、単一セッション排他、無限ループ防止リミッタ）を提供します。
- **`Hypera.Web.UI`**：MudBlazor ベースの Slate & Teal テーマ UI ライブラリ。`PlayStage.razor` は `GetSessionRenderStateAsync` / `ReadSessionAssetAsync` / `HitTestAndClickAsync` に直結し、論理ビューポート固定ステージ上で `font/*.ttf` の `@font-face` 登録、スプライト・アニメーション WebP・`g:<id>` キャンバス描画、`WebAudioTracker`（BGM 1ch＋SE 10ch）の同期、`CBGSETBMAPG` / `<img srcm>` クリック時の `RESULT:3/4/6` 反映を行います。

### 4. ホットリロード対応コミュニティ言語パック（`locales/*.json`）

- 既定のフォールバック言語は日本語（`ja`）で、簡体字中国語（`zh-CN`）と英語（`en`）もアセンブリに内蔵されており、UI 文言とコンパイラ構造化診断（`diag.ERA*`）の両方を統一カバーします。
- 実行時の `data/locales/<lang>.json`（例：`data/locales/ko.json`）に翻訳 JSON を配置すると、`HyperaLocalizer` が `FileSystemWatcher` で自動検知してホットリロードし、再コンパイル不要でナビゲーションバーや設定画面の言語一覧へ即時反映されます（未定義キーは自動的に `ja` へフォールバック）：

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

---

## ライセンス（License）

`Copyright (c) 2026 JoyJ. All rights reserved.`

本プロジェクトのライセンスは現在 **All Rights Reserved**（全著作権所有）であり、現時点ではオープンソースとしての複製・改変・再配布の許諾は付与されていません。詳細は [`LICENSE`](LICENSE) をご確認ください。
