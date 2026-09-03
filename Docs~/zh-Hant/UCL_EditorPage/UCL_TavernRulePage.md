---
title: 酒館規則頁 (UCL_TavernRulePage)
description: Tavern Rule System 的 GUI — 列出 active / reverted 規則、看完整內容、一鍵 revert（走 Cmd_Rule 統一路徑，不在頁內 bypass 業務邏輯）。
tags: [editor-page, tavern, rules]
aliases: [規則頁, tavern rule, 酒館規則]
target_audience: [AI_Agent, Tools_User]
last_updated: 2026-08-17
---

# 📜 酒館規則頁 (UCL_TavernRulePage)

> 一句話：**看規則與撤回規則的地方**（Tim 2026-05-12 拍板，對應 [`Cmd_Rule`](../API/UCL_AgentCommand/Cmd_Rule.md)）。

## 版面

上 toolbar / 左 list / 右 detail（沿用 `UCL_AffinitySystemPage` 的慣例 —— 同型頁面長一樣，
使用者不必為每頁重學一次動線）。

| 區 | 內容 |
|---|---|
| toolbar | active / reverted 篩選 |
| 左 list | 規則清單 |
| 右 detail | 選中規則的完整內容 ＋ revert 按鈕 |

## revert 走 `Cmd_Rule`，不在頁內自己改

按下 revert **會呼叫 `Cmd_Rule.ExecuteAsync`**，跟 agent 跑 `senate ucmd run Rule --arg op=revert`
**是同一條路徑**。

> 這是本頁最重要的設計約束：撤銷規則牽涉 ledger（退還提案者 100 token）與檔案改動。
> 頁面自己動手就會有第二套業務邏輯，而兩套邏輯的漂移**只會在帳對不起來時才被發現**。

## 為什麼沒有「提案」UI

提案要構造 `title` / `body`（長文），**GUI 表單不如 CLI 合適** —— 走
`senate ucmd run Rule --arg op=propose ...`。

⚠ 提案消耗 **100 token** 且需餘額 ≥ 300；**只有 Tim 可以 revert**。

## 實作註記

2026-05-13 重構後本頁 UI 層 **zero-Editor 依賴**（改用 `UCL_GUIStyle` + 純 `GUILayout`，
移除 `EditorStyles` / `EditorGUILayout` / `EditorUtility` / `EditorApplication`）。
仍保留 `#if UNITY_EDITOR`，因為檔案在 `EditorCore/` 且 `Cmd_Rule` 的 revert handler 走 UnityEditor。

## 相關

- [`Cmd_Rule`](../API/UCL_AgentCommand/Cmd_Rule.md)
- [`UCL_CommonEditorPage`](UCL_CommonEditorPage.md)
