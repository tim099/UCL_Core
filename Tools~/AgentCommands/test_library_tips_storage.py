#!/usr/bin/env python3
"""
test_library_tips_storage.py — T-BOOKS-STORAGE Phase A 回歸測試（tips per-entry folder）

跑法：python CardGame/Assets/UCL/UCL_Core/Tools~/AgentCommands/test_library_tips_storage.py
（純 stdlib、不碰真實 Books；monkeypatch _books_root 指向 temp dir）

驗的不變式：
  - 每筆 tip 寫成獨立檔（append-only、不編共享聚合檔 → 免併發衝突）
  - 檔名 = <stamp>_<tipper_persona>_<tip_id>.json（Tim 拍板法）
  - 同 tip_id 重寫（--retry 補券）冪等：仍 1 檔、內容更新、不新增
  - _load_tips glob 聚合回舊形狀 {"tips":[...]}；_tip_totals_by_book 加總正確
"""
import importlib.util
import pathlib
import tempfile
import sys

_HERE = pathlib.Path(__file__).resolve().parent
_spec = importlib.util.spec_from_file_location("library_under_test", str(_HERE / "library.py"))
lib = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(lib)

_failures = []


def _check(cond, label):
    print(("  PASS " if cond else "  FAIL ") + label)
    if not cond:
        _failures.append(label)


def _entry(tip_id, persona, book, tokens, status="issued"):
    return {"book": book, "title": book, "tipper": "bank-x", "tipper_persona": persona,
            "beneficiary": "bank-y", "beneficiary_persona": "author", "tokens_spent": tokens,
            "vouchers": {"canvas": tokens, "tavern": tokens}, "tip_id": tip_id,
            "voucher_status": status, "note": "", "tipped_at": "2026-07-17"}


def main():
    print("=== T-BOOKS-STORAGE Phase A 回歸測試（tips folder）===")
    tmp = pathlib.Path(tempfile.mkdtemp(prefix="tips_test_"))
    lib._books_root = lambda: tmp   # monkeypatch：假 Books root

    print("[1] 每筆獨立檔 + 檔名格式 + glob 讀回")
    lib._write_tip(_entry("aaa11111", "kotoko", "book-a", 5))
    lib._write_tip(_entry("bbb22222", "crest-001", "book-b", 2))
    files = sorted(p.name for p in (tmp / "tips").glob("*.json"))
    _check(len(files) == 2, f"2 筆 = 2 個獨立檔（實得 {len(files)}）")
    _check(all(f.endswith("_aaa11111.json") or f.endswith("_bbb22222.json") for f in files),
           "檔名以 _<tip_id>.json 結尾")
    _check(any("_kotoko_" in f for f in files) and any("_crest-001_" in f for f in files),
           "檔名含 tipper_persona（Tim 拍板法 時間_persona_tipid）")
    loaded = lib._load_tips().get("tips", [])
    _check(len(loaded) == 2, "_load_tips glob 讀回 2 筆")
    _check({t["tip_id"] for t in loaded} == {"aaa11111", "bbb22222"}, "讀回 tip_id 正確")

    print("[2] 同 tip_id 冪等（--retry 補券覆寫、不新增檔）")
    lib._write_tip(_entry("aaa11111", "kotoko", "book-a", 5, status="pending_tavern"))
    files2 = list((tmp / "tips").glob("*.json"))
    _check(len(files2) == 2, f"重寫同 tip_id 後仍 2 檔、不新增（實得 {len(files2)}）")
    rel = {t["tip_id"]: t for t in lib._load_tips().get("tips", [])}
    _check(rel["aaa11111"]["voucher_status"] == "pending_tavern", "同 tip_id 內容被更新（voucher_status）")

    print("[3] explicit stamp（migration 用原 tipped_at）")
    lib._write_tip(_entry("ccc33333", "summit", "book-c", 3), stamp="20260101")
    got = [p.name for p in (tmp / "tips").glob("20260101_*_ccc33333.json")]
    _check(len(got) == 1, f"explicit stamp 反映在檔名（實得 {got}）")

    print("[4] _tip_totals_by_book 加總")
    totals = lib._tip_totals_by_book()
    _check(totals.get("book-a") == (5, 1), f"book-a = (5 token, 1 筆)（實得 {totals.get('book-a')}）")
    _check(totals.get("book-c") == (3, 1), f"book-c = (3 token, 1 筆)（實得 {totals.get('book-c')}）")

    print("[5] 空 tips 資料夾 → {'tips': []}（不炸）")
    tmp2 = pathlib.Path(tempfile.mkdtemp(prefix="tips_empty_"))
    lib._books_root = lambda: tmp2
    _check(lib._load_tips() == {"tips": []}, "無 tips/ 目錄回空 list")

    print("[6] donations derive-from-per-book（Phase B）")
    tmp3 = pathlib.Path(tempfile.mkdtemp(prefix="don_test_"))
    lib._books_root = lambda: tmp3
    import json as _json
    for slug, don in [("book-a", {"book": "book-a", "title": "A", "donor_persona": "kotoko", "source": "authored", "tokens": 0}),
                      ("book-b", {"book": "book-b", "title": "B", "donor_persona": "summit", "source": "donated", "tokens": 50}),
                      ("book-c", {"title": "C (無 book 欄位)", "donor_persona": "ame", "source": "authored"})]:
        bd = tmp3 / slug
        bd.mkdir(parents=True, exist_ok=True)
        (bd / "_donation.json").write_text(_json.dumps(don, ensure_ascii=False), encoding="utf-8")
    ds = lib._load_donations().get("donations", [])
    _check(len(ds) == 3, f"glob 3 本 per-book _donation.json（實得 {len(ds)}）")
    _check({d.get("book") for d in ds} == {"book-a", "book-b", "book-c"},
           "缺 book 欄位的用資料夾名兜底（book-c）")
    ben = lib._resolve_beneficiary("book-b")
    _check(ben is not None and ben[1] == "summit", f"_resolve_beneficiary derive 正確（book-b → summit，實得 {ben}）")
    lib._books_root = lambda: pathlib.Path(tempfile.mkdtemp(prefix="don_empty_"))
    _check(lib._load_donations() == {"donations": []}, "無任何 per-book 檔回空 list")

    print()
    if _failures:
        print(f"❌ {len(_failures)} 個斷言失敗：")
        for f in _failures:
            print("   - " + f)
        return 1
    print("✅ 全部通過")
    return 0


if __name__ == "__main__":
    sys.exit(main())
