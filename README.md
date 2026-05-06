# UCL_Core

[繁體中文](#繁體中文) | [English](#english) | [日本語](#日本語)

---

## 繁體中文

**UCL_Core** 是 [UCL Framework](https://github.com/tim099/UCL) 的核心模組 — 提供 Unity Editor 端的資產系統、模組服務、AI 協作工具與一系列可重用的編輯器 UI 元件。本模組可單獨使用，也作為 UCL 各上層模組（UCL_Game / UCL_Audio / UCL_Build 等）的共通基底。

### ✨ 主要功能

| 系統 | 說明 |
|---|---|
| 🤖 **Agent Command 系統** | AI agent 與 Unity Editor 的跨 process 指令系統 — agent 寫 `queue.json` + `pending.trigger`（lock-file），Editor 端 `UCL_AgentCommandWatcher`（`[InitializeOnLoad]`）1Hz 自動偵測接手；含自動發現 / 反射註冊 / async 執行流程；支援 5 種觸發方式（**Python CLI + lock-file 自動觸發** ⭐ / Editor UI / Menu / 手寫 queue.json / batchmode）|
| 🐍 **Tools~/AgentCommands/** | Python wrapper（`Tools~/` 字尾讓 Unity 略過 import）— `run_cmd.py` 提供 `submit/wait/run/list/catalog` 子命令，含 `ensure_idle()` pre-flight 串行化保證 |
| 🧱 **UCL_Asset 資產系統** | `UCL_Asset<T>` 通用 JSON 序列化資產容器，含 `UCLI_AssetEntry<T>` 跨資產引用 + 模組載入順序 + cache 管理 |
| 🗂 **UCL_ModuleService** | 模組系統 — 多模組（Core + 子模組）並存、跨模組 ID lookup、Persistent / Built-in 路徑切換 |
| 🖥 **Editor IMGUI Pages** | `UCL_CommonEditorPage` / `UCL_AgentCommandsPage` / `UCL_SelectAssetPage` 等可繼承的編輯器頁面 |
| 📚 **多語系 HelpURL** | `ucl_core:` / `eov_docs:` prefix 機制，編輯器內 ? 按鈕跳轉到對應語系 markdown 文件 |

### 📁 文件索引

完整多語系文件位於 [`Docs~/`](Docs~/)：

- 🇹🇼 [繁體中文](Docs~/zh-Hant/index.md)
- 🇨🇳 [简体中文](Docs~/zh-Hans/index.md)
- 🇯🇵 [日本語](Docs~/ja/index.md)
- 🇬🇧 [English](Docs~/en/index.md)

### 📝 更新紀錄

[`DevLogs~/`](DevLogs~/) — 給插件使用者的更新內容說明（一筆一檔，檔名 `NNNNN_YYYY-MM-DD_slug.md`）。最新：
- [00008_2026-05-07](DevLogs~/00008_2026-05-07_welcome-compile-tracker.md)：UCL_WelcomePage（跨專案歡迎頁，i18n + 語言切換列）+ UCL_CompileErrorTracker + check_compile.py（雞生蛋編譯錯誤排查工具）
- [00007_2026-05-07](DevLogs~/00007_2026-05-07_docs-catalog-fuzzy-search.md)：文件目錄索引 + 模糊搜尋系統 — Cmd_ExportDocsCatalog / Cmd_SearchDocs / UCL_DocSearchPage（aliases 同義詞 + 語系偏好排序）
- [00006_2026-05-07](DevLogs~/00006_2026-05-07_agent-cmd-ux.md)：Agent Command UX — Fill Example 按鈕 + Failed 自動清理 + Clear Failed 按鈕
- [00005_2026-05-06](DevLogs~/00005_2026-05-06_serializeref-symmetry-fix.md)：SerializeReference 對稱性修復（GUI ↔ JSON）
- [00004_2026-05-06](DevLogs~/00004_2026-05-06_find-asset-usages-reflect.md)：Cmd_FindAssetUsages 反向引用查詢 + 反射診斷
- [00003_2026-05-05](DevLogs~/00003_2026-05-05_claude-hook-asset-validate.md)：Claude Code Hook 自動化 — UCL_Asset 驗證從 SOP 升級為強制門檻
- [00002_2026-05-05](DevLogs~/00002_2026-05-05_validate-asset-format.md)：新增 `Cmd_ValidateAssetFormat` — UCL_Asset schema + 引用完整性檢查
- [00001_2026-05-05](DevLogs~/00001_2026-05-05_agent-command-lockfile-trigger.md)：Agent Command 系統新增 Lock-file 自動觸發機制

⭐ **重點推薦**：
- [`UCL_AgentCommand_Architecture`](Docs~/zh-Hant/API/UCL_AgentCommand/UCL_AgentCommand_Architecture.md) — Agent Command 系統的整體架構文件（元件圖 / 生命週期 / 觸發方式對照 / 擴充點）
- 🔍 [`Validate_UCL_Asset_Workflow`](Docs~/zh-Hant/Workflows/Validate_UCL_Asset_Workflow.md) — 任何寫完 / 改完 UCL_Asset JSON 後的驗收 SOP（搭配 [`Cmd_ValidateAssetFormat`](Docs~/zh-Hant/API/UCL_AgentCommand/Cmd_ValidateAssetFormat.md)）
- 🔗 [`Hook_Setup_Workflow`](Docs~/zh-Hant/Workflows/Hook_Setup_Workflow.md) — 用 Claude Code hooks 把上面的 SOP **自動化** — `PostToolUse` 早期警告 + `Stop` 強制驗收門檻；給每個使用 UCL_Core 的上層專案複製 settings.json 範本

### 🖥 如何開啟 UCL_EditorMenu 與 Agent Commands

1. 在 Unity 編輯器的上方選單列，點選 **UCL** -> **Menu** 即可開啟 **UCL_EditorMenu**。
2. 在開啟的選單介面中，可以點選 **Agent Commands** 按鈕來開啟 Agent Commands 主管理面板。
3. **UCL_CodeLocalize 翻譯按鈕文字速查**：
   - 編輯模組按鈕文字：`Edit Modules`
   - Agent 指令集按鈕文字：`Agent Commands`

### 🚀 快速使用範例

新增一條 Agent Command（給 AI agent 用）：

```csharp
public class Cmd_MyCustom : UCL_AgentCommandHandlerBase
{
    public override string CommandType => "MyCustom";
    public override string ShortDescription => "我的自訂指令";
    public override string ArgsSchema => "key1=描述\nkey2=描述";

    public override async UniTask ExecuteAsync(
        Dictionary<string,string> args, CancellationToken token)
    {
        // ... 你的邏輯
        await UniTask.CompletedTask;
    }
}
```

寫好後 reflection 會自動註冊。Agent 在 `AgentCommands/queue.json` 加一筆即可觸發：

```json
{ "Type": "MyCustom", "Mode": "OneShot", "Args": { "key1": "value1" } }
```

### 📦 安裝

UCL_Core 是 [UCL Framework](https://github.com/tim099/UCL) 的子模組，**透過 UCL 一併安裝**。

```bash
git clone --recursive https://github.com/tim099/UCL.git
```

如已有 UCL，更新所有子模組：

```bash
git submodule update --init --recursive
```

### 📜 授權

MIT License — 見 [`COPYING.txt`](COPYING.txt)。

---

## English

**UCL_Core** is the core module of the [UCL Framework](https://github.com/tim099/UCL) — it provides Unity Editor-side asset systems, module services, AI collaboration tools, and a suite of reusable Editor UI components. This module can be used standalone, and also serves as the common foundation for upper-layer UCL modules (UCL_Game / UCL_Audio / UCL_Build, etc.).

### ✨ Main Features

| System | Description |
|---|---|
| 🤖 **Agent Command System** | Cross-process command system between AI agents and the Unity Editor — agents write to `queue.json`, the Editor executes and writes results back; includes auto-discovery / reflection registry / async execution; supports 4 trigger paths (Editor UI / Menu / Python CLI / batchmode) |
| 🧱 **UCL_Asset System** | Generic JSON-serialized asset container `UCL_Asset<T>` with cross-asset references via `UCLI_AssetEntry<T>` + module load order + cache management |
| 🗂 **UCL_ModuleService** | Module system — multi-module support (Core + sub-modules), cross-module ID lookup, Persistent / Built-in path switching |
| 🖥 **Editor IMGUI Pages** | Inheritable Editor pages like `UCL_CommonEditorPage` / `UCL_AgentCommandsPage` / `UCL_SelectAssetPage` |
| 📚 **Multi-language HelpURL** | `ucl_core:` / `eov_docs:` prefix mechanism — the in-Editor ? button jumps to the matching language Markdown doc |

### 📁 Documentation Index

Full multi-language documentation lives under [`Docs~/`](Docs~/):

- 🇹🇼 [繁體中文](Docs~/zh-Hant/index.md)
- 🇨🇳 [简体中文](Docs~/zh-Hans/index.md)
- 🇯🇵 [日本語](Docs~/ja/index.md)
- 🇬🇧 [English](Docs~/en/index.md)

⭐ **Recommended starting point**: [`UCL_AgentCommand_Architecture`](Docs~/en/API/UCL_AgentCommand/UCL_AgentCommand_Architecture.md) — the architectural overview of the Agent Command system (component diagram / lifecycle / trigger comparison / extension points).

### 🖥 How to open UCL_EditorMenu and Agent Commands

1. On the top Unity menu bar, click on **UCL** -> **Menu** to open **UCL_EditorMenu**.
2. From the menu interface, you can click the **Agent Commands** button to open the Agent Commands management panel.
3. **UCL_CodeLocalize quick reference for translated button text**:
   - Edit Modules button text: `Edit Modules`
   - Agent Commands button text: `Agent Commands`

### 🚀 Quick Start

Add a new Agent Command (for AI agents to trigger):

```csharp
public class Cmd_MyCustom : UCL_AgentCommandHandlerBase
{
    public override string CommandType => "MyCustom";
    public override string ShortDescription => "My custom command";
    public override string ArgsSchema => "key1=description\nkey2=description";

    public override async UniTask ExecuteAsync(
        Dictionary<string,string> args, CancellationToken token)
    {
        // ... your logic
        await UniTask.CompletedTask;
    }
}
```

Reflection auto-registers it. An agent triggers it by adding an entry to `AgentCommands/queue.json`:

```json
{ "Type": "MyCustom", "Mode": "OneShot", "Args": { "key1": "value1" } }
```

### 📦 Installation

UCL_Core is a submodule of the [UCL Framework](https://github.com/tim099/UCL); **install it together with UCL**.

```bash
git clone --recursive https://github.com/tim099/UCL.git
```

If you already have UCL, update all submodules:

```bash
git submodule update --init --recursive
```

### 📜 License

MIT License — see [`COPYING.txt`](COPYING.txt).

---

## 日本語

**UCL_Core** は [UCL Framework](https://github.com/tim099/UCL) のコアモジュールです — Unity Editor 側のアセットシステム、モジュールサービス、AI 協調ツール、そして再利用可能な Editor UI コンポーネント群を提供します。本モジュールは単独で使用することも、UCL の上位モジュール (UCL_Game / UCL_Audio / UCL_Build など) の共通基盤として使うこともできます。

### ✨ 主要機能

| システム | 説明 |
|---|---|
| 🤖 **Agent Command システム** | AI agent と Unity Editor のクロスプロセス指令システム — agent が `queue.json` + `pending.trigger` (lock-file) を書き、Editor 側 `UCL_AgentCommandWatcher` (`[InitializeOnLoad]`) が 1Hz で自動検出して引き継ぎ。自動発見 / リフレクション登録 / 非同期実行フロー含む。5 種類のトリガー方法をサポート (**Python CLI + lock-file 自動トリガー** ⭐ / Editor UI / メニュー / 手書き queue.json / batchmode) |
| 🐍 **Tools~/AgentCommands/** | Python wrapper (`Tools~/` サフィックスにより Unity がインポートをスキップ) — `run_cmd.py` が `submit/wait/run/list/catalog` サブコマンドを提供。`ensure_idle()` pre-flight による直列化保証付き |
| 🧱 **UCL_Asset アセットシステム** | `UCL_Asset<T>` 汎用 JSON シリアライズアセットコンテナ。`UCLI_AssetEntry<T>` クロスアセット参照 + モジュール読み込み順 + キャッシュ管理を含む |
| 🗂 **UCL_ModuleService** | モジュールシステム — 複数モジュール (Core + サブモジュール) 共存、クロスモジュール ID ルックアップ、Persistent / Built-in パス切替 |
| 🖥 **Editor IMGUI Pages** | `UCL_CommonEditorPage` / `UCL_AgentCommandsPage` / `UCL_SelectAssetPage` など継承可能な Editor ページ |
| 📚 **多言語 HelpURL** | `ucl_core:` / `eov_docs:` プレフィックス機構。Editor 内の ? ボタンが現在の言語に対応する markdown ドキュメントへジャンプ |

### 📁 ドキュメント索引

完全な多言語ドキュメントは [`Docs~/`](Docs~/) 配下:

- 🇹🇼 [繁體中文](Docs~/zh-Hant/index.md)
- 🇨🇳 [简体中文](Docs~/zh-Hans/index.md)
- 🇯🇵 [日本語](Docs~/ja/index.md)
- 🇬🇧 [English](Docs~/en/index.md)

### 📝 更新ログ

[`DevLogs~/`](DevLogs~/) — プラグイン利用者向けの更新内容説明 (1 件 1 ファイル、ファイル名 `NNNNN_YYYY-MM-DD_slug.md`)。最新:
- [00008_2026-05-07](DevLogs~/00008_2026-05-07_welcome-compile-tracker.md): UCL_WelcomePage (プロジェクト横断ウェルカムページ、i18n + 言語切替バー) + UCL_CompileErrorTracker + check_compile.py (鶏卵問題のコンパイルエラー診断ツール)
- [00007_2026-05-07](DevLogs~/00007_2026-05-07_docs-catalog-fuzzy-search.md): ドキュメント索引 + ファジー検索システム — Cmd_ExportDocsCatalog / Cmd_SearchDocs / UCL_DocSearchPage (aliases 同義語 + 言語優先ランキング)
- [00006_2026-05-07](DevLogs~/00006_2026-05-07_agent-cmd-ux.md): Agent Command UX — Fill Example ボタン + Failed 自動クリア + Clear Failed ボタン
- [00005_2026-05-06](DevLogs~/00005_2026-05-06_serializeref-symmetry-fix.md): SerializeReference 対称性修正 (GUI ↔ JSON)
- [00004_2026-05-06](DevLogs~/00004_2026-05-06_find-asset-usages-reflect.md): Cmd_FindAssetUsages 逆引き参照クエリ + リフレクション診断
- [00003_2026-05-05](DevLogs~/00003_2026-05-05_claude-hook-asset-validate.md): Claude Code Hook 自動化 — UCL_Asset 検証を SOP から強制ゲートへ昇格
- [00002_2026-05-05](DevLogs~/00002_2026-05-05_validate-asset-format.md): `Cmd_ValidateAssetFormat` 追加 — UCL_Asset スキーマ + 参照整合性チェック
- [00001_2026-05-05](DevLogs~/00001_2026-05-05_agent-command-lockfile-trigger.md): Agent Command システムに Lock-file 自動トリガー機構を追加

⭐ **おすすめ**:
- [`UCL_AgentCommand_Architecture`](Docs~/ja/API/UCL_AgentCommand/UCL_AgentCommand_Architecture.md) — Agent Command システム全体アーキテクチャ (コンポーネント図 / ライフサイクル / トリガー方法対比 / 拡張ポイント)
- 🔍 [`Validate_UCL_Asset_Workflow`](Docs~/ja/Workflows/Validate_UCL_Asset_Workflow.md) — UCL_Asset JSON を書いた / 修正した後の検収 SOP ([`Cmd_ValidateAssetFormat`](Docs~/ja/API/UCL_AgentCommand/Cmd_ValidateAssetFormat.md) と組み合わせ)
- 🔗 [`Hook_Setup_Workflow`](Docs~/ja/Workflows/Hook_Setup_Workflow.md) — Claude Code hooks で上記 SOP を**自動化** — `PostToolUse` 早期警告 + `Stop` 強制検収ゲート。UCL_Core を使う各上位プロジェクトに settings.json テンプレートをコピー

### 🖥 UCL_EditorMenu と Agent Commands を開く方法

1. Unity Editor 上部メニューバーで **UCL** -> **Menu** をクリック → **UCL_EditorMenu** が開きます。
2. 開いたメニュー画面から **Agent Commands** ボタンをクリックして Agent Commands 管理パネルを開きます。
3. **UCL_CodeLocalize 翻訳されたボタンテキストの早見表**:
   - モジュール編集ボタンテキスト: `Edit Modules`
   - Agent コマンド集ボタンテキスト: `Agent Commands`

### 🚀 クイックスタート

新しい Agent Command を追加 (AI agent から呼び出される):

```csharp
public class Cmd_MyCustom : UCL_AgentCommandHandlerBase
{
    public override string CommandType => "MyCustom";
    public override string ShortDescription => "私のカスタムコマンド";
    public override string ArgsSchema => "key1=説明\nkey2=説明";

    public override async UniTask ExecuteAsync(
        Dictionary<string,string> args, CancellationToken token)
    {
        // ... ロジックをここに
        await UniTask.CompletedTask;
    }
}
```

書いたら reflection が自動登録します。agent は `AgentCommands/queue.json` に 1 件追加するだけでトリガー可能:

```json
{ "Type": "MyCustom", "Mode": "OneShot", "Args": { "key1": "value1" } }
```

### 📦 インストール

UCL_Core は [UCL Framework](https://github.com/tim099/UCL) のサブモジュールです — **UCL と一緒にインストール**してください。

```bash
git clone --recursive https://github.com/tim099/UCL.git
```

すでに UCL を持っている場合、すべてのサブモジュールを更新:

```bash
git submodule update --init --recursive
```

### 📜 ライセンス

MIT License — [`COPYING.txt`](COPYING.txt) を参照。
