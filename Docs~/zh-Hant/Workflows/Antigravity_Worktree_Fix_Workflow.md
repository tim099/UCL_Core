---
title: Antigravity（Gemini）開過 Worktree 後失靈的修復
description: 同一個 git repo 用過 git worktree 之後，該 repo 的 Antigravity（Gemini CLI / Code）會卡死、所有 prompt 沒反應。修法是 unset `extensions.worktreeConfig`。
last_updated: 2026-05-08
target_audience: [AI_Agent, Developer]
aliases: [antigravity broken, gemini cli no reply, worktree breaks gemini, gemini stuck, antigravity rescue]
tags: [workflow, antigravity, gemini, worktree, git-config, agent-rescue]
related:
  - ucl_core:Docs~/{lang}/CommandTable.md | CommandTable | 口語觸發詞 → workflow 入口
---

# 🛟 Antigravity Worktree 失靈修復

> [!IMPORTANT]
> 同一個 git repo **建立過 worktree** 後，該 repo 內的 Antigravity（Gemini）就會出現「prompt 完全沒反應」的卡死狀態。
> 不是 Gemini 模型問題，是 Antigravity 對 git config 的 `extensions.worktreeConfig` 旗標處理有 bug。

---

## 1. 症狀

- 在曾經 `git worktree add` 過的 repo 內打開 Antigravity / Gemini Code
- 任何 prompt 送出後**完全沒回應** — 沒進度、沒錯誤、沒有 streaming 任何字
- Gemini Code 看似正常啟動，介面有反應，但 LLM 端就是不回話
- 退出該 repo / 在別的 repo 操作則正常

---

## 2. 修法（單行）

```bash
git config --unset extensions.worktreeConfig
```

在出問題的 repo 根目錄執行即可。執行後 Antigravity 立刻恢復正常，不必重啟。

> 不確定當前 repo 是否中招？先檢查：
> ```bash
> git config --get extensions.worktreeConfig
> ```
> 印出 `true` → 中招；空字串 → 沒事。

---

## 3. 為什麼會這樣

`git worktree add` 會在 main repo 的 `.git/config` 寫入 `[extensions] worktreeConfig = true`，啟用「per-worktree config」機制（讓不同 worktree 可以有獨立的 git config）。

Antigravity / Gemini Code 在 cold start 時讀 git config 解析倉庫 metadata，這個 extension 旗標會觸發某條 code path 把 LLM round-trip 卡死（具體 bug 機制由 Google 那邊掌握）。

unset 之後 git 仍可正常運作 — 該 worktree 變回讀**全域** `.git/config` 而非 per-worktree config。如果你**已經**在 worktree 內依賴 per-worktree config（少見），unset 會丟失那部分；常規使用無感。

---

## 4. 反向作用：移回後的 worktree 怎麼辦？

實務上 99% 的 worktree 用法不依賴 per-worktree config，unset 沒副作用。如果之後想再用 worktree feature 並要 per-worktree config，可重新：

```bash
git config extensions.worktreeConfig true
```

但要做**任何**這類測試前，記得先把 Antigravity / Gemini session 收掉，否則修完又會卡。

---

## 5. 來源

- 使用者實測（2026-05-08）— 同 repo 開過 worktree 後 Antigravity 卡死，跑 unset 即修復
- Google AI Studio 討論串：<https://discuss.ai.google.dev/t/solution-no-response-to-any-prompt/139655>

---

## 6. 給 agent 的對話模板

當使用者抱怨「Gemini大小姐不說話」/「Antigravity 沒反應」/「agent 沒回應」時：

1. 先問「是不是這個 repo 開過 git worktree？」
2. 是 → 引用本 workflow，建議跑 `git config --unset extensions.worktreeConfig`
3. 否 → 不是這個問題，走別的排查（網路 / API key / Gemini session 狀態）

不要建議「重啟 Antigravity」/「換 model」/「reload window」 — 對這個 bug 沒用，會浪費時間。
