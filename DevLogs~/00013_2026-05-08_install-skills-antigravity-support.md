---
date: 2026-05-08
index: 00013
title: 支援 install_skills.py --target antigravity 暨動態 YAML Flow Style 觸發器轉換
tags: [feature, tools, docs, cross-agent]
---

# UCL Skills 跨 Agent 支援 — 完美征服 `--target antigravity` 暨動態 YAML 觸發器系統

## What

為 UCL Core 的 Agent 技能安裝腳本 `install_skills.py` 進行了重大擴充，完美支援多 IDE / 跨 Agent 的規則鏈接，並進行了說明文件多語系翻譯：

1. **`install_skills.py` 支援 `--target antigravity`** — 支援將內建的 5 大工作流技能（UCL Skills）自動一鍵安裝至 Antigravity 與 Cursor 體系全域規則目錄 `/.agents/rules/` 下。
2. **動態 YAML Flow Style 觸發器** — 當為 Antigravity 目標安裝 Skill 時，腳本會自動讀取 `SKILL.md`，並在 YAML Frontmatter 頂部注入高雅的 `trigger: { on_intent: [...], on_files: [...] }` 屬性。格式 100% 符合 YAML 標準 Flow Style 規範，能被所有 YAML-compliant 的 Agent 引擎順暢讀取，徹底告別 Custom Parser 報錯。
3. **編譯排查雙軌併行觸發 (ucl-compile-error)** — 針對 `ucl-compile-error` 技能，實作了基於 C# 檔案與特定編譯報錯意圖的雙軌並行觸發：
   `trigger: { on_files: ["*.cs"], on_intent: ["編譯錯", "compile error", "CS0103", "CS0117", "CS1503", "CS0246", "asmdef", "assembly"] }`
   確保在編譯失敗、腳本損壞的最嚴苛情境下，對應規則能被瞬間精準調用！
4. **未來解耦與自定義 Trigger 繼承** — 在安裝轉換邏輯中加入了防守型的 `if "trigger:" not in frontmatter:` 判定。未來開發者新增 Skill 時，若直接在原始 `SKILL.md` 的 Frontmatter 中寫好 `trigger` 設定，安裝腳本會 100% 完整保留來源格式，完全不需要修改腳本。
5. **說明文件多國語系國際化 (en/ja/zh-Hans)** — 將新建立的 onboarding 工具頁說明 [`UCL_AgentSkillManagerPage.md`](file:///d:/Unity/EmblemOfValor/CardGame/Assets/UCL/UCL_Core/Docs~/zh-Hant/UCL_EditorPage/UCL_AgentSkillManagerPage.md) 完美翻譯並分流部署至英文 (en)、日文 (ja)、簡體中文 (zh-Hans) 資料夾中。

---

## Why

### 1. 跨 Agent 體驗對等與 Token 壓力釋放
Claude 端具備 `description-match` 的動態 Lazy-Load 特性，而原本 Antigravity 端僅能採用常駐的 `always_on` 模式。
若將 5 大 UCL Skills 全部設為 `always_on`，等同於將所有規則 inline 合併成一份超巨大的 rules，導致 Agent 在每一次對話 turn 中都必須攜帶所有工作流上下文，這會對 Token 消耗和 Context Window 造成極大的累積壓力。
改為 `on_intent` 與 `on_files` 後，Antigravity 成功實現了「不觸發就不吃 Context」的極致 Lazy-Load 機制，兩端體驗完美對等！

### 2. YAML 標準語意相容性
直接拼接 `on_files(*.cs) + on_intent(...)` 這類非標準字串，會導致標準 YAML 語法分析器出錯。改用 `{ on_files: [...], on_intent: [...] }` 標準 YAML Flow Style 後，結構安全、無比高雅，能與任何 cross-IDE 規則載入器完美相容。

---

## How to use

```powershell
# 1. 執行 Dry-Run（乾跑），觀察安裝對照與檔案轉換路徑
python CardGame/Assets/UCL/UCL_Core/Tools~/install_skills.py --target antigravity --dry-run

# 2. 正式執行安裝，一鍵轉換並部署至全域 /.agents/rules/
python CardGame/Assets/UCL/UCL_Core/Tools~/install_skills.py --target antigravity

# 3. 執行乾淨卸載，會將 ucl-*.md、.ucl_source、.ucl_installed 全面清空，不傷及自建 rule.md
python CardGame/Assets/UCL/UCL_Core/Tools~/install_skills.py --target antigravity --uninstall
```

---

## Breaking changes

無破壞性 API 或現有機制變更。

---

## Migration

由於先前的測試版本可能在 `/.agents/rules/` 下生成了帶有 `always_on` 的舊版規則 Markdown 複本：
建議在升級至最新版時，優先在終端機中執行一次**安全卸載**：
```powershell
python CardGame/Assets/UCL/UCL_Core/Tools~/install_skills.py --target antigravity --uninstall
```
隨後重新跑**正式安裝**，以一鍵覆寫並啟用最新最省 token 的標準雙軌 Flow Style 觸發器系統！

---

## 驗證

| 測試場景 | 預期結果 | 實測結果 |
|---|---|---|
| `--dry-run` 輸出驗證 | 輸出精準的拷貝路徑日誌，實際無寫檔行為 | ✅ 符合預期 |
| `--uninstall` 安全清除 | 刪除所有 `ucl-*.md` 及其伴隨追蹤檔，保留 `rule.md` 不動 | ✅ `.agents/rules/` 僅剩 `rule.md` |
| `--target antigravity` 正式寫入 | 在 `/.agents/rules/` 下生成 5 大 `ucl-*.md` 檔案與對應 `.ucl_source` 檔 | ✅ `copied=5 skipped=0 selected=5` 完美成功 |
| `ucl-compile-error.md` YAML 檢查 | Frontmatter trigger 呈現正確的 Flow Style `{}`，name/description 保留 | ✅ 100% 符合 YAML 標準 |
| 多國語系說明書（en / ja / zh-Hans） | 在對應語系子資料夾下產出完美翻譯版且連結可用 | ✅ `Docs~/` 三國語系版全部完成部署 |
