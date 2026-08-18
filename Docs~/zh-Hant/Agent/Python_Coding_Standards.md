---
title: Python 撰寫規範 (Python Coding Standards)
description: UCL_Core Tools~ 底下 Python CLI 的硬規則 — 路徑一律走 ucl_paths、錢一律走 Cmd、狀態寫入不自己來、失敗要出聲。寫任何 .py 前先讀本檔。
tags: [python, coding-standards, paths, treasury]
aliases: [python 規範, python coding, 寫 python 前]
target_audience: [AI_Agent, Tools_Maintainer]
last_updated: 2026-08-17
---

# 🐍 Python 撰寫規範

> 一句話：**在這個 repo 裡寫 Python，最貴的錯不是寫錯邏輯，是「路徑推對了九次、第十次推到別的地方，而它不會告訴你」。**

本檔是 C# 端 [`Coding_Standards.md`](Coding_Standards.md) 的姊妹篇。
`<UCL_Core>/Tools~/AgentCommands/` 底下所有 `.py` 適用。

---

## ⛔ 硬規則一：路徑一律走 `_lib/ucl_paths.py`，**不准自己推導**

```python
# ✅ 唯一正確的做法
def _ucl_paths():
    import importlib.util as _ilu
    _spec = _ilu.spec_from_file_location(
        "_ucl_paths_<yourtool>", Path(__file__).resolve().parent / "_lib" / "ucl_paths.py")
    _m = _ilu.module_from_spec(_spec); _spec.loader.exec_module(_m)
    return _m

REPO_ROOT = _ucl_paths().repo_root()      # git repo 根
DATA_ROOT = _ucl_paths().data_root()      # <repo>/AgentCommands（可被路徑快照覆寫）
CORE_DIR  = _ucl_paths().ucl_core_dir()   # UCL_Core 掛載位置
```

| 要什麼 | 用哪個 |
|---|---|
| repo 根 | `repo_root()` |
| AgentCommands 資料根 | `data_root()` |
| UCL_Core 自身 | `ucl_core_dir()` |
| persona / registry / letters / comic 等子路徑 | `persona_file()` / `registry_path()` / `letters_root()` / `comic_root()`… |
| **letters 底下的版面**（信 vs Cmd 回傳檔） | `letters_persona_dir()` / `letters_cmd_dir()` / `letters_cmd_payload(persona, cmd, step)` |

> ⚠ **letters 底下不要自己接字串**（`letters_root() / persona / f"_{cmd}_{step}.md"`）。
> C# 對側是 `UCL_LettersPath`，兩端**要一起改** —— 只改一端的後果是兩邊各看各的目錄，
> 而**兩邊都不會報錯**（寫檔會自動建目錄，新舊位置各有一份、各自看起來都正常）。
> 🩸 2026-08-18 Cmd 回傳檔搬進 `letters/<persona>/cmd/` 時就是靠這兩支才只改一處。

### ❌ 以下每一種都出過事，全部禁止

```python
Path(__file__).resolve().parents[6]                    # 寫死目錄深度
(p / "AgentCommands").is_dir() and (p / "CardGame").is_dir()   # 寫死別的專案佈局
Path("CardGame") / "AgentCommands"                     # 寫死專案名
while p != p.parent: if (p/".git").is_dir(): ...       # 自己 walk（submodule 的 .git 是**檔案**，會撞坑）
os.environ["CLAUDE_PROJECT_DIR"] or cwd or ...         # 自排 fallback 順序
```

### 🩸 為什麼這條是硬規則（2026-08-17 一天內撞到三次，全部靜默）

| 工具 | 推導方式 | 後果 |
|---|---|---|
| `chess.py` | 要求同時有 `AgentCommands/` **與 `CardGame/`**，否則 `parents[6]` | 全部棋局檔寫進 **repo 外**、不在版控裡；C# 讀 repo 內的舊快照，於是**兩邊的骰面對同一局講出相反的話** |
| `UCL_BartenderDaemon`（C#，同族） | `Application.dataPath/../..` | 跳到 repo 上一層，**剛好命中一棵舊資料樹** → 餘額查詢回報 `453`，真實帳本是 `1330`。**差 877，而且完全沒有錯誤訊息** |
| `hook_validate_modified.py` | `Path("CardGame")/"AgentCommands"` | 報告寫進不存在的目錄；寫檔會自動建目錄 ⇒ **憑空長出假資料夾**，人去正確位置找只會找不到 |

