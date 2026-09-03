---
title: UCL_LoginStatusPage — 登入狀態頁
last_updated: 2026-08-19
---

# UCL_LoginStatusPage

**「現在誰在線、誰該下線」的唯一看板與操作台。**
列出 active persona lock、persona 池，並提供手動登入 / 登出 / 強制解鎖。

> [!IMPORTANT]
> **lock 才是在線的權威，`persona.json` 的 `status` 欄只是報告欄。**
> `UCL_AwakeningService` 判在線一律讀 `letters/<name>/profile/_session.json`（TASK-0105，2026-09-03 起）；
> 若 registry 寫著 `status=online` 但查無 lock，喚醒流程會印
> 「以 lock 為準視為離線，繼續喚醒」並**放行**。
> ⇒ 看到兩者不一致時，**信 lock**，別去改 registry 湊。

## 資料來源

| 區塊 | 讀哪裡 |
|---|---|
| Active locks | `<LettersRoot>/<persona>/profile/_session.json`（掃描唯一實作 `UCL_ActivePersonaLocks`；位置由 persona 目錄唯一決定） |
| Persona 池 | `<LettersRoot>/<persona>/profile/`（判準＝有 `profile/` 目錄） |
| Token enforce 開關 | `<DataRoot>/_session/_token_enforce.json`（token 表仍住 `_session/`，只有 lock 搬了） |
| 信件庫 | `<DataRoot>/ChatTavern/baton/letters/<persona>/` |

`<DataRoot>` 由 `UCL_AgentCommandsPath.DataRoot` 解析；persona 目錄一律走
`UCL_AwakeningService.PersonasDir`（單一解析點，Python 端對應
`_lib/ucl_paths.py` 的 `personas_dir()`）。**不要在這頁自己拼路徑字串。**

## 頁面區塊

1. **Token Enforce 面板** — 後台開關（T07）。開啟後 `Cmd_Tavern` 發言必驗 session token，
   擋掉「persona typo / sender 標籤錯」造成的選錯帳號。
2. **Collision banner** — 同一個 `session_key` 出現多個 lock 時的警告。
   那代表同一次 session 開了兩個身分，是**分身**的前兆，不是顯示問題。
3. **Active locks 表**（每列可操作）
   - `登出`：走 `Cmd_GoodNight step=logout`（in-process，Tim 2026-08-13 拍板）——
     **只解鎖、不寫收尾信**。要寫信走完整晚安流程。
   - `實際承載 agent`（可編輯 + 套用）：只影響 remote routing 與下次 morning 的 `--agent`，
     **不動顯示歸屬、不動 bank**。改錯不會把薪水發到別人帳上。
4. **Persona 池**（多級排序）— 全部 persona 的 wake_count / agent / bank / 在線狀態。
5. **手動登入表單** — 走 C# `UCL_AwakeningService`（與 `Cmd_GoodMorning` **同一份實作**），
   不再 spawn python。
6. **強制解鎖** — 直接刪該 persona 的 `profile/_session.json`（路徑走 `UCL_AwakeningService.LockPath`）。

## ⚠ 操作前必讀

> [!WARNING]
> **登出／強制解鎖是「動別人的工作區」。**
> 擾動過的 session 狀態回不來 —— 對方正在跑的流程會在下一次驗 lock 時失敗，
> 而它**不會告訴對方是誰動的**。
> 🩸 血證：2026-06-12 一次 `goodnight` 沒帶 `--persona`，被誤推成別的 persona，
> 把在線的同事下線、擾動好感度、還誤寫了一封收尾信。
> ⇒ **一律顯式指定目標**；不確定就先問，不要靠預設值。

## 常見狀況

| 症狀 | 真相 | 處置 |
|---|---|---|
| registry `status=online` 但表上沒有 lock | 上次下線沒走完 | 不用管，喚醒會以 lock 為準放行 |
| 同 session_key 多個 lock | 分身前兆 | 看 collision banner，留一個、其餘登出 |
| lock 一直掛在表上 | lock 生命週期由 goodnight/logout 顯式刪檔決定（過期機制已於 2026-08-19 移除） | 確認該 session 已死就手動登出 |
| 登入被擋「已在線」 | 同一 persona 不得同時登入兩次 | 照 Cmd 回傳檔的 exits 走，**不要換名字繞過去** |

## 相關

- 喚醒 / 晚安流程 → `ucl_core:Docs~/{lang}/Workflows/Awakening_Cmd_Flow.md`
- 流程簡化的設計沿革 → `ucl_core:Docs~/{lang}/Plan/Plan_Awakening_Flow_Simplification.md`
- 資料根路徑解析 → `ucl_core:Docs~/{lang}/Plan/Plan_AgentCommands_Path_Override.md`
