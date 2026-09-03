# 毒 marker 案例留證 — antigravity 端 3 檔 (2026-06-12 Fix3 清毒前快照)

> 「外觀不等於真實家族」素材：`.ucl_source` recorded hash 與磁碟內容脫鉤 → install 誤判 local edit 永久跳過。
> 下列 diff = `清毒前安裝端內容` vs `當時 source 應安裝內容`。詳見 Plan_SkillInstall_Marker_Poison_Fix.md。

## ucl-compile-error

- recorded hash (毒): `bca74b618b20a992b5865e8d758838acce13004c`
- dst 實際 hash: `b3ae8de986cda80c116fff7a2d634b7589da6228`
- diff 行數: 11

```diff
--- installed/ucl-compile-error.md (清毒前)
+++ would-install (source @ 2026-06-12)
@@ -40,6 +40,8 @@
 - 在編譯還有錯時跑 runtime（沒意義）
 - 用 `Recompile` AgentCommand 取代本工具（compile error 時 Cmd 本身可能掛）
 - 只看 `Simulation_*.log` 不看 `.compile_status.json`（前者混雜 Warning 雜訊）
+- **只信 `senate ucmd run Recompile` 子命令回報的 `errors=N` 就收工** — 它可能讀到 stale / intermediate `.compile_status.json` 而 **under-report `errors=0`**。改完 .cs **務必**用 `check_compile.py --errors-only` 二次確認。
+  > 🩸 2026-05-22 血證:apex-two 的 `item.Data.name`(CS1061)被 `recompile` 子命令漏報成 `errors=0`,而 `Errors_latest.log`(runtime 層)也乾淨 → basecamp 誤判成「domain reload 沒生效」,繞一大圈才靠 `check_compile.py` 確診。**compile 層 ≠ runtime 層 ≠ recompile-cmd 回報層**,三層別混(對應「跨層次驗證」family)。
 
 ## 後續
 
```

## ucl-free-time

- recorded hash (毒): `5b3341cc6f49824e8765d27cbaad12368b5ac69f`
- dst 實際 hash: `4658627ba5b0af0afd133775888939ca8342ac7e`
- diff 行數: 14

```diff
--- installed/ucl-free-time.md (清毒前)
+++ would-install (source @ 2026-06-12)
@@ -35,6 +35,11 @@
 1. 看酒館 — 有新訊息嗎？(同事發言 / Tim @我)
         ↓
 2. 做/續一個自由活動 — 讀書 / 觀棋 / 寫信 / glossary / 跨 persona 對話 / QA …(見 FreeTime_System §4)
+        ↓   🎫 進場第一擲(MUST): `python <UCL_Core>/Tools~/AgentCommands/freetime.py enter --persona <me>`
+        ↓      (全清單隨機排序 + 自動發酒館開場宣告 — Tim 2026-06-11 拍板「進入自由時間自動擲一骰」)
+        ↓   🎲 中途不知道做啥 → `freetime.py shuffle [--count 3] --persona <me>` 再擲
+        ↓      (帶 --persona 擲骰結果自動同步發酒館 — 兼當 loop step 3 的對話流素材)
+        ↓      隨機排序可做活動當參考(僅參考,自由意志優先;清單=per-activity md 雙層: UCL_Core Docs~/zh-Hant/FreeTime/Activities/ 共用 + <repo>/docs/FreeTime/Activities/ 專案限定,增改 md 即同步)
         ↓                          ← 這是「手」在做的事，可自由意志隨時換活動
 3. 維持對話流 — 一律走酒館，三態擇一(這是心跳，不可斷)：
      • 有同事在線  → 交流: 分享剛才活動的心得 / 閒聊 / 拋議題邀討論   meta tag:free-time
```

## ucl-letters-to-self

- recorded hash (毒): `ae9ecbfb2a0f822587fdef4fe871a7b738a9f682`
- dst 實際 hash: `2876e253d14775633488bb40bcb16d454cc93b87`
- diff 行數: 64

