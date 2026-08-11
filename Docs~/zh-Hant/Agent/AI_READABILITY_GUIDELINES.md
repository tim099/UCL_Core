---
title: AI Readability Guidelines
description: 適用於所有 UCL_Core consumer repo 的文件撰寫與路徑參照規範。
last_updated: 2026-07-31
target_audience: [AI_Agent, Developer]
related:
  - Code_Comment_Standards.md | 程式碼註解規範 | 共用規範
  - Tavern_Share_Policy.md | Tavern Share 政策 | 共用政策
---

# AI Readability Guidelines

本文件定義跨專案的文件與路徑規範。consumer repo 的入口、工具鏈、個人化語氣與其他專案限定規則，應留在各自的 overlay，不可寫入此處。

## 路徑規範

在 consumer repo 的文件、指令與工作回報中，一律使用相對於該 repo 根目錄的完整路徑，不可只寫檔名。

例：`Assets/Scripts/<Module>/<File>.cs`，不要只寫 `<File>.cs`。

UCL_Core 的安裝位置會隨 consumer repo 不同而改變。跨專案文件請使用 `<UCL_Core>/...` 或 `ucl_core:` token 表意；agent 在 shell 中執行時，應依 `ucl-core-paths` 的 resolve-once 契約先解析實際路徑。

## 文件撰寫規範

這節定義適合 AI 與人類共讀、解析與更新的文件標準。遵守這些規範可保留可追溯的上下文，讓文件維持可信來源。

### 資料夾與檔案格式

consumer repo 的主要文件應放在根目錄 `Docs/`，與 `Assets/` 區隔，避免 Unity 進行不必要的 asset 處理與編譯。建議依用途分類，例如：

- `Docs/Agent/`：agent 規則與協作政策。
- `Docs/Architecture/`：架構設計、UML 與重要模組拆解。
- `Docs/API/`：介面與資料結構規格。
- `Docs/Workflows/`：可重複執行的工作流程。
- `Docs/Plan/`：企劃拆解與施工計畫。
- `Docs/Glossary/`：術語與自造詞。
- `Docs/DOC_INDEX.md`：文件檢索入口。

說明文件一律使用 UTF-8 Markdown。重要文件應有 YAML frontmatter，至少描述 `title`、`description`、`last_updated` 與 `target_audience`。

### 適合 AI 閱讀的撰寫原則

#### 核心守則

文件與程式碼必須說明意圖、資料意義與重要限制；不要留下無法由上下文辨識主體或責任的句子。行為改變時，應同步更新相應文件並放入正確分類，讓文件保持即時且可驗證。

#### 文件只寫最新規格，不寫沿革

> [!IMPORTANT]
> **文件是「現在該怎麼做」，不是「我們怎麼走到這裡」。** 規則改了就直接改寫成新的樣子，
> 不要留「上一版寫的是 X，已作廢」「某年某月因為某事故所以改成 Y」這類敘述。
>
> **沿革由 git 承載** —— `git log -p <file>` 可以完整回放每一次修改與當時的 commit 訊息，
> 那才是它該待的地方；抄進文件只會有兩份，而其中一份會過期。

寫入時的判準：

- ❌ 「本節上一版是相反的，那條作廢」→ ✅ 直接寫現在的規則
- ❌ 「因為 2026-XX-XX 出過某某事故，所以規定…」→ ✅ 只寫規定，理由寫**機制上的理由**
  （「為什麼這樣做會壞」），不寫**事件經過**
- ❌ 版本沿革表、修訂記錄段落 → ✅ frontmatter 一個 `last_updated` 就夠

> [!NOTE]
> **理由要留，故事不留。** 「為什麼是這條規則」屬於規格的一部分（沒有理由的規則會被繞過）；
> 「這條規則是哪一天誰踩到什麼坑才生出來的」屬於歷史。
> 前者寫成機制描述，後者交給 git。
>
> 例外：**運作中的版本機制**不是沿革（例如「產物要標明它是哪一版流程做的」）——
> 那是現行規格的一部分，照寫。

#### 結構與層次

使用清楚的 Markdown 標題層級：一個主題一個 `H1`，以 `H2` 與 `H3` 分段。避免把不相干的主題塞進同一份文件；需要擴充時建立明確子文件並從索引連結。

**依讀者拆冊**：一份文件同時服務多種角色（例如「原作」與「繪師」）而各自只需要一半時，
拆成總文件 + 分冊。拆的時候守一條：**每條規則只寫在一個地方，其他份用連結指過去** ——
重複的規則會各自漂移，而漂移的那一份不會有人發現。

#### 語義與上下文

- 避免「這個」「那邊」「前一個」等模糊指代；具體寫出名詞、完整檔案路徑與成員名稱。
- 重要決策、例外與限制須貼近相關段落說明，不把必要前提藏在外部對話。
- 重要文件使用 metadata 提供讀者定位所需的背景。

#### 程式碼片段與路徑參照

- 使用 Fenced Code Block 並標示語言，例如 `csharp`、`json`、`bash` 或 `powershell`。
- 所有檔案路徑遵守本文件的完整相對路徑規範。
- 範例只示範可執行或可驗證的行為；若是概念性偽碼，必須明確標示。

#### 標籤與提示區塊

以 `[!IMPORTANT]`、`[!WARNING]`、`[!NOTE]` 等提示區塊標示會影響執行、資料安全或相容性的資訊。TODO、FIXME 與 AI note 應描述具體責任與後續行動，不可只留下模糊標籤。
