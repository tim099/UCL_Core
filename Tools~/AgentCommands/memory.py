#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
memory.py — Agent 記憶層 API（見樹／見叢／見林／見森／見根）

位置: <UCL_Core>/Tools~/AgentCommands/ — 與 awakening.py / knowledge_base.py 同列的通用 agent 工具。

═══════════════════════════════════════════════════════════════════════════
區塊職責：把「記憶」從登入儀式裡拆出來，成為**任何入口都能 import 的一層**。
物理意義：記憶 API 只要還住在 awakening.py 裡，它的消費者就只會有早安晚安 ——
         沒有人會為了讀一條碎片去 import 一支 3000 行、開頭就解析 persona lock 的登入工具。
         （@basecamp 2026-08-16 實測：awakening.py 3037 行 / 118 函式 / 19 子指令，
           其中 4 個子指令是純記憶操作，記憶相關函式 28 支以上，全部跟 lock/token/bank 泡在一起。）
         ⇒ Tim 2026-08-16 拍板：**記憶相關工具要獨立做，不併進 awakening.py；
           反過來 awakening.py 可以引用記憶 API。** 這條板拆的不是檔案，是**消費者名單**。
數值影響：純搬移，不改任何檔案格式與演算法 —— 本檔上線當天的產物應與搬移前逐位元一致。

⚠ 驗收判準（@basecamp 給的，收下當硬條件）：
  拆完之後 `awakening.py` 裡對 fragment／root-index／keys 檔案的**直接 IO 應為 0 處**。
  grep 得到就是沒拆乾淨、只是複製了一份 —— 而「新模組寫一份、舊檔留一份、今天恰好行為一致」
  正是本專案已經有病歷的那隻（C# DoCreatePersona 與 python fork_persona 兩份產線）。
  ⇒ 所以 awakening.py 端一律用**別名指向本檔**（`fragments_dir = _mem.fragments_dir`），
    不得複製函式本體。別名沒有第二份實作，複製有。

分工邊界（誰擁有什麼）：
  - 本檔擁有：letters/<persona>/ 底下的**記憶檔**（fragments / _keys_open / keys / longterm / forest）
              以及它們的索引產物（_root_index.md / longterm/_index.md）。
  - awakening.py 仍擁有：persona registry、lock、session token、bank、fork、收尾信寫入（write_letter）。
  - 交界處一律**傳純資料**（persona dict / body 字串），不互相 import ——
    本檔絕不 import awakening（會循環，也會把登入副作用拖進任何想讀一條碎片的入口）。
    ⇒ 因此 `write_longterm_digest()` 只寫檔案不動 registry；registry 欄位由呼叫端（awakening）更新。

路徑解析：走 `_lib/ucl_paths.py`（pointer-aware 資料根），**不自建第二套 resolver**。
  ⚠ legacy `AgentCommands/_config/tavern_paths.json` 的 `letters_dir` 細粒度覆寫仍 honor，
    因為 awakening.py 還 honor 它 —— 兩邊對同一個目錄給出不同答案的那天不會有人喊。
