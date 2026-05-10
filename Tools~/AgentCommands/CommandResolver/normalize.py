"""T-LVC-002 — Input normalization layer.

職責：把使用者輸入做統一化處理，讓後續 substring / vector 比對不被大小寫、
全半角、標點等表面差異干擾。

Pipeline:
  raw input → NFKC unicode → lowercase → strip 標點 → 同義詞替換 → output
"""
from __future__ import annotations
import json
import os
import re
import unicodedata
from typing import Dict, Optional

# 區塊職責：標點剝除 regex
# 物理意義：保留 [\w一-鿿] (英數+中日韓) + emoji 範圍 (U+1F300-1FAFF / U+2600-27BF)，其他剝除
# 數值影響：'!' / '?' / '。' / '，' 等被剝除；emoji（📥 / ✨ / 🍕）保留為觸發詞
_PUNCT_RE = re.compile(
    r"[^\w一-鿿　-〿\U0001F300-\U0001FAFF☀-➿⌀-⏿]+",
    re.UNICODE,
)
_MULTI_WS_RE = re.compile(r"\s+", re.UNICODE)


def load_synonyms(synonyms_path: Optional[str] = None) -> Dict[str, str]:
    """載入 synonyms.json — 失敗時回空 dict（fail-open，不擋主流程）."""
    if synonyms_path is None:
        synonyms_path = os.path.join(
            os.path.dirname(__file__), "synonyms.json"
        )
    if not os.path.exists(synonyms_path):
        return {}
    try:
        with open(synonyms_path, "r", encoding="utf-8") as f:
            data = json.load(f)
        return data.get("mappings", {})
    except Exception:
        return {}


def apply_synonyms(text: str, mappings: Dict[str, str]) -> str:
    """同義詞 substring 替換。

    區塊職責：把 key 全部替換為 value（簡單 str.replace，不走 word boundary）
    物理意義：例如 "保存" → "儲存"，讓 vocab 收斂
    數值影響：替換後字串可能變長/變短；不影響 NFKC normalize
    安全：mappings 為空 → 直接 return text
    """
    if not mappings:
        return text
    out = text
    # 按 key 長度由長到短排序 — 避免短 key 提前吃掉長 key 的字
    for key in sorted(mappings.keys(), key=len, reverse=True):
        out = out.replace(key, mappings[key])
    return out


def normalize(
    text: str,
    synonyms: Optional[Dict[str, str]] = None,
    keep_whitespace: bool = False,
) -> str:
    """主 entry — 對輸入做完整 normalize pipeline.

    Args:
      text: 原始輸入
      synonyms: 同義詞表（None = 自動從 synonyms.json 載入；{} = 不替換）
      keep_whitespace: True 保留空白；False 剝光（適合 char-ngram TF-IDF）

    Returns:
      normalized 字串
    """
    if text is None:
        return ""

    # Step 1: NFKC — 全形變半形、組合字元統一
    s = unicodedata.normalize("NFKC", text)

    # Step 2: lowercase — 英數統一
    s = s.lower()

    # Step 3: 同義詞替換
    if synonyms is None:
        synonyms = load_synonyms()
    s = apply_synonyms(s, synonyms)

    # Step 4: 剝標點（替換為空白）
    s = _PUNCT_RE.sub(" ", s)

    # Step 5: collapse 多空白
    s = _MULTI_WS_RE.sub(" ", s).strip()

    if not keep_whitespace:
        s = s.replace(" ", "")

    return s


# ─────────────────────────────────────────────────────────────────
# 自我測試 — 跑 `python normalize.py` 即可驗收 T-LVC-002 DOD
# ─────────────────────────────────────────────────────────────────

def _self_test() -> int:
    """9 條 unit test — 對應 T-LVC-002 DOD."""
    # Windows cp950 console safety — 強制 stdout UTF-8
    import sys
    if hasattr(sys.stdout, "reconfigure"):
        try:
            sys.stdout.reconfigure(encoding="utf-8")
        except Exception:
            pass
    cases = [
        # (input, expected, description)
        ("Token", "token", "ASCII lowercase"),
        ("ＴＯＫＥＮ", "token", "Full-width → ASCII lowercase"),
        ("token", "token", "Already normalized"),
        ("Hello, World!", "helloworld", "Punctuation strip"),
        ("進酒館", "進酒館", "Chinese passthrough"),
        ("進入  聊天酒館", "進入聊天酒館", "Multi whitespace collapse"),
        ("Commit 一下！", "commit一下", "Mixed CJK + ASCII + punct"),
        ("@claude 你好", "claude你好", "@ symbol stripped"),
        ("CS0103: 找不到", "cs0103找不到", "Compile error code preserved"),
    ]
    fail = 0
    for inp, expected, desc in cases:
        actual = normalize(inp, synonyms={})
        ok = actual == expected
        if not ok:
            fail += 1
        marker = "✅" if ok else "❌"
        print(f"  {marker} {desc:35s} | '{inp}' → '{actual}' (expected '{expected}')")

    # 同義詞測試
    syns = {"Token": "token", "保存": "儲存"}
    actual = normalize("保存設定", synonyms=syns)
    ok = actual == "儲存設定"
    if not ok:
        fail += 1
    marker = "✅" if ok else "❌"
    print(f"  {marker} {'Synonym replacement':35s} | '保存設定' → '{actual}' (expected '儲存設定')")

    print(f"\n{'PASS' if fail == 0 else 'FAIL'}: {len(cases) + 1 - fail}/{len(cases) + 1}")
    return 0 if fail == 0 else 1


if __name__ == "__main__":
    import sys
    sys.exit(_self_test())
