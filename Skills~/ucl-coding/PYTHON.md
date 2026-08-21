# UCL Coding — Python 章

> 一句話：**寫任何 `.py` 之前先讀 `ucl_core:Docs~/{lang}/Agent/Python_Coding_Standards.md`** ——
> 本檔只是它的入口與最常撞的幾格，規範本體在那裡（單一事實源）。
>
> 本檔是 [`SKILL.md`](SKILL.md) 的 Python 專章（依語言拆出）。跨語言規則（路徑／錢／`--persona`／
> 開工廣播／坑寫回哪裡）在 `SKILL.md`，**不在本檔重抄**；C# 端見 [`CSHARP.md`](CSHARP.md)。

> [!WARNING]
> ## 🐍 用 python 腳本改 C# 的兩個坑（2026-08-20 一天內兩次）
> 跳脫字元在 `heredoc → python → .cs` 這條鏈上會被多解一次，把 C# 字串 literal 拆斷；
> 而 `assert s.count(old)==1` 通過**不代表**定義與呼叫端都換了（腳本印成功、code 編不過）。
> ⇒ 判準與修法（含「內容先落成檔案再插入」與「recompile 要看 errors= 那一行」）寫在
> `ucl_core:Docs~/{lang}/Agent/Python_Coding_Standards.md` **硬規則四**。

## 📚 規範本體（本章只是指路，細節不在這裡重抄）

| 主題 | 文件 |
|---|---|
| **Python 撰寫規範（寫任何 .py 前先讀）** | `ucl_core:Docs~/{lang}/Agent/Python_Coding_Standards.md` |
| 程式碼註解規範（區塊職責 / 物理意義 / 數值影響） | `ucl_core:Docs~/{lang}/Agent/Code_Comment_Standards.md` |
| UCL_Core 路徑解析（不要寫死安裝路徑） | skill `ucl-core-paths` |
| Python 工具索引（有沒有現成的） | `ucl_core:Docs~/{lang}/Tools/Python_Tools_Index.md` |

## 🧭 路徑：python 端一律走 `_lib/ucl_paths.py`

`repo_root()` / `data_root()` / `ucl_core_dir()`；**letters 底下走 `ucl_paths.letters_cmd_payload()`**
（C# 對側是 `UCL_LettersPath`）。

❌ 不要 `parents[N]`、不要自己 walk `.git`、不要自排 env/cwd fallback。

> `ucl_paths` 讀的是 **C# 寫的路徑快照** —— 兩端因此保證同源。
> 判準與三次無聲血證見 `SKILL.md`「① 路徑一律走既有解析器」；完整規範見 `Python_Coding_Standards.md`。

## 💰 錢：python 端一律走 `_lib/treasury_cmd.py`

token 與券都是，**不直寫帳本** —— 直寫會繞過餘額快取與冪等判重，且簽章欄位偽造成本為零。
（2026-08-17 券的帳本分裂：路徑 bug 是導火線，**能燒起來是因為 grant 那條路徑本來就允許直寫**。）

查餘額不要自己 parse `Treasury/` 底下的檔 —— 那是 C# `UCL_TreasuryLedger` 的守備範圍，
理由（正確性／效能／一致性）見 [`CSHARP.md`](CSHARP.md)「③ 銀行／餘額一律走 API」。

## 🔍 要驗證 C# 做了什麼：不要讀磁碟推導，直接呼叫它的 API

python 端拿到的磁碟檔可能是**平行索引**（有檔 ≠ 系統看得到、系統看得到 ≠ 磁碟那份是當前值）。
⇒ 走 `Cmd_Invoke` 反射呼叫，用法見 [`CSHARP.md`](CSHARP.md)「🔌 `Cmd_Invoke` 反射呼叫」。
