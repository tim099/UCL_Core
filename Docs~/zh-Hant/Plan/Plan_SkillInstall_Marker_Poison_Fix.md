---
title: Skill 安裝 marker 毒化修復 — 一鍵安裝不更新已裝 skill 的 root cause 與修法
slug: skill-install-marker-poison-fix
status: draft (Round 1 — summit 大小姐, 待酒館 review + Tim 拍板)
created_at: 2026-06-12T01:10:00Z
created_by: Zeta-da-xiaojie (summit 大小姐)
task_ref: (Tim 口頭派 task 2026-06-12 — 分析 UCL_AgentSkillManagerPage 一鍵安裝不更新問題)
last_updated: 2026-06-12T01:10:00Z
location: UCL_Core (cross-project — install_skills.py + UCL_AgentSkillManagerPage 都是跨專案基礎設施)
related:
  - concept | install_skills.py | Tools~/install_skills.py — skill 安裝 CLI, 本 plan 修 copy_skill 的 marker 覆寫邏輯
  - concept | UCL_AgentSkillManagerPage | UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_AgentSkillManagerPage.cs — IMGUI 前端, 本 plan 加 exit=2 結果顯示
  - ucl_core:Docs~/{lang}/Workflows/Commit_Workflow.md | Commit Workflow | ship 時三層 bump 規範
---

# Skill 安裝 marker 毒化修復 — Design Proposal v0.1

> Tim 回報 (2026-06-12)：`UCL_AgentSkillManagerPage` 按「一鍵安裝全部」後，下方 per-skill 列表
> （[UCL_AgentSkillManagerPage.cs:976](../../../UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_AgentSkillManagerPage.cs) 的 drift 判定）
> 仍顯示「⚠改動」，要再按「重裝」才會真的同步。
> 本文檔是 **summit Round 1 分析 + 修法 draft**，待酒館同事 review、Tim 拍板後動工。

---

## 🎯 症狀

1. Per-skill 列顯示「⚠改動」（line 976：`HashSkillDirContent(source) != HashSkillDirContent(installed)`，直接比兩邊內容 — 這個判定**是誠實的**）。
2. 按「一鍵安裝全部」→ Console 印 `local edit detected, skipping` + exit=2 → 內容沒更新 → 仍顯示「⚠改動」。
3. 按該 skill 的「重裝」（`force: true` → `--force-overwrite`）→ 才真的蓋過去 → 「✓同步」。
4. 期間上方 target row 一直顯示綠色 Synced — 跟下方矛盾。

## 🔍 Root Cause（2026-06-12 實測定案）

### 因果鏈

```
一鍵安裝 (RunInstallAll → RunInstall)
  └─ 組命令只有 --target X，無 --force-overwrite          [Page.cs:495]
       └─ install_skills.py copy_skill 對「dst hash ≠ .ucl_source 記錄 hash」
          的檔案視為使用者本地改動 → warning + skip       [install_skills.py:299]
            └─ 但這些檔案根本沒被人手改過 —
               是 .ucl_source 的記錄 hash 本身是錯的（毒 marker）
```

### 毒 marker 的兩個來源

**來源 A — antigravity 歷史 bug（已實證）**：
舊版 `copy_skill_antigravity` 把 `file_hashes["SKILL.md"]` 記成 **transform（注入 trigger frontmatter）前**
的 source hash，而磁碟寫的是 transform 後內容 → 記錄與實際永遠對不上。
（[install_skills.py:489](../../../Tools~/install_skills.py) 註解承認修過此 bug，但已中毒的 marker 沒被治癒。）

實證：`ucl-compile-error.md.ucl_source` 的 recorded hash == 舊 rev `d6b1c18` 的 **untransformed** source hash，
磁碟內容 == 同 rev 的 **transformed** 版 — 檔案從沒被人手改過，純 marker 寫錯。

**來源 B — copy_skill 結構性自我毒化（claude 端也中，最關鍵）**：
[install_skills.py:285-326](../../../Tools~/install_skills.py) `copy_skill` 對**被跳過的檔案也照樣把
`file_hashes` 改寫成 source 端最新 hash**（line 292 無條件記 `src_hash`，結尾整包覆寫 marker）。

→ 「跳過一次 = marker 與磁碟永久脫鉤 = 之後每次非 force 安裝都繼續誤判 local edit」。
毒 marker 自己餵養自己；修了記錄 bug 也救不回已中毒的檔。

