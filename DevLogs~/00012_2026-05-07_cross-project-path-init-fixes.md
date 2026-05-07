---
date: 2026-05-07
index: 00012
title: 跨專案 path / pref / init race 修法 — DocSearch / Welcome / Localize 對 flat-layout 友善
tags: [fix, infra, cross-project]
---

# 跨專案 Portability — 五個 path / pref / init race 修法

## What

修五個對「git-root 即 Unity project root」這種 flat layout 失準的點：

1. **`UCL_LocalizeInitializeOnLoad` 加 `delayCall`** — 早期 `[InitializeOnLoadMethod]` 直接 `await UCL_ModuleService.WaitUntilInitialized` 會跟 UniTask `PlayerLoopHelper.Init()` 競爭，`runners` 陣列尚未就緒時 NRE。改成排到下一輪 editor tick。
2. **`UCL_WelcomePage` PrefKey 改 per-project namespace** — `const string` → cached static getter，key 自帶 `Application.dataPath.GetHashCode()` 後綴。EditorPrefs 全機共用導致「在 A 專案看過 Welcome → B 專案不再彈」的污染解決。
3. **`UCL_WelcomeAutoOpen` 加 `UCL_MenuWindow.ShowWindow()`** — `OpenAndShow()` 內 `ExecuteMenuItem("UCL/Menu")` 在 `[InitializeOnLoad]` + `delayCall` 早期時序可能不在當輪 frame 把 window 帶進 OnGUI cycle，`s_OnFirstDraw` hook 沒人消費。同 asmdef 直接 reference `UCL_MenuWindow` 比走 ExecuteMenuItem 可靠。
4. **`UCL_DocCatalogScanner.GetGitRoot()` 改 walk** — 原本 `Application.dataPath/../..` 假設結構是 `<gitRoot>/<UnityProject>/Assets`（如 CardGame layout），對 `<gitRoot>/Assets` 的扁平結構（如 TEVI）落點偏 1 層。改成 walk 找含 `.git` directory 的 ancestor（跳過 submodule 的 `.git` file redirect）。
5. **`UCL_DocSearchPage.DoSearch` 改用 `UCL_DocsModuleRegistry.All`** — 原寫死 `"CardGame/Assets/UCL/UCL_Core/Docs~"` 對 TEVI 的 `Assets/UCL_Core/Docs~/` 找不到。改用 HelpURL 同一份 docs 模組註冊表，每個 module 的 absolute root = `ResolveBaseProvider() + DocsSubfolder`。新註冊的下游 docs 模組自動納入搜尋。
6. **`UCL_DocSearchPage` Preview 按鈕修空白問題** — 按鈕原本只放 `"📄"` 字面，當 Editor 字型不含 supplementary plane emoji glyph（多數 Windows / 部分 Linux 字型）時整顆按鈕變空白。改走 `UCL_CodeLocalize.Get("DocSearch.Preview")`（4 國語系新增 `"📄 預覽" / "📄 预览" / "📄 Preview" / "📄 プレビュー"`），跟 `DocSearch.Reveal` / `Welcome.Search.OpenButton` 模式一致 — emoji 沒 render 出來時還有文字保底。

## Why

### Flat 結構 vs 巢狀結構

```
CardGame layout (UCL_Core 假設):     TEVI layout (新場景):
<gitRoot>/                           <gitRoot>/                ← 也是 Unity project root
  CardGame/                            Assets/
    Assets/                              UCL_Core/  (submodule)
      UCL/UCL_Core/                        Docs~/
```

`Application.dataPath/../..` 對 CardGame 得到 `<gitRoot>`（對），對 TEVI 得到 `<gitRoot>` 的**上一層**（錯）。

### EditorPrefs 是 per-machine 不是 per-project

Windows 上 `HKCU\Software\Unity Technologies\Unity Editor 5.x` 全 Unity 專案共用。看過 Welcome 一次 → 任何別的有 UCL_Core 的專案永不再彈，直到 `CurrentVersion` bump 或使用者手動清 pref。

### Init race

UniTask 的 `PlayerLoopHelper.Init()` 與 UCL_Core 的 `[InitializeOnLoadMethod]` 都用同樣機制，順序不保證。早期 await 點容易踩 `runners` 還沒填滿的 race。`delayCall` 排到 editor tick 後，所有 `[InitializeOnLoadMethod]` 已跑完，安全。

## How — 五個修點細節

### 1. Localize init NRE
```csharp
// Before
[InitializeOnLoadMethod]
public static void InitializeOnLoad() => EditorInitLocalize();
// After
[InitializeOnLoadMethod]
public static void InitializeOnLoad()
{
    UnityEditor.EditorApplication.delayCall += EditorInitLocalize;
}
```

### 2. Welcome PrefKey per-project

```csharp
// Before
public const string PrefKey_ShownVersion = "UCL_Core.Welcome.ShownVersion";

// After
static string s_PrefKey_ShownVersion;
static string s_ProjectFingerprint;
static string ProjectFingerprint =>
    s_ProjectFingerprint ??= Application.dataPath.GetHashCode().ToString("X");
public static string PrefKey_ShownVersion =>
    s_PrefKey_ShownVersion ??= $"UCL_Core.Welcome.ShownVersion@{ProjectFingerprint}";
```

