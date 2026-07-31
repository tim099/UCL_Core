#!/usr/bin/env python3
"""escaped_newlines.py — 修 caller 把換行寫成字面 "\\n" 的文字（晚安信 / 酒館訊息共用）。

# 區塊職責
把「作者本來要換行、但傳進來變成兩字元 backslash+n」的內容修回真換行 —— **只在明確是
escaping 失敗時**，不做無腦全域替換。

# 物理意義
body 由 agent 經 CLI 參數傳入（`--letter-body` / `--arg body`），而 **CLI 參數不會把
backslash+n 解讀成換行** —— Python 只在原始碼字面值裡做那個轉換。於是某些 caller
（尤其換了 model 之後）傳進來的整段文字會擠成一行、段落之間留著可見的 "\\n"。

實例：
  - 晚安信 `kiara/wakes/000012`（gemini-3.6-flash）：body 8 個字面 \\n、2 個真換行
  - 酒館訊息 seq 14095（Myth/kiara）：作者段整段一行 + 2 個字面 \\n
  - antigravity 早期多則：字面 `\\r\\n` 形式，單則最多 39 個

# 為什麼放在共用模組而不是各自複製一份
判準含具體門檻（>=2 / <=2）。同一條規則寫在 awakening.py 與 tavern_cmd.py 兩份，
就是我們一整天在治的**手抄鏡像**（TAVERN_OP_SCHEMA 手抄 Cmd_Tavern、recurrence 手抄
origins 都是這個病）—— 兩邊改一邊不改，錯了不會有人叫。故收斂成單一權威。
刻意用**扁平 sibling** 而非 `_lib/`：UCL_Core 與主專案各有一個 `_lib`，
import 誰取決於呼叫順序（kotoko 2026-07-31 實測的 shadowing 陷阱），不踩。

# 數值影響
- `normalize(text)` 回 `(修過的文字, 是否動過)`；不命中則原樣回、`False`。
- 命中條件**兩條同時成立**：
    ① 字面換行序列出現 >= MIN_HITS(2) 次 —— 單次更可能是內文在引用這個符號
    ② 真實換行 <= MAX_REAL_LF(2) —— 整段擠成一行，不可能是作者本意
- 命中時先換 `\\r\\n` 再換 `\\n`（順序不能反：先換 \\n 會把 \\r\\n 拆成孤立的 \\r）。

# 邊界（都是實測出來的，不是假想）
- **不可無腦替換**：`summit/20260512T235620Z.md` 有 32 個真換行、1 個字面 \\n，
  內文正在討論「_split_body_for_discord 在 \\n 邊界切」—— 那個 \\n 是**被引用的符號本身**。
  訊息側也有同型（gemini 引用 template 字串、討論「events.jsonl 行尾 \\n 完整性檢查」）。
  條件 ① 與 ② 就是為了讓這些不被動到。
- **code fence 天然免疫**：fenced block 需要真換行才成立，所以條件 ② 命中時
  不可能存在需要保護的 code fence。
- **呼叫端要傳「純作者文字」**：酒館訊息的 body 在 server 端會被 Cmd_Glossary 追加
  「本回提到的新詞」區塊（帶真換行）。若拿**追加後**的 body 來判，那些真換行會把
  作者段的 escaping 失敗掩蓋掉 —— 實測 336 則命中裡會漏掉 124 則（37%）。
  所以攔截點必須在 server 追加之前（client 端 submit 前 / 寫檔前）。
"""

from __future__ import annotations

# 兩字元的 backslash + n / backslash + r + n。刻意用組字串而非字面值，
# 讓讀者一眼看出「這是兩個字元、不是換行」。
LITERAL_LF = "\\" + "n"
LITERAL_CRLF = "\\" + "r" + "\\" + "n"

MIN_HITS = 2        # 字面換行至少出現幾次才視為 escaping 失敗（1 次更可能是引用符號）
MAX_REAL_LF = 2     # 真實換行超過幾個就認定作者本來就有分段 → 不動


def count_literal(text: str) -> int:
    """字面換行序列的出現次數（\\r\\n 算一個，不重複計入其中的 \\n）。"""
    if not text:
        return 0
    return text.count(LITERAL_CRLF) + text.replace(LITERAL_CRLF, "").count(LITERAL_LF)


def looks_escaped(text: str) -> bool:
    """是否判定為「caller escaping 失敗」。判準見模組 docstring。"""
    if not text:
        return False
    return count_literal(text) >= MIN_HITS and text.count("\n") <= MAX_REAL_LF


def normalize(text: str) -> tuple[str, bool]:
    """回 (可能已修的文字, 是否動過)。不命中則原樣回。"""
    if not looks_escaped(text):
        return text, False
    # 順序重要：先 \r\n 再 \n，否則先換 \n 會把 \r\n 拆成孤立 \r
    fixed = text.replace(LITERAL_CRLF, "\n").replace(LITERAL_LF, "\n")
    return fixed, True


HINT = ("換行是字面 \"\\n\"（CLI 參數不會自動解讀）→ 已轉成真換行。"
        "下次直接傳真換行（bash 用單引號 heredoc）可免這層修正。")
