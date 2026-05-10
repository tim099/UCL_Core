"""T-LVC-001 — Parse CommandTable.md → triggers.json.

職責：把 CommandTable.md 的 Markdown entry 結構抽取成 agent 可機讀的 JSON。

Parser 規則:
  - `## 1. Entries` 開始 → `## 2.` 結束
  - 每個 `### <name>` heading 為一筆 entry
  - 觸發詞抽取：找 `**觸發詞**` 開頭的 bullet（含後續縮排子 bullet）
    - 內含 `\`...\`` 反引號的字串為 phrase
  - workflow 抽取：找 `**對應 Workflow**` bullet
    - `[<label>](<url>)` 內的 url 為 workflow_path
  - 意圖：找 `**意圖**` bullet
"""
from __future__ import annotations
import json
import os
import re
import sys
import unicodedata
from datetime import datetime, timezone
from typing import List, Dict, Any, Optional


# 區塊職責：定位 CommandTable.md 路徑（相對本檔）
# 物理意義：本檔在 Tools~/AgentCommands/CommandResolver/，CommandTable.md 在 Docs~/zh-Hant/
# 數值影響：路徑解析錯 → 直接 abort
_HERE = os.path.dirname(os.path.abspath(__file__))
_UCL_CORE_ROOT = os.path.abspath(os.path.join(_HERE, "..", "..", ".."))
DEFAULT_COMMAND_TABLE = os.path.join(
    _UCL_CORE_ROOT, "Docs~", "zh-Hant", "CommandTable.md"
)
DEFAULT_OUTPUT = os.path.join(_HERE, "triggers.json")


# 區塊職責：phrase 抽取 regex
# 物理意義：抓 `\`X\`` 反引號內字串；非貪婪
_PHRASE_RE = re.compile(r"`([^`]+)`")

# 區塊職責：workflow link 抽取 regex
# 物理意義：抓 [label](url) 的 url 部分
_LINK_RE = re.compile(r"\[([^\]]+)\]\(([^)]+)\)")

# entry heading: ### <name>
_ENTRY_HEADING_RE = re.compile(r"^###\s+(.+)$")

# 觸發詞 / 對應 Workflow / 意圖 bullet detector
_TRIGGER_BULLET_RE = re.compile(r"^\s*-\s*\*\*觸發詞\*\*", re.UNICODE)
_WORKFLOW_BULLET_RE = re.compile(r"^\s*-\s*\*\*對應\s*Workflow\*\*", re.UNICODE)
_INTENT_BULLET_RE = re.compile(r"^\s*-\s*\*\*意圖\*\*", re.UNICODE)
_NEW_BULLET_RE = re.compile(r"^\s*-\s*\*\*", re.UNICODE)  # 任一 bold bullet 開頭
_INDENTED_BULLET_RE = re.compile(r"^\s+-\s", re.UNICODE)  # 縮排 sub-bullet


def _slugify(name: str) -> str:
    """把 entry 名稱轉成 entry_id slug.

    區塊職責：穩定 ID — 不含中英混合 / 標點變動就好
    物理意義：'進入聊天酒館' → 'enter_tavern' 這種對應靠人手在 CommandTable 加 entry_id 註解；
              此處 fallback 走 hash + 取前幾字
    數值影響：slug 必為合法 JSON key + 路徑友善
    安全：fallback 永不為空（若全是非英文字元用 NFKD 轉拼音失敗則用 _UNNAMED + counter）
    """
    # 取英文部分當主 slug
    s = unicodedata.normalize("NFKC", name).strip().lower()
    # 拿英文 + 數字 + 底線
    eng = re.sub(r"[^a-z0-9]+", "_", s)
    eng = eng.strip("_")
    if eng:
        return eng
    # 全中文 fallback — 取前 6 字元 + 後綴 hash
    import hashlib
    h = hashlib.md5(s.encode("utf-8")).hexdigest()[:6]
    return f"entry_{h}"


def _extract_phrases_from_lines(lines: List[str]) -> List[str]:
    """從 bullet 行群中抓所有 \\`...\\` phrase."""
    phrases: List[str] = []
    for line in lines:
        for m in _PHRASE_RE.findall(line):
            ph = m.strip()
            if ph and ph not in phrases:
                phrases.append(ph)
    return phrases


def _extract_workflow(line: str) -> Optional[str]:
    """從 workflow bullet 抓 url."""
    m = _LINK_RE.search(line)
    if m:
        return m.group(2)
    return None


def _extract_intent(line: str) -> str:
    """從 意圖 bullet 抓冒號後文字."""
    # `- **意圖**: <text>` 或 `- **意圖**：<text>`
    parts = re.split(r"[:：]", line, maxsplit=1)
    if len(parts) == 2:
        return parts[1].strip()
    return ""


def _detect_lang(phrases: List[str]) -> List[str]:
    """看 phrases 推語言標籤."""
    has_zh = any(re.search(r"[一-鿿]", p) for p in phrases)
    has_en = any(re.search(r"[a-zA-Z]", p) for p in phrases)
    out = []
    if has_zh:
        out.append("zh")
    if has_en:
        out.append("en")
    return out or ["zh"]


