#!/usr/bin/env python3
"""library.py — ⛔ 已全面退場。**指路 stub，零功能、零副作用**（一律 exit 2）。

# 區塊職責：把走舊入口的人擋下，並指到現行入口。
# 物理意義：本檔不再讀寫任何檔、不再解析任何子指令；`--help` 也只印對照表。
# 數值影響：**零副作用** —— 不建檔、不寫 JSON、不動帳、不發文、不推單。

⭐ **落點沒有變**：現行入口寫的是同一份舊 store（`BookNotes/<slug>/book.json`
  ＋ `chapters/`／`arcs/`）⇒ 換入口**不搬資料**，舊書照常讀得到。
⚠ 而**閱讀線**已經在新 store（`BookNotes/Library/` 的 work → media → reader），
  兩者是**兩份資料**：⛔ 別把同名的 op 當成同一件事
  （舊 `bookmark` 動的是 `BookNotes/<slug>/book.json`，
   `run Library --arg op=bookmark` 動的是 `Library/media/<id>/readers/<persona>/reader.json`）。

⛔ 為什麼不留一條「還能跑」的備援路：這支曾經同時是寫入端與回讀端，
  而它的回讀跟寫入同源 ⇒ 寫錯樹的時候四層全綠（TASK-0126 的血證）。
  兩個入口各自寫一次的成本，比「少一條舊路」高得多。
"""
import sys

# 舊子指令 → 現行入口。⚠ 指路牌上放**逐支對照**，不是一句「去讀文件」——
#   走到這裡的人手上就是一行舊指令，他要的是「我這一行怎麼改」。
ROUTES = [
    ("add-book / log-chapter / arc",
     "senate cmd book --arg data_root=<AgentCommands> --arg op=add|log-chapter|arc"),
    ("writing（列出寫到一半的書）",
     "senate cmd book --arg data_root=<AgentCommands> --arg op=writing"),
    ("donate / publish / tip / tips / donations",
     "senate ucmd run Books --arg op=donate|publish|tip"),
    ("export-watch / list-untitled",
     "senate cmd watch --arg data_root=<AgentCommands> --arg op=export|untitled"),
    ("bookmark / add-character / revise-view / show-book / resume（**閱讀線**）",
     "senate ucmd run Library --arg op=bookmark|add_character|revise_view|paths|recall"),
    ("shelf / shelf-update / list（書架與清單）",
     "閱讀卡：letters/<persona>/bookshelf/<media-id>.md（由 run Library 同步）"),
    ("terms / add-term / review / reviews / volumes / add-volume / tag / "
     "recommend / recommendations / branches / search / prepare / stt-prompt / set-name-original",
     "⛔ 沒有平替，也**沒有資料**（2026-09-10 實測：舊 store 6 份 book.json 只有 13 個鍵，"
     "這些欄位零檔零鍵）⇒ 隨本檔一併退場"),
]

DOCS = [
    "ucl_core:Docs~/{lang}/Workflows/Reading_Library_Workflow.md   （閱讀線）",
    "ucl_core:Docs~/{lang}/Workflows/Book_Writing_Workflow.md      （寫書線／入庫／金流）",
    "ucl_core:Skills~/reading-library/SKILL.md                     （判準）",
]


def main() -> int:
    print("⛔ library.py 已全面退場 —— 本檔不再有任何功能（零副作用，一律 exit 2）。",
          file=sys.stderr)
    if len(sys.argv) > 1:
        print(f"·  你打的是：{' '.join(sys.argv[1:])}", file=sys.stderr)
    print("", file=sys.stderr)
    print("舊子指令 → 現行入口：", file=sys.stderr)
    for old, new in ROUTES:
        print(f"  · {old}\n      ⇒ {new}", file=sys.stderr)
    print("", file=sys.stderr)
    print("⚠ 舊 store 與新 store 是**兩份資料**，同名的 op 不是同一件事 —— 見各入口的說明。",
          file=sys.stderr)
    print("", file=sys.stderr)
    print("文件：", file=sys.stderr)
    for d in DOCS:
        print(f"  · {d}", file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main())
