#!/usr/bin/env python3
"""HelpURL 死連結對帳 — 掃 C# 裡的 HelpURL，檢查指向的文件是否真的存在。

為什麼需要這支工具（2026-08-17，Tim 報「說明按鈕沒反應」而起）:
  HelpURL 指向不存在的檔時，整條鏈路是**四層 fail-soft 疊起來、完全不會叫**:
    {lang} 找不到 → en 回退也不存在 → 維持原路徑
    → Application.OpenURL(不存在的路徑) → OS shell 靜默無視、不丟例外不寫 log
  每一層單獨看都合理，但沒有任何一層負責說「我找不到」。
  結果是死連結只有「剛好有人按到」才會被發現，而按到的人看到的是「按鈕壞了」——
  那跟「功能壞了」長得一模一樣（Tim 這次就是這樣讀的，完全合理）。
  ⇒ 讓它自己叫，就不必靠下一個人剛好按到。

🩸 掃描器本身踩過的兩格（本檔的兩個修正各自對應一格，缺一不可）:
  ① **註解裡的範例會被算成真的宣告** — 文件註解常寫 `[HelpURL("ucl_core:...")]` 當說明，
     不排除的話會去補兩份根本不存在的文件（summit 2026-08-17 的假陽性）。
  ② **逐行掃會漏掉跨行宣告** — Cmd 系列慣例寫成:
         public override string HelpURL =>
             "ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_X.md";
     字串在下一行，逐行 regex 抓不到。實測漏掉 13 條（kiara 2026-08-17，
     為了修①而改逐行掃，數字從 22 掉到 10，差點照著少一半的清單回報）。
  ⇒ 正解是**先拔註解（區塊 + 行）再全文比對**，兩個修正同時到位才對。
  兩次都是同一種壞法: **掃描器的視野決定了世界的大小，而它不會報錯** ——
  只會給你一個看起來很整齊的數字。

用法:
  python <UCL_Core>/Tools~/AgentCommands/helpurl_check.py            # 列出斷連
  python <UCL_Core>/Tools~/AgentCommands/helpurl_check.py --all      # 連正常的也列
  python <UCL_Core>/Tools~/AgentCommands/helpurl_check.py --strict   # 有斷連就 exit 1（CI / 必經路徑用）
"""
import argparse
import re
import sys
from pathlib import Path

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

# 區塊職責: UCL_Core 根目錄解析（不寫死安裝路徑 — 各專案掛載位置不同）
# 物理意義: 本檔位於 <UCL_Core>/Tools~/AgentCommands/，上推兩層即 core 根。
_CORE_ROOT = Path(__file__).resolve().parent.parent.parent

# 語系回退順序: 任一存在即算通過（對齊 UCL_URL 的 {lang} → en 回退）
LANGS = ["zh-Hant", "en", "ja", "zh-Hans"]

# 兩種宣告形式都要抓，且允許 "=>" 與字串之間換行（見檔頭 🩸 ②）
HELPURL_RE = re.compile(r'HelpURL\s*(?:\(|=>)\s*\n?\s*"(ucl_core|repo):([^"]+)"')


def strip_comments(src: str) -> str:
    """拔掉區塊註解與行註解（見檔頭 🩸 ①）。

    刻意不做完整的 C# 詞法分析: 字串裡含 "//" 的極少數情形被誤拔，
    造成的是**漏報一條**（保守），而不是憑空生出一條要人去補的假文件。
    """
    src = re.sub(r"/\*.*?\*/", "", src, flags=re.S)
    return "\n".join(re.sub(r"//.*$", "", ln) for ln in src.splitlines())


def resolve_exists(prefix: str, rel: str):
    """回 (是否存在, 檢查過的路徑清單)。{lang} 展開四語系，任一存在即通過。"""
    base = _CORE_ROOT if prefix == "ucl_core" else _CORE_ROOT.parent
    cands = [rel.replace("{lang}", lg) for lg in LANGS] if "{lang}" in rel else [rel]
    paths = [base / c for c in cands]
    return any(p.is_file() for p in paths), paths


def scan():
    rows = []
    for cs in sorted(_CORE_ROOT.rglob("*.cs")):
        try:
            raw = cs.read_text(encoding="utf-8", errors="replace")
        except OSError:
            continue
        clean = strip_comments(raw)
        for m in HELPURL_RE.finditer(clean):
            prefix, rel = m.group(1), m.group(2)
            # 行號以拔註解後的文字計算 — 與原檔可能差幾行，只作定位參考
            line = clean[: m.start()].count("\n") + 1
            ok, paths = resolve_exists(prefix, rel)
            rows.append({
                "file": cs.relative_to(_CORE_ROOT).as_posix(),
                "line": line, "prefix": prefix, "rel": rel,
                "ok": ok, "checked": paths,
            })
    return rows


def main():
    ap = argparse.ArgumentParser(description="HelpURL 死連結對帳")
    ap.add_argument("--all", action="store_true", help="連指向正常的也列出")
    ap.add_argument("--strict", action="store_true", help="有斷連就 exit 1")
    args = ap.parse_args()

    rows = scan()
    broken = [r for r in rows if not r["ok"]]

    print(f"🔗 HelpURL 對帳 — 共 {len(rows)} 條宣告，斷連 {len(broken)} 條")
    print(f"   core root: {_CORE_ROOT}")
    print("=" * 68)

    if args.all:
        for r in rows:
            if r["ok"]:
                print(f"✅ {r['file']}:{r['line']}\n     → {r['prefix']}:{r['rel']}")

    if not broken:
        print("✅ 沒有斷連。")
        return 0

    # 同一份不存在的文件被多處引用時聚在一起 — 修一次就解決多條，優先序不同
    by_target = {}
    for r in broken:
        by_target.setdefault((r["prefix"], r["rel"]), []).append(r)
    for (prefix, rel), rs in sorted(by_target.items(), key=lambda kv: -len(kv[1])):
        multi = f"  ⚠ {len(rs)} 處共用同一目標（補一份文件可一次修好）" if len(rs) > 1 else ""
        print(f"\n❌ {prefix}:{rel}{multi}")
        for r in rs:
            print(f"     宣告於 {r['file']}:{r['line']}")
        print(f"     已檢查: {', '.join(str(p) for p in rs[0]['checked'])}")

    print("\n" + "=" * 68)
    print("修法二選一（先判斷是「沒寫」還是「被改名/搬走」— 後者只要改路徑，不必重寫）:")
    print("  a) 文件確實不存在 → 補一份（頁面文件寫『這頁怎麼用』，不要指向 Plan）")
    print("  b) 文件被搬走/改名 → 改 HelpURL 的路徑")
    return 1 if args.strict else 0


if __name__ == "__main__":
    sys.exit(main())