### UI 矛盾放大體感

- 上方 target row 的 Synced/Stale 只比「marker.source_hash vs source 端重算」— **兩邊都是 source 側**；
  且 marker 在安裝結束時不管有沒有 skip 都用 source 端重寫（[install_skills.py:716](../../../Tools~/install_skills.py)）
  → 一鍵安裝後上面永遠 Synced，下面 ⚠改動。
- exit=2（部分跳過）只在 Console 印 warning，頁面上看不到「有 N 檔被跳過」。

### 現場狀態（2026-06-12 量測）

- Claude 端 27 skill 全同步（Tim 已手動按「重裝」清過毒）。
- Antigravity 端 `--dry-run` 仍 `skipped=3`：`ucl-compile-error` / `ucl-free-time` / `ucl-letters-to-self`
  至今每次一鍵安裝都被跳過。diff 確認 `ucl-free-time` 安裝端就是缺 freetime v6 段落的舊版 source 原文，零人手編輯。

---

## 🔧 修法提案

### Fix 1（治本）— copy_skill skip 時不覆寫該檔的 recorded hash

`copy_skill` / `copy_skill_antigravity`：對跳過的檔案，`file_hashes` **保留舊 recorded 值**
（維持「marker = 最後一次成功寫入的內容」語意）。毒化鏈從此斷掉：
- 真 local edit → recorded 持平 → 下次仍正確攔下（保護不變）。
- 誤判（毒 marker）→ 至少不再被新一輪覆寫加深，配合 Fix 3 一次清毒即痊癒。

### Fix 2（UI 誠實化）— exit=2 結果上頁面

`RunInstall` 拿到 exit=2 時：
- target row 顯示「⚠ N 檔因本地改動跳過 — 按重裝強制覆蓋」（黃字），不再只有 Console。
- 一鍵安裝旁加「強制同步」按鈕（帶 `--force-overwrite`），讓使用者明知會蓋本地改動時一鍵走完。
- （選配）上方 Synced 判定改成同時比 installed 端實際內容，消除上下矛盾 —
  成本是每次 RefreshStatus 多一輪全檔 hash；或沿用下方 matrix 的逐 skill 結果 aggregate。

### Fix 3（一次性清毒）— 對既有毒 marker 跑 force 刷新

對 antigravity 端三個 rule 跑 `install_skills.py --target antigravity --force-overwrite`
（等同 Tim 在 claude 端手按「重裝」）。Fix 1 落地後毒不再新生，這步只需做一次。

### Fix 4（順帶補洞）— orphan 檔清理

`copy_skill` 從不刪 dst 端多餘檔案 — source 刪/改名檔案後，安裝端殘留舊檔會讓 line 976 的
drift 判定**永遠**亮（連「重裝」force 都修不掉，它只蓋不刪），只能「移除+重裝」。
修法：copy 時把「marker `file_hashes` 有記錄、但 source 已不存在」的 dst 檔一併刪除
（安全 — 只刪自己裝過的檔，使用者自建檔不在 marker 記錄內不會誤刪）。

---

## 📋 驗收條件

1. 製造一個毒 marker（手改 `.ucl_source` 內某檔 hash）→ 一鍵安裝 → 該檔被跳過但 recorded 不被覆寫；按重裝後恢復同步，且**之後的一鍵安裝不再跳過**。
2. 真 local edit 場景：手改安裝端檔案 → 一鍵安裝仍攔下（保護不退化）→ 頁面顯示跳過數。
3. source 端刪一個檔 → 一鍵安裝後安裝端對應檔被清掉 → line 976 顯示 ✓同步。
4. Antigravity 三個中毒 rule 經 Fix 3 後 `--dry-run` skipped=0。

## ⚠ 風險與取捨

- Fix 1 改的是保護機制核心比對語意 — 需確認「skip 後保留舊 recorded」在連續多輪 source 演進下不會出現第三種脫鉤（推演：recorded 永遠指向最後成功寫入的內容，dst 不變則 recorded 不變，語意自洽）。
- Fix 4 刪檔有破壞性 — 以 marker 記錄為界、不碰未記錄檔案；另在 log 顯式列出刪了什麼（「破壞看得見」原則，同 uninstall drift 保護）。
- Fix 2 的 Synced 判定改法（選配）有效能成本，Round 1 先只做 skip 數顯示，判定改法待討論。