def parse_command_table(md_path: str) -> List[Dict[str, Any]]:
    """主 parser — 讀 CommandTable.md → entry list.

    區塊職責：state machine — track 是否在 Entries section / 當前 entry / 當前 bullet group
    """
    with open(md_path, "r", encoding="utf-8") as f:
        lines = f.readlines()

    entries: List[Dict[str, Any]] = []
    in_entries_section = False
    current: Optional[Dict[str, Any]] = None
    # bullet group buffer：when 累積觸發詞 bullet 含 sub-bullet 時用
    trigger_buffer: List[str] = []
    workflow_buffer: List[str] = []
    intent_buffer: List[str] = []
    current_section: Optional[str] = None  # "trigger" | "workflow" | "intent" | None

    def flush_current():
        nonlocal current, trigger_buffer, workflow_buffer, intent_buffer, current_section
        if current is None:
            return
        # 處理 buffer
        phrases = _extract_phrases_from_lines(trigger_buffer)
        workflow = ""
        for line in workflow_buffer:
            url = _extract_workflow(line)
            if url:
                workflow = url
                break
        intent = ""
        for line in intent_buffer:
            t = _extract_intent(line)
            if t:
                intent = t
                break

        current["phrases"] = phrases
        current["workflow_path"] = workflow
        current["intent"] = intent
        current["lang"] = _detect_lang(phrases)
        # 只收錄至少有一個 phrase 的 entry — 純說明區段不算
        if phrases:
            entries.append(current)
        current = None
        trigger_buffer = []
        workflow_buffer = []
        intent_buffer = []
        current_section = None

    for line in lines:
        stripped = line.rstrip("\n")

        # Section 進入偵測
        if re.match(r"^##\s+1\.\s", stripped):
            in_entries_section = True
            continue
        if re.match(r"^##\s+[2-9]\.\s", stripped) or re.match(r"^##\s+\d{2,}\.\s", stripped):
            # 進入下一個 section → 收尾
            flush_current()
            in_entries_section = False
            continue

        if not in_entries_section:
            continue

        # Entry heading
        m = _ENTRY_HEADING_RE.match(stripped)
        if m:
            flush_current()
            name = m.group(1).strip()
            # 簡化 name — 移除括號註解
            display_name = re.sub(r"[（(].*?[)）]", "", name).strip()
            current = {
                "entry_id": _slugify(display_name),
                "display_name": display_name,
                "tags": [],
            }
            continue

        if current is None:
            continue

        # 偵測 bullet 種類
        if _TRIGGER_BULLET_RE.match(stripped):
            current_section = "trigger"
            trigger_buffer.append(stripped)
            continue
        if _WORKFLOW_BULLET_RE.match(stripped):
            current_section = "workflow"
            workflow_buffer.append(stripped)
            continue
        if _INTENT_BULLET_RE.match(stripped):
            current_section = "intent"
            intent_buffer.append(stripped)
            continue

        # sub-bullet 處理 — 縮排或非 bold bullet 視為當前 section 的延續
        if _INDENTED_BULLET_RE.match(stripped):
            if current_section == "trigger":
                trigger_buffer.append(stripped)
            elif current_section == "workflow":
                workflow_buffer.append(stripped)
            elif current_section == "intent":
                intent_buffer.append(stripped)
            continue

        # 遇到新 bold bullet（非觸發詞/workflow/意圖）→ 結束當前 section
        if _NEW_BULLET_RE.match(stripped):
            current_section = None
            continue

        # 空行 / 其他 → 不打斷 section（避免吞掉空白行後的 sub-bullet）
        if stripped == "":
            continue

    # 檔尾收尾
    flush_current()
    return entries


def write_triggers_json(entries: List[Dict[str, Any]], output_path: str, source_path: str) -> None:
    """把解析結果寫成 triggers.json."""
    data = {
        "_schema_version": 1,
        "_synced_from": os.path.relpath(source_path).replace(os.sep, "/"),
        "_synced_at": datetime.now(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z"),
        "_synced_by": "sync_command_table.py",
        "_entry_count": len(entries),
        "triggers": entries,
    }
    with open(output_path, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)


def main(argv: Optional[List[str]] = None) -> int:
    """CLI entry."""
    # Windows console UTF-8
    if hasattr(sys.stdout, "reconfigure"):
        try:
            sys.stdout.reconfigure(encoding="utf-8")
        except Exception:
            pass

    src = DEFAULT_COMMAND_TABLE
    out = DEFAULT_OUTPUT
    args = argv or sys.argv[1:]
    if "--src" in args:
        i = args.index("--src")
        src = args[i + 1]
    if "--out" in args:
        i = args.index("--out")
        out = args[i + 1]

    if not os.path.exists(src):
        print(f"❌ CommandTable not found: {src}")
        return 1

    print(f"📖 Parsing: {src}")
    entries = parse_command_table(src)
    print(f"✅ Parsed {len(entries)} entries")

    for e in entries:
        print(f"  - {e['entry_id']:30s} ({len(e['phrases']):2d} phrases) → {e['workflow_path'] or '(no workflow)'}")

    write_triggers_json(entries, out, src)
    print(f"💾 Wrote: {out}")

    if len(entries) < 5:
        print(f"⚠️  Only {len(entries)} entries — DOD 要求 ≥ 5，可能 parser 漏東西")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