`const` → property 對所有 caller 透明（都是 `EditorPrefs.GetX/SetX(PrefKey_*, ...)` 字串參數用法）。

### 3. WelcomeAutoOpen 強制開窗 + 加 log + ForceOpen

```csharp
public static void TryAutoOpen()  // private → public，方便 Cmd_Invoke 觸發
{
    Debug.Log($"[UCL_Welcome] TryAutoOpen begin. ...");
    if (disabled) { Debug.Log("Gate 1 skip"); return; }
    if (shown == current) { Debug.Log("Gate 2 skip"); return; }
    EditorPrefs.SetString(versionKey, current);
    UCL_MenuWindow.ShowWindow(new UCL_EditorMenu()); // ★ 新增：先強制開窗
    UCL_WelcomePage.OpenAndShow();
}

public static void ForceOpen()  // 跳過 gate，給 Cmd_Invoke 重複測試
{
    UCL_MenuWindow.ShowWindow(new UCL_EditorMenu());
    UCL_WelcomePage.OpenAndShow();
}
```

### 4. GetGitRoot walk

```csharp
public static string GetGitRoot()
{
    string p = Path.GetFullPath(Application.dataPath);
    string cur = Path.GetDirectoryName(p);
    while (!string.IsNullOrEmpty(cur))
    {
        // submodule 的 .git 是檔案 redirect → 只認 directory
        if (Directory.Exists(Path.Combine(cur, ".git"))) return cur.Replace('\\', '/');
        string parent = Path.GetDirectoryName(cur);
        if (string.IsNullOrEmpty(parent) || parent == cur) break;
        cur = parent;
    }
    // fallback：保留舊行為
    return Path.GetFullPath(Path.Combine(Application.dataPath, "../..")).Replace('\\', '/');
}
```

### 5. DocSearch 用 DocsModuleRegistry

```csharp
var roots = new List<string> { "Docs" };
string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? "";
foreach (var module in UCL_DocsModuleRegistry.All)
{
    string baseDir = module.ResolveBaseProvider?.Invoke();
    if (string.IsNullOrEmpty(baseDir)) continue;
    string absBase = Path.IsPathRooted(baseDir) ? baseDir : Path.Combine(projectRoot, baseDir);
    string absDocs = string.IsNullOrEmpty(module.DocsSubfolder)
        ? absBase : Path.Combine(absBase, module.DocsSubfolder);
    if (Directory.Exists(absDocs)) roots.Add(absDocs);
}
```

跟 HelpURL 走同一份註冊表 → 「搜得到的 = HelpURL 點得開的」永遠一致，避免對偶 bug。

## Cmd_Invoke / EditorPrefs 互動的副作用

驗證過程踩到的點，記錄供未來 debug 參考：

- **Unity 把 EditorPrefs 快取在 process 記憶體**：PowerShell 直接刪 registry 對 running Unity 無效，Unity 結束時才 flush 並覆寫 registry。要清 pref 必須 `EditorPrefs.DeleteKey(key)` 從 Editor 內部呼叫（可走 `Cmd_Invoke`）。
- **`run_cmd.py` 的 git_root 與 watcher 的 git_root 算法不同**：python 走 `_find_git_root_by_walk` 找 `.git` directory；C# watcher 用 `Application.dataPath/../..`。對 flat layout（TEVI）會錯位，trigger 落到不同資料夾，Cmd 永遠不被消費。workaround：`CLAUDE_PROJECT_DIR=<watcher_root>` 環境變數讓 python 用同一根。

## Breaking changes

無實質 API 變動：
- `UCL_WelcomePage.PrefKey_*` 從 `const` 改 `static get` — 所有 caller 都是字串引用，編譯透明
- `UCL_WelcomeAutoOpen.TryAutoOpen` 從 `private` 改 `public` — 擴充 access，沒縮減
- `GetGitRoot()` 在大多數 layout 下回傳值不變（CardGame layout 結果相同）；flat layout 回傳改為「真正的 git root」（修 bug）

## Migration

舊 EditorPrefs key（`UCL_Core.Welcome.ShownVersion` 不帶 `@<hash>`）會留在 registry 永久（無害垃圾）。要清乾淨可手動刪 registry 或從 Editor 內 `EditorPrefs.DeleteKey("UCL_Core.Welcome.ShownVersion")`（一次性）。

## 驗證

| 場景 | 預期 | 實測 |
|---|---|---|
| TEVI flat layout：DocSearch 開 console | `roots=[Docs, <abs UCL_Core/Docs~>]`，N>0 entries | ✅ |
| 跨專案 EditorPrefs 隔離 | 各專案各自首次彈一次 Welcome | ✅ project hash `73B12CF0` 寫入正確 |
| TryAutoOpen log 鏈 | 五段 log 完整出現 | ✅ Gate 決策可診斷 |
| ForceOpen 跳 gate | Welcome 直接彈出 | ✅ 透過 Cmd_Invoke 重複測試成功 |
| Editor 啟動無 NRE | localize init 不再噴 PlayerLoopHelper NRE | ✅ delayCall 後就緒 |
| CardGame layout 不破 | 既有專案行為不變 | ✅ GetGitRoot walk 仍命中相同 git root |