**共同形狀**：每一層單獨看都合理，fallback 也「保守」，但**沒有任何一層負責說「我找不到」**。
最壞的一種甚至不是找不到 —— 是**找到了另一個宇宙的檔**，回傳一個看起來完全正常的數字。

⇒ `ucl_paths` 讀 C# 寫的路徑快照 `.agentcommands_root.local`，
**兩端因此保證同源**。這才是重點：不是「它推得比較準」，是「它跟 C# 推的是同一個」。

> 📌 判準：**路徑不該被推導，該被傳遞。**

---

## ⛔ 硬規則二：錢一律走 Cmd（`_lib/treasury_cmd.py`），python 不直寫帳本

```python
from _lib.treasury_cmd import (treasury_credit, treasury_debit, treasury_balance,
                               canvas_voucher_grant, canvas_voucher_consume)
```

涵蓋 **token（Treasury）與券（Canvas voucher）兩種錢**。直寫的四條後果寫在
`_lib/treasury_cmd.py` 檔頭（餘額快取靜默失準 / 繞過冪等判重 / 簽章不可信 / `balance_before/after` 要事後回填）。

🩸 **券曾經是唯一的缺口**：consume 早就走 Cmd，grant 卻留著兩處直寫
（`canvas.py voucher grant`、`chess.py grant_voucher`）。
2026-08-17 那次帳本分裂，**路徑 bug 是導火線，但能燒起來是因為那裡本來就允許直寫**。

**查餘額也一樣**：不要自己掃 ledger。
python 端各自全掃的複製品曾有四份，每份 14,985 檔逐檔 `json.load` ——
冷快取近兩分鐘，早安 brief 被拖到 112s，撞 120s timeout 被 kill。

---

## ⛔ 硬規則三：狀態寫入前先問「C# 那邊是不是已經有擁有者」

| 狀態 | 擁有者 | python 該怎麼做 |
|---|---|---|
| Treasury / 券 | `UCL_TreasuryLedger` / `UCL_CanvasVoucherLedger` | 走 Cmd |
| persona lock / registry | `UCL_AwakeningService` | 走 Cmd（`GoodMorning` / `GoodNight`） |
| 自由時間 session / 免費像素 | `Cmd_FreeTime` 發放、python 只遞增 `used` | **兩端 schema 對齊義務** |
| 酒館訊息 | `Cmd_Tavern` | **絕不直寫 jsonl**（T36 P0 教訓） |

兩端共讀同一份 JSON 時，**改任一端的 schema 必須同步另一端**，並在兩邊都寫下這條義務。

---

## 📏 一般慣例

- **Windows 終端**：檔頭設 `sys.stdout.reconfigure(encoding="utf-8", errors="replace")`
  —— cp950 印中文 / emoji 會炸。
- **外部 process**：`subprocess.run` 要帶 `encoding="utf-8", errors="replace"` 與 `timeout`。
- **fail-soft 要出聲**：讀不到就回 `None` 並印警告，**不要回 `0`／空字串假裝正常**。
  `0` 是「有帳戶但沒錢」，`None` 是「問不到」—— 混淆會讓額度顯示成 0 而看起來像破產。
- **印 ✓ 不算數，讀回來才算**：寫檔／發券／post 之後，要驗就去讀落地結果，
  不要用記憶體裡的值印「new balance」。
- **純 stdlib 優先**（對齊 canvas.py / library.py / awakening.py）；要 pip 依賴先問。

---

## 🔍 寫完自我檢查

```bash
# 路徑：確認解析出的 root 真的是 repo 而不是它的上一層
python -c "import importlib.util as i;s=i.spec_from_file_location('t','<你的檔>.py');m=i.module_from_spec(s);s.loader.exec_module(m);print(m.REPO_ROOT)"

# HelpURL / 文件連結沒斷
python <UCL_Core>/Tools~/AgentCommands/helpurl_check.py --strict
```

## 相關

- C# 端：[`Coding_Standards.md`](Coding_Standards.md)
- 路徑慣例（跨三端）：skill `ucl-core-paths`
- 註解規範：[`Code_Comment_Standards.md`](Code_Comment_Standards.md)
- 工具索引：[`../Tools/Python_Tools_Index.md`](../Tools/Python_Tools_Index.md)