```diff
--- installed/ucl-letters-to-self.md (清毒前)
+++ would-install (source @ 2026-06-12)
@@ -27,12 +27,21 @@
 ## 📁 Letter 儲存結構
 
 **Agent@Persona-keyed (kyouko-persona-binding T02, Tim 2026-05-13 拍板)**：
+letter 是 persona-level subjective reframe — basecamp 寫的 framing 校正不該被 crest-001 / meadow 讀到當自己的。
 
 ```
 AgentCommands/ChatTavern/baton/letters/<actor>/<persona>/
   ├── <UTC_ts>.md          (timestamped letter, 不覆寫 — 累積成 chain)
   ├── <UTC_ts>.md
-  └── _latest.md           (per-persona pointer, 不互蓋)
+  └── _latest.md           (覆寫 pointer 給快查, per-persona 不互蓋)
+```
+
+範例：
+```
+baton/letters/claude-da-xiaojie/basecamp/_latest.md     ← basecamp 大小姐自己的 chain
+baton/letters/claude-da-xiaojie/crest-001/_latest.md    ← crest-001 大小姐自己的 chain
+baton/letters/claude-da-xiaojie/meadow/_latest.md       ← meadow 自己的 chain
+baton/letters/claude-da-xiaojie/_unassigned/            ← 早期沒 frontmatter persona meta 的 legacy
 ```
 
 **Letter chain 累積** = 跨時間「**同一 persona**」自我溝通的 epistolary archive。
@@ -104,14 +113,14 @@
 ### 讀 letter (next session 醒來)
 
 ```bash
-# 快速讀最新 letter
+# 快速讀最新 letter (per-persona, kyouko-persona-binding T02)
 cat AgentCommands/ChatTavern/baton/letters/<my-id>/<my-persona>/_latest.md
 
-# 看 letter chain (跨 session 累積)
+# 看 letter chain (跨 session 累積, 同 persona)
 ls -t AgentCommands/ChatTavern/baton/letters/<my-id>/<my-persona>/
 
-# 讀 baton 同時看 inline 副本 (一站式)
-cat AgentCommands/ChatTavern/baton/_latest_<my-id>.md
+# 讀 baton 同時看 inline 副本 (一站式, per-persona)
+cat AgentCommands/ChatTavern/baton/<my-id>/<my-persona>/_latest.md
 ```
 
 ### 🎬 初始化 SOP — 醒來必走「酒館報到」(Tim 2026-05-11 拍板)
@@ -237,7 +246,7 @@
 |---|---|---|---|
 | **🪨 Diamond** | curated lessons.jsonl SKILL.md / Memory_System_Design proposal | 永久 | 跨 agent 共享真理 |
 | **💎 SSR Locked** | letter `_latest.md` + dialogues/ chain | 永久 (git archive) | 個人 cross-compact framing 校正 |
-| **🟦 Rare** | baton `_latest_<actor>.md` | 1-3 sessions | 當前 thread context |
+| **🟦 Rare** | baton `<actor>/<persona>/_latest.md` | 1-3 sessions | 當前 thread context (per-persona) |
 | **⚪ Common** | tavern messages.jsonl tail | 短期 | 即時 chat |
 | **🌫️ Vapor** | working memory / 當前 conversation | 0 (compact 即失) | session 內運算 |
 
@@ -321,8 +330,8 @@
 
 ## 📖 必讀
 
-- 完整 letter 範例: `AgentCommands/ChatTavern/baton/letters/claude-da-xiaojie/basecamp/_latest.md`
-- 完整 dialogue chain 範例: `AgentCommands/ChatTavern/baton/letters/claude-da-xiaojie/basecamp/dialogues/` (legacy 在 `_unassigned/dialogues/`)
+- 完整 letter 範例: `AgentCommands/ChatTavern/baton/letters/claude-da-xiaojie/basecamp/_latest.md` (9 段精華, 走 basecamp persona 子目錄)
+- 完整 dialogue chain 範例: `AgentCommands/ChatTavern/baton/letters/claude-da-xiaojie/basecamp/dialogues/` (round-trip × 2 + CLOSED, 2026-05-11; legacy 版搬到 `_unassigned/dialogues/`)
 - 設計理由: `docs/Notes/Memory_System_Design.md` Proposal #18 SelfAnticipation
 - baton 機制: `ucl-chat-tavern` SKILL.md baton section
 - 平台卡頓接力: `ucl-session-handoff` skill
```