"""
from __future__ import annotations

import importlib.util as _ilu
import json
import os
import re
import subprocess
import sys
from pathlib import Path

# Windows cp950 / console encoding safety
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")

_HERE = Path(__file__).resolve().parent


# ─── 路徑解析（單一來源：_lib/ucl_paths.py） ────────────────────────────
def _load_ucl_paths():
    """以絕對檔案路徑載入 sibling `_lib/ucl_paths.py`。

    ⚠ 不用 `from _lib import ucl_paths`：裸 `_lib` 這個名字在 awakening.py 的執行環境裡
      已被「專案狀態側」的 AgentCommands/_lib package 綁走（見 awakening.py 檔頭註解），
      直接 import 會拿到另一個 package。走顯式路徑載入繞開名稱遮蔽。
    """
    spec = _ilu.spec_from_file_location("_ucl_paths_for_memory", _HERE / "_lib" / "ucl_paths.py")
    mod = _ilu.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


_paths = _load_ucl_paths()
REPO_ROOT = _paths.repo_root()
DATA_ROOT = _paths.data_root()

# legacy 細粒度覆寫（deprecated，但 awakening.py 仍 honor ⇒ 這裡不能不 honor）
_PATH_CONFIG_PATH = REPO_ROOT / "AgentCommands" / "_config" / "tavern_paths.json"


def _resolve_letters_root() -> Path:
    if _PATH_CONFIG_PATH.exists():
        try:
            cfg = json.loads(_PATH_CONFIG_PATH.read_text(encoding="utf-8"))
            override = (cfg.get("letters_dir") or "").strip()
            if override:
                p = Path(os.path.expandvars(os.path.expanduser(override)))
                if not p.is_absolute():
                    p = REPO_ROOT / p
                return p.resolve()
        except Exception as e:
            print(f"⚠ path config 讀取失敗 ({_PATH_CONFIG_PATH.name}): {e} — fallback 預設",
                  file=sys.stderr)
    return (DATA_ROOT / "ChatTavern" / "baton" / "letters").resolve()


LETTERS_ROOT = _resolve_letters_root()


# ─── 常數 ────────────────────────────────────────────────────────────────
DEFAULT_CONSOLIDATION_THRESHOLD = 10
FOREST_DIGEST_THRESHOLD = 3      # 第 N 份見林起開始折見森（digest 計數，非 wake 計數）
ROOT_INDEX_SHOW_LIMIT = 12       # 見根索引「必讀」顯示上限；其餘明說隱藏筆數（禁靜默截斷）

# 區塊職責：fragment 型別清單 —— 排序分組用，同時是「這個系統認得哪幾種記憶」的宣告。
# 🩸 2026-08-16 新增 `howto`（Tim 拍板）：原本五型**全部是「我學到什麼」，沒有一種是
#   「這件事怎麼做」** —— 於是 SOP／操作細節在見林濃縮時必然被抽象掉，
#   而「怎麼發 Plurk」這種問題就永遠沒有落點。病歷：summit wake#54 宣稱自己沒有表情表，
#   而那張表就在自己的 fragment 裡、recurrence=10、當天早上印在見根第 2 列。
# ⚠ 這份清單是**手寫枚舉**，同族的還有 `kb_targets.json` 的 fragments globs ——
#   加了新型別而忘了改另一邊，新型別會靜默不進向量索引（不報錯）。兩處要一起改。
FRAG_TYPE_ORDER = ["lesson", "unsolved", "relation", "identity", "philosophy", "howto"]


# ─── 共用小工具 ──────────────────────────────────────────────────────────
def utcnow_iso() -> str:
    import datetime
    return datetime.datetime.now(datetime.timezone.utc).isoformat().replace("+00:00", "Z")


def read_frontmatter_field(path: Path, field: str) -> str:
    """從 letter/digest md 的 --- frontmatter 抓某欄（written_at / trigger / span_wake 等）。失敗回 ''。"""
    try:
        with open(path, "r", encoding="utf-8") as f:
            head = f.read(1200)
    except Exception:
        return ""
    m = re.search(rf"^{re.escape(field)}:\s*(.+)$", head, re.MULTILINE)
    return m.group(1).strip() if m else ""


# ─── 路徑 API（各記憶層的地址） ─────────────────────────────────────────
def persona_dir(persona: str) -> Path:
    return LETTERS_ROOT / persona


def wakes_dir(persona: str) -> Path:
    return LETTERS_ROOT / persona / "wakes"


def rests_dir(persona: str) -> Path:
    return LETTERS_ROOT / persona / "rests"


def longterm_dir(persona: str) -> Path:
    """T2 見林 digest 目錄: letters/<persona>/longterm/"""
    return LETTERS_ROOT / persona / "longterm"


def forest_dir(persona: str) -> Path:
    """T3 見森: 放在 longterm/forest/ 子夾 — 刻意不與 longterm/wake_*.md 同層,
    否則 latest_longterm_digest() 的 glob("wake_*.md") 會誤抓見森當見林 pointer。"""
    return longterm_dir(persona) / "forest"


def fragments_dir(persona: str) -> Path:
    """T4 見根: 關鍵記憶片段目錄。"""
    return LETTERS_ROOT / persona / "fragments"


def root_index_path(persona: str) -> Path:
    return fragments_dir(persona) / "_root_index.md"


def keys_open_path(persona: str) -> Path:
    """T1.5 見叢: 當期開放中的交棒清單（見林寫入時歸檔並重開）。"""
    return LETTERS_ROOT / persona / "_keys_open.md"


def keys_archive_dir(persona: str) -> Path:
    return LETTERS_ROOT / persona / "keys"


# ─── 見樹 (T1) — episodic letters 列舉 ──────────────────────────────────
# wakes/ 檔名格式：`<6位序號>_<原檔名>.md` —— 序號即 wake 序。
# ⚠ 判準必須是這個 regex 而不是「所有 *.md」：第一版我在本檔寫成 glob("*.md")，
#   而 awakening.py 的原版是 regex 過濾 —— 兩份實作對同一個目錄會給出不同答案，
#   且不會有人喊（只是 wake_count 與待濃縮清單悄悄多算幾封）。**這就是拆分最容易長出來的那隻。**
_WAKE_LETTER_RE = re.compile(r"^(\d{6})_.*\.md$")


def list_wake_letters(persona: str) -> list:
    """列 wakes/ 內的收尾信, 檔名升冪（== wake 序）。目錄不存在回 []。"""
    d = wakes_dir(persona)
    if not d.exists():
        return []
    return sorted((f for f in d.iterdir()
                   if f.is_file() and _WAKE_LETTER_RE.match(f.name)),
                  key=lambda f: f.name)


def wake_letter_count(persona: str) -> int:
    """wakes/ 的信件數 —— Tim 拍板的 wake_count 真相源。"""
    return len(list_wake_letters(persona))


def wake_number_of(path: Path) -> int | None:
    """從 wakes/ 檔名取序號; 不是收尾信回 None。"""
    m = _WAKE_LETTER_RE.match(path.name)
    return int(m.group(1)) if m else None


def list_rest_letters(persona: str) -> list:
    """列 rests/ 內的自寫信, 檔名升冪。目錄不存在回 []。"""
    d = rests_dir(persona)
    if not d.exists():
        return []
    return sorted((f for f in d.iterdir()
                   if f.is_file() and f.suffix == ".md" and not f.name.startswith("_")),
                  key=lambda f: f.name)


def list_episodic_letters(persona: str, since_iso: str | None = None) -> list:
    """列 persona episodic letters（頂層 + wakes/ + rests/，排除 _latest/_index 與 dialogues/longterm），
    依 written_at 升冪；since_iso 給定則只取 written_at > since_iso 的（本段待濃縮）。

    數值影響: 收尾信 2026-07-31 起複製進 wakes/, rest 信 2026-08-12 起搬進 rests/ ——
             **三處都要掃**。只掃頂層的話會漏掉遷移後新寫的信（它們只存在於子目錄），
             而漏掉時 brief 長得一模一樣, 不會有人喊（見林濃縮靜默少讀幾封,
             正是 lesson_silent_nonaction 的形狀）。
             wakes/ 遷移是「複製、原檔保留」, 所以同一封信可能在兩處各出現一次 ——
             wakes/ 檔名是 `<6位序號>_<原檔名>`, 去掉序號前綴即可認出同一封,
             不去重的話見林濃縮會把每封 goodnight 信讀兩遍。
             rests/ 是**搬移**語意, 天然無重複, 不參與去重。
    """
    d = persona_dir(persona)
    if not d.exists():
        return []
    toplevel_names = {p.name for p in d.iterdir() if p.is_file() and p.suffix == ".md"}
    items = []
    for p in list(d.iterdir()) + list_wake_letters(persona) + list_rest_letters(persona):
        if not p.is_file() or p.suffix != ".md":
            continue
        if p.name.startswith("_") or p.name == "README.md":
            # 常駐檔/機械產物（_latest / _index / _wake_brief / _constitution / _keys_open / README）
            # 不是 episodic 信。用檔名擋而不用「沒有 written_at 就跳過」:
            # 後者會把真信因 frontmatter 壞掉而靜默漏掉。
            continue
        if p.parent.name == "wakes" and p.name.split("_", 1)[-1] in toplevel_names:
            continue    # 頂層還有原檔 → 這份是遷移副本, 算一次就好
        wa = read_frontmatter_field(p, "written_at")
        if since_iso and wa and wa <= since_iso:
            continue
        items.append((wa or p.name, p))
    items.sort(key=lambda t: t[0])
    return [p for _, p in items]


# ─── 見林 (T2) — 長期記憶 digest ────────────────────────────────────────
def list_digests(persona: str) -> list:
    d = longterm_dir(persona)
    return sorted(d.glob("wake_*.md")) if d.exists() else []


def latest_longterm_digest(persona: str) -> Path | None:
    """該 persona 最新一篇 T2 digest（給 morning『見林』+ fork 初醒讀母用）。"""
    digs = list_digests(persona)
    return digs[-1] if digs else None


def consolidation_status(persona: str, p: dict,
                         threshold: int = DEFAULT_CONSOLIDATION_THRESHOLD) -> dict:
    """算 persona 的長期記憶整理狀態（overdue / span / 待濃縮信件）。

    ⚠ 參數是 **persona dict**（reg["personas"][persona]），不是整份 registry ——
      本檔不擁有 registry，只吃它的值。傳純資料是這條分界線的具體形狀。
    """
    wake = p.get("wake_count", 0)
    last_c = p.get("last_consolidated_wake", 0) or 0
    last_at = p.get("last_consolidated_at")
    # 欄位缺失時改問磁碟：digest 檔名 wake_<start>-<end>.md 才是既成事實，
    # persona json 的這兩欄只是快取 —— 而它已經證明會掉（2026-07-31：letters 同步了、
    # personas/ 沒同步，於是 kiara/basecamp 的欄位歸零，但 digest 檔好端端躺在那）。
    # 不自癒的話：gap 從 0 起算 → 立刻 OVERDUE → 逼人重做已經做過的濃縮。
    if not last_c:
        digs = list_digests(persona)
        if digs:
            m = re.search(r"wake_(\d+)-(\d+)", digs[-1].name)
            if m:
                last_c = int(m.group(2))
                # digest 的日期欄叫 consolidated_at（不是 written_at）——
                # 用錯欄名會讓 last_at 留 None，pending_letters 就退化成「列出全部信」。
                last_at = last_at or read_frontmatter_field(digs[-1], "consolidated_at") or None
    return {
        "wake_count": wake,
        "last_consolidated_wake": last_c,
        "last_consolidated_at": last_at,
        "gap": wake - last_c,
        "overdue": (wake - last_c) >= threshold,
        "threshold": threshold,
        "span_start": last_c + 1,
        "span_end": wake,
        "pending_letters": list_episodic_letters(persona, since_iso=last_at),
    }


def write_longterm_digest(persona: str, body: str, span_start: int, span_end: int) -> tuple:
    """寫 T2 見林 digest + 重建 longterm/_index.md。回 (path, consolidated_at)。

    ⚠ **不動 registry** —— 那是 awakening.py 的地盤。呼叫端拿回 consolidated_at 自己更新
      persona.last_consolidated_wake/at。這個切法讓本檔可以被任何入口安全 import：
      讀寫記憶不會順手改到登入狀態。
    """
    d = longterm_dir(persona)
    d.mkdir(parents=True, exist_ok=True)
    ts = utcnow_iso()
    fm = (f"---\n"
          f"type: longterm_memory_digest\n"
          f"persona: {persona}\n"
          f"span_wake: {span_start}-{span_end}\n"
          f"consolidated_at: {ts}\n"
          f"---\n\n")
    path = d / f"wake_{span_start:03d}-{span_end:03d}.md"
    with open(path, "w", encoding="utf-8") as f:
        f.write(fm + body + "\n")
    # 重建 _index.md（掃全部 digest，append-friendly）
    idx_lines = [f"# Long-term memory index — {persona}", ""]
    for dg in sorted(d.glob("wake_*.md")):
        idx_lines.append(f"- [{dg.name}]({dg.name}) — wake {read_frontmatter_field(dg, 'span_wake')} "
                         f"@ {read_frontmatter_field(dg, 'consolidated_at')}")
    with open(d / "_index.md", "w", encoding="utf-8") as f:
        f.write("\n".join(idx_lines) + "\n")
    return path, ts


# ─── 見森 (T3) — rolling fold ───────────────────────────────────────────
def list_forests(persona: str) -> list:
    d = forest_dir(persona)
    return sorted(d.glob("gen_*.md")) if d.exists() else []


def latest_forest(persona: str) -> Path | None:
    fs = list_forests(persona)
    return fs[-1] if fs else None


def forest_status(persona: str) -> dict:
    """見森狀態：門檻是否達到 / 是否有新見林未折疊。

    數值影響：首折是唯一的多輸入折疊（讀全部 digest）；之後恆為 2 份輸入
    （上代森 + 新林）→ 成本不隨壽命成長。
    """
    digs, fors = list_digests(persona), list_forests(persona)
    last_gen = len(fors)
    folded_upto = 0
    if fors:
        folded_upto = int(read_frontmatter_field(fors[-1], "folded_digest_count") or 0)
    return {
        "digest_count": len(digs),
        "forest_count": last_gen,
        "threshold": FOREST_DIGEST_THRESHOLD,
        "eligible": len(digs) >= FOREST_DIGEST_THRESHOLD,
        "folded_digest_count": folded_upto,
        "pending": max(0, len(digs) - folded_upto) if len(digs) >= FOREST_DIGEST_THRESHOLD else 0,
        "overdue": len(digs) >= FOREST_DIGEST_THRESHOLD and folded_upto < len(digs),
        "next_gen": last_gen + 1,
        "digests": digs,
        "latest_forest": fors[-1] if fors else None,
    }


def write_forest(persona: str, body: str) -> Path:
    """寫新一代見森（append-only：舊代全保留，per Tim 拍板）。"""
    st = forest_status(persona)
    d = forest_dir(persona)
    d.mkdir(parents=True, exist_ok=True)
    gen = st["next_gen"]
    span_end = 0
    if st["digests"]:
        m = re.search(r"wake_(\d+)-(\d+)", st["digests"][-1].name)
        span_end = int(m.group(2)) if m else 0
    prev = st["latest_forest"].name if st["latest_forest"] else "(首折)"
    path = d / f"gen_{gen:03d}_wake_001-{span_end:03d}.md"
    fm = (f"---\ntype: forest_digest\npersona: {persona}\ngeneration: {gen}\n"
          f"span_wake: 1-{span_end}\nfolded_digest_count: {st['digest_count']}\n"
          f"folded_from: {prev} + {st['digests'][-1].name if st['digests'] else '-'}\n"
          f"consolidated_at: {utcnow_iso()}\n---\n\n")
    path.write_text(fm + body.strip() + "\n", encoding="utf-8")
    return path


# ─── 見根 (T4) — fragments + 機械索引 ───────────────────────────────────
def parse_fragment(path: Path) -> dict:
    """讀單一 fragment 的 frontmatter → dict（含 origins 筆數當 fallback recurrence）。

    數值影響：只 parse frontmatter、不讀正文 → 索引生成成本 O(檔數) 且極輕。
    """
    try:
        text = path.read_text(encoding="utf-8")
    except Exception:
        return {}
    m = re.match(r"^---\n(.*?)\n---", text, re.S)
    if not m:
        return {}
    fm = {}
    for line in m.group(1).split("\n"):
        mm = re.match(r"^(\w+):\s*(.*)$", line)
        if mm:
            fm[mm.group(1)] = mm.group(2).strip()
    fm["_origin_count"] = len(re.findall(r"^\s*-\s*\{\s*by:", m.group(1), re.M))
    fm["_path"] = path
    fm.setdefault("id", path.stem)
    return fm


def load_fragments(persona: str) -> list:
    """列該 persona 全部 fragment（排除底線開頭的產物檔如 _root_index.md）。"""
    d = fragments_dir(persona)
    if not d.exists():
        return []
    out = []
    for p in sorted(d.glob("*.md")):
        if p.name.startswith("_"):
            continue
        fm = parse_fragment(p)
        if fm:
            out.append(fm)
    return out


def _frag_sort_key(f: dict):
    """排序：踩過次數(recurrence)降冪 → type 群組 → id（穩定）。
    物理意義：次數本身就是資訊 — 踩 9 次的教訓該排在最上面。"""
    try:
        rec = int(f.get("recurrence", f.get("_origin_count", 1)) or 1)
    except Exception:
        rec = 1
    # 分組要用 fragment_type; 用 `type` 的話每筆都是 "fragment" → 恆為 99,
    # 「type 群組」這一層排序從來沒生效過（不會報錯, 只是安靜地沒分組）。
    ft = f.get("fragment_type") or f.get("type")
    ti = FRAG_TYPE_ORDER.index(ft) if ft in FRAG_TYPE_ORDER else 99
    return (-rec, ti, f.get("id", ""))


def render_root_index(persona: str, show_limit: int = ROOT_INDEX_SHOW_LIMIT) -> str:
    """見根索引 — 純機械生成（掃 fragment frontmatter）。

    區塊職責：產出「必讀關鍵記憶」清單文本。
    數值影響：只列 status=open + 踩過次數最多的 3 筆 internalized；closed 不列但不刪檔；
      超過 show_limit 明說隱藏筆數（禁靜默截斷）。
    ⚠ 已知限制（@basecamp 2026-08-16 點出，未修）：`howto` 型碎片 recurrence 天生 =1，
      **出生就在顯示線以下** ⇒ 見根索引不是它的回流路徑。它的回流要走執行入口（見 recall()）。
      這一行留著，是為了讓下一個人知道「它沒出現在索引裡」是預期而不是壞了。
    """
    frags = load_fragments(persona)
    open_rows = [f for f in frags if f.get("status") == "open"]
    intl_rows = [f for f in frags if f.get("status") == "internalized"]
    open_rows.sort(key=_frag_sort_key)
    intl_rows.sort(key=_frag_sort_key)
    shown, hidden = open_rows[:show_limit], max(0, len(open_rows) - show_limit)

    L = ["---", "type: root_index", f"persona: {persona}",
         "generated: mechanical   # 掃 fragments/ frontmatter 產生 — 手改會被下次生成覆寫",
         f"fragment_total: {len(frags)}", "---", "",
         f"# 🌱 見根 — {persona} 必讀關鍵記憶索引", "",
         "> 機械生成 → 零漂移、可隨時重建、可 diff 驗證。事實來源永遠是 fragment 檔本身；",
         "> 見根/樹/叢/林/森都只是視圖。排序＝踩過次數降冪。closed 不列但不刪檔。", "",
         f"## 必讀（status: open，{len(open_rows)} 筆）", "",
         "| 次數 | 類型 | 關鍵記憶 | 涉及層 | 檔案 |", "|---|---|---|---|---|"]
    for f in shown:
        L.append(f"| **{f.get('recurrence', f['_origin_count'])}** | "
                 f"{f.get('fragment_type', f.get('type', '?'))} | "
                 f"{f.get('title', f['id'])} | {f.get('layers', '') or '—'} | "
                 f"[{f['id']}]({f['id']}.md) |")
    if hidden:
        L += ["", f"⚠ **另有 {hidden} 筆 open 未顯示**（顯示上限 {show_limit}）— 全清單見本目錄。"]
    L += ["", "## 已內化（status: internalized，取踩過次數最多的 3 筆）", ""]
    for f in intl_rows[:3]:
        L.append(f"- ✅ {f.get('title', f['id'])}（踩過 "
                 f"{f.get('recurrence', f['_origin_count'])} 次）→ [{f['id']}]({f['id']}.md)")
    if len(intl_rows) > 3:
        L.append(f"- …另有 {len(intl_rows) - 3} 筆已內化（不列，避免洗版；見本目錄）")
    shared = [f for f in frags if f.get("visibility") == "shared"]
    L += ["", "## 共享狀態", "",
          f"- shared（可被其他 persona / 外部 reference）：{len(shared)} 筆",
          f"- private：{len(frags) - len(shared)} 筆"]
    return "\n".join(L) + "\n"


def write_root_index(persona: str) -> Path | None:
    """生成/覆寫見根索引；無 fragment 時不建檔（回 None）。"""
    if not load_fragments(persona):
        return None
    d = fragments_dir(persona)
    d.mkdir(parents=True, exist_ok=True)
    path = root_index_path(persona)
    path.write_text(render_root_index(persona), encoding="utf-8")
    return path


# ─── 見叢 (T1.5) — 當期交棒清單 ─────────────────────────────────────────
def keys_entries(persona: str) -> tuple:
    """回 (未勾銷 list[str], 已勾銷 list[str])；解析 `- [ ]` / `- [x]` 行。"""
    p = keys_open_path(persona)
    if not p.exists():
        return [], []
    todo, done = [], []
    for line in p.read_text(encoding="utf-8").split("\n"):
        s = line.strip()
        if s.startswith("- [ ]"):
            todo.append(s[5:].strip())
        elif s.startswith("- [x]") or s.startswith("- [X]"):
            done.append(s[5:].strip())
    return todo, done


def keys_append(persona: str, items: list) -> Path:
    """append 交棒事項到當期見叢（隨時可加，不限儀式 — summit 2026-07-27 拍板：
    斷線風險最高的正是「沒走到任何儀式就掛掉」的場景）。"""
    p = keys_open_path(persona)
    p.parent.mkdir(parents=True, exist_ok=True)
    if not p.exists():
        p.write_text(
            "---\ntype: keys_open\npersona: %s\nopened_at: %s\n---\n\n"
            "# 🌿 見叢 — 當期交棒清單（跨夜 append-only，見林時歸檔）\n\n"
            "> 給明天的自己**執行**用（可勾銷）；抒發與敘事寫進 letter，不寫這裡。\n\n"
            % (persona, utcnow_iso()), encoding="utf-8")
    with open(p, "a", encoding="utf-8") as f:
        for it in items:
            f.write(f"- [ ] {it}  <!-- {utcnow_iso()} -->\n")
    return p


def keys_archive(persona: str, span_start: int, span_end: int) -> Path | None:
    """見林寫入時把當期見叢歸檔成 keys/wake_<N>-<M>.md 並重開空的當期檔。
    物理意義：叢的窗口與見林窗口同步開關 → 天然不會無限長。"""
    p = keys_open_path(persona)
    if not p.exists():
        return None
    ad = keys_archive_dir(persona)
    ad.mkdir(parents=True, exist_ok=True)
    dest = ad / f"wake_{span_start:03d}-{span_end:03d}.md"
    dest.write_text(p.read_text(encoding="utf-8"), encoding="utf-8")
    p.unlink()
    return dest


# ─── 召回 (recall) — 語意檢索既有向量庫 ─────────────────────────────────
# 區塊職責：把「想不起某件事怎麼做」變成一次語意檢索，走既有 knowledge_base.py（bge-m3）。
# 物理意義：檢索本身不是新東西 —— `kb_targets.json` 早就有 `fragments` target，
#   而 2026-08-16 實測發現**它從來沒有被建過索引**（整個向量庫只有 docs）。
#   ⇒ 本函式存在的理由不是「加一個搜尋」，是**給記憶層一個它自己的召回入口**，
#     讓任何動作入口（發文前、commit 前、開工前）都能呼叫，而不必知道向量庫在哪。
# ⚠ 已知邊界（不假裝它解了）：本函式解的是「查得準」那一半；
#   「我不知道我要查」那一半**要靠呼叫端把它掛在必經路徑上**，不是靠這裡。
#   （病歷：summit wake#54 —— 東西在、標籤在、當天讀過，然後宣稱自己沒有。）
# 數值影響：每次呼叫都要付一次 bge-m3 模型載入 —— 實測 CLI 單次約 4.3 秒。
#   ⚠ knowledge_base.py 檔頭寫「熱路徑保 <15ms」，那是**檢索本身**的數字，不含模型載入；
#     照字面把它排進每輪迴圈會付四秒。要 <15ms 得常駐行程。
# 預設檢索範圍 —— **不是單一 target**。
# 🩸 2026-08-16 血證（Tim 當場問出來的）：我拿「開畫前該做什麼」召回漫畫記憶，
#   四筆全中我自己的碎片、看起來很成功 —— 而 Tim 問「有沒有找到
#   `Manga_Adaptation_Workflow.md`」。答案是**沒有**：那份 SOP 住在 `coredocs`，
#   而我只查了 `fragments`。事後對照：同一個問題查 coredocs，那份 SOP score 0.71，
#   **比我碎片的 0.63 還高** —— 它一直都在，是我的檢索範圍把它擋在外面。
#   ⇒ 「預設只查一個 target」是枚舉盲區長在查詢層的版本：漏掉的那一類不會出現在結果裡，
#     而結果看起來完全正常（四筆命中、分數漂亮）。**預設要寬，要窄由呼叫端明講。**
DEFAULT_RECALL_TARGETS = "fragments,coredocs,docs,work_memory"


def _links_of(path: Path) -> list:
    """讀某份記憶檔 frontmatter 的 `links:` 清單（關聯記憶的邊）。

    🩸 為什麼要單獨讀而不是靠檢索撈到：`links:` 只是 frontmatter 裡的文字，
      它會被切進**某一個 chunk**，而那個 chunk 不一定排進 top-k ——
      於是「這份記憶指向哪份文件」變成靠運氣。實測：howto_manga_production 的四筆命中
      沒有任何一筆的 preview 含它 links 裡的 SOP 路徑，而那條路徑就寫在檔案第 20 行。
      ⇒ 關聯要當**邊**走（命中哪份檔就把該檔的 links 一起端出來），不能當內容碰運氣。
    """
    try:
        head = path.read_text(encoding="utf-8")[:2000]
    except Exception:
        return []
    m = re.search(r"^links:\s*\n((?:\s*-\s*.+\n)+)", head, re.M)
    if not m:
        return []
    out = []
    for line in m.group(1).split("\n"):
        s = line.strip()
        if not s.startswith("-"):
            continue
        v = s[1:].strip()
        v = v.split("#")[0].strip()      # 去尾註（`# 共用 SOP（全流程）`）
        if v:
            out.append(v)
    return out


def recall(query: str, target: str = DEFAULT_RECALL_TARGETS, topk: int = 5,
           timeout: int = 300) -> dict:
    """語意召回。回 {"ok", "stdout", "hits", "links", "error"}。

    `links`：本次命中的每份記憶檔在 frontmatter 宣告的關聯（去重後）——
    **這是「關聯記憶」的實作**：命中碎片就把它指向的 SOP／文件一起端上來，
    不必期待那條路徑自己排進 top-k。

    fail-soft：知識庫沒裝／索引沒建都不丟例外，回 ok=False + 可照做的訊息 ——
    呼叫端（例如發文前的自動附掛）不該因為召回不可用就整個失敗。
    """
    kb = _HERE / "knowledge_base.py"
    if not kb.exists():
        return {"ok": False, "stdout": "", "hits": [], "error": f"找不到 knowledge_base.py（{kb}）"}
    cmd = [sys.executable, str(kb), "search", "--query", query,
           "--target", target, "--topk", str(topk), "--format", "json"]
    try:
        r = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8",
                           timeout=timeout, cwd=str(REPO_ROOT))
    except Exception as e:
        return {"ok": False, "stdout": "", "hits": [], "error": f"召回失敗：{e}"}
    out = r.stdout or ""
    hits = []
    try:
        # stdout 可能混入模型載入的進度列 → 只取最後一個看起來像 JSON 物件的區塊
        start = out.find("{")
        if start >= 0:
            hits = (json.loads(out[start:]) or {}).get("hits", []) or []
    except Exception:
        pass
    # 關聯記憶：命中哪份檔，就把該檔宣告的 links 一起端出來（去重、保持出現順序）
    links, seen = [], set()
    for h in hits:
        f = h.get("file") or ""
        if not f:
            continue
        for lk in _links_of(Path(f)):
            if lk not in seen:
                seen.add(lk)
                links.append(lk)
    return {"ok": r.returncode == 0, "stdout": out, "hits": hits, "links": links,
            "error": "" if r.returncode == 0 else (r.stderr or "").strip()}


# ─── CLI（薄層：記憶層自己的入口，不經 awakening） ──────────────────────
def main() -> int:
    import argparse
    ap = argparse.ArgumentParser(description="記憶層 API（見樹/見叢/見林/見森/見根）+ 召回")
    sub = ap.add_subparsers(dest="cmd", required=True)

    pr = sub.add_parser("recall", help="語意召回（走 knowledge_base.py 向量檢索）＋關聯記憶")
    pr.add_argument("--query", required=True)
    pr.add_argument("--target", default=DEFAULT_RECALL_TARGETS,
                    help=f"逗號分隔；預設 {DEFAULT_RECALL_TARGETS}（刻意寬 —— 窄化要明講）")
    pr.add_argument("--topk", type=int, default=5)

    pi = sub.add_parser("root-index", help="見根：掃 fragments/ 機械重建 _root_index.md")
    pi.add_argument("--persona", required=True)

    ps = sub.add_parser("status", help="某 persona 的記憶層狀態（各層檔數）")
    ps.add_argument("--persona", required=True)

    a = ap.parse_args()
    if a.cmd == "recall":
        res = recall(a.query, a.target, a.topk)
        print(res["stdout"] or res["error"])
        # 關聯記憶單獨印一段 —— 它是**邊**不是命中內容，混在 hits 裡會被當成第 N 筆結果讀過去
        if res.get("links"):
            print("\n## 🔗 關聯記憶（命中碎片自己宣告的指向 — 不是檢索排名決定的）")
            for lk in res["links"]:
                print(f"  - {lk}")
        return 0 if res["ok"] else 1
    if a.cmd == "root-index":
        p = write_root_index(a.persona)
        print(f"✅ 見根索引重建: {p}" if p else f"（{a.persona} 尚無 fragment，未建檔）")
        return 0
    if a.cmd == "status":
        frags = load_fragments(a.persona)
        todo, done = keys_entries(a.persona)
        print(f"# 記憶層狀態 — {a.persona}")
        print(f"- 見樹 episodic : {len(list_episodic_letters(a.persona))} 封")
        print(f"- 見叢 keys     : {len(todo)} 未完 / {len(done)} 已完")
        print(f"- 見林 digests  : {len(list_digests(a.persona))} 份")
        print(f"- 見森 forests  : {len(list_forests(a.persona))} 代")
        print(f"- 見根 fragments: {len(frags)} 筆")
        # ⚠ 型別欄兩個名字都要吃：現存 fragment 寫的是 `type: lesson`，
        #   而排序/索引那兩支是 `fragment_type or type`。只讀其一 → 每一型都數到 0，
        #   而「0 筆」與「欄名讀錯」在畫面上同形（第一版就是這樣，實跑當場撞到）。
        typed = 0
        for t in FRAG_TYPE_ORDER:
            n = len([f for f in frags if (f.get("fragment_type") or f.get("type") or "") == t])
            typed += n
            print(f"    · {t:<11}{n}")
        if typed != len(frags):
            print(f"    ⚠ 有 {len(frags) - typed} 筆的型別不在已知清單內（不是 0 筆，是沒被認出來）")
        return 0
    return 2


if __name__ == "__main__":
    raise SystemExit(main())
