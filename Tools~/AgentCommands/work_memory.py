#!/usr/bin/env python3
"""work_memory.py — 工作記憶區（所有 agent 共用, 以「工作主題」為單位的 knowhow 庫）。

區塊職責: 承接 Memory_Fragment_Backfill 的 fragment 哲學（事實源寫一次不改、索引=機械視圖、
          先搜再寫防洗版）, 但記憶單位從「persona 身分」改為「工作主題」— 讓任何 agent
          在進行某項工作前, 5 分鐘內取得該工作的拍板/坑/現況/指路。
物理意義: 資料落 <repo>/AgentCommands/WorkMemory/<topic-slug>/ —
          _topic.md（主題卡）+ <type>_<slug>.md（記憶 fragment, 寫一次不改寫,
          更新走 status/origins/supersede）+ _index.md（機械生成視圖, 手改必被覆寫）。
          記憶之間可跨主題關聯（links: [<topic>/<fragment-id>]）, 讀取時 --with-links 一起拉。
數值影響: 與知識庫整合 — kb_targets.json 的 work_memory target 涵蓋本目錄,
          `knowledge_base.py search --target work_memory` 可語意檢索。

文件 vs 工作記憶的分工判準（Tim 2026-07-29 拍板設計）:
  - 文件（Docs/）= 內容本體: 完整規格/施工圖/欄位表, 會被維護更新, 篇幅大
  - 工作記憶 = 開工前 5 分鐘要知道、但翻文件目錄撈不快的東西:
    拍板結論(不含推導)/工作特有的坑/現況快照/「哪份文件是權威」的指路
  - 記憶不重複文件內容 — 記憶「指向」文件（pointer/related_docs）

CLI:
    python work_memory.py topics                          # 列出所有工作主題
    python work_memory.py read --topic <slug> [--with-links] [--types decision,pitfall]
    python work_memory.py init --topic <slug> --title <t> [--desc <d>]
    python work_memory.py add  --topic <slug> --type <t> --id <slug> --title <t> \
        (--body <text> | --body-file <path>) [--links t/f,t/f] [--docs d1,d2] [--by <persona>]
    python work_memory.py supersede --topic <slug> --id <fragment-id> [--by <new-fragment-id>]
    python work_memory.py link --from <topic>/<frag-id> --to <topic>/<frag-id>   # 雙向
    python work_memory.py index [--topic <slug>]          # 重建機械索引（add/link 後自動跑）
    python work_memory.py tasks --topic <slug> [--set 8,15 | --add 17 | --remove 3]  # 反向索引
    python work_memory.py archive --topic <slug> [--commit <sha>] [--undo]           # 退場（含 git 守衛）
    python work_memory.py delete --topic <slug> [--by <persona>] [--confirm]         # 刪除＋留墓碑

# Source: ucl_core:Tools~/AgentCommands/work_memory.py
"""
from __future__ import annotations

import argparse
import datetime
import re
import subprocess
import sys
from pathlib import Path

# Windows cp950 終端強制 utf-8（對齊 freetime.py 慣例）
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

FRAGMENT_TYPES = ("decision", "knowhow", "pitfall", "state", "pointer")
FRAGMENT_STATUS = ("active", "superseded", "closed")
# 區塊職責: 主題卡的生命週期狀態（TASK-0017）。
# 物理意義: 記憶是工作期間的鷹架不是永久資產 — 相關 Task 全關之後歸檔或刪除, 紀錄留 git。
# 數值影響: 只影響 topics 的分組與 read 的橫幅; 不影響任何 fragment 的內容。
TOPIC_STATUS = ("active", "archived")


# ===========================================================
# 路徑解析 — repo root（對齊 freetime.py / knowledge_base.py 慣例）
# ===========================================================
def _resolve_repo_root() -> Path:
    """委派 _lib/ucl_paths —— python 端路徑解析的唯一擁有者（Tim 2026-08-17 定調）。

    🩸 原本的最後一層 fallback 是 `parents[2]` —— 那是寫死目錄深度。
      工作記憶整批（WM_ROOT / READ_BRIEF_ROOT）都掛在這個 root 底下，
      推錯的症狀是「記憶讀得到、但讀到的是另一棵樹的」，而且不會報錯。
    """
    import importlib.util as _ilu
    _spec = _ilu.spec_from_file_location(
        "_ucl_paths_wm", Path(__file__).resolve().parent / "_lib" / "ucl_paths.py")
    _m = _ilu.module_from_spec(_spec)
    _spec.loader.exec_module(_m)
    return _m.repo_root()


REPO_ROOT = _resolve_repo_root()
WM_ROOT = REPO_ROOT / "AgentCommands" / "WorkMemory"
# 區塊職責: 為每次 `read` 產生「agent 與人類共讀」的 briefing，含記憶摘要與權威來源全文。
# 物理意義: briefing 置於 WorkMemory 外，避免 topics()/index()/知識庫把生成品誤判為工作記憶事實源。
# 數值影響: 每次讀取新增一份 UTF-8 Markdown；檔名含微秒 UTC 時間戳，連續呼叫也不會互相覆寫。
READ_BRIEF_ROOT = REPO_ROOT / "AgentCommands" / "WorkMemoryReadBriefs"
UCL_CORE_ROOT = Path(__file__).resolve().parents[2]
# 區塊職責: Task 單檔目錄 — 反向索引（task_indices → 那幾張單現在在哪一格）的讀取來源。
# 物理意義: 契約①（decision_contract-task-memory）: 本檔**只讀 Task 側, 絕不寫**。
#          Task 的 memory_topic / memory_archived_commit 歸 Cmd_Task（C#）寫;
#          記憶側的 task_indices / status 歸本檔寫。互寫＝兩個獨立寫入者的分散式衝突。
# 數值影響: 純讀; 讀不到單檔不是錯誤, 是**一種要被印出來的答案**（見 _describe_task）。
TASKS_ROOT = REPO_ROOT / "AgentCommands" / "Tasks" / "tasks"
# 區塊職責: 主題被刪除時的墓碑總表（append-only）。
# 物理意義: **刪除可以, 失聯不行** — 刪掉的主題要留一行指向那顆 commit, 否則
#          「這個主題不存在」與「它曾經存在, 內容在 git 裡」在輸出上同形。
# 數值影響: 檔名以 `_` 開頭 ⇒ topics()/index() 的 `d.is_dir()` 掃描天生略過它。
TOMBSTONE_PATH = WM_ROOT / "_tombstones.md"
# 區塊職責: 限制每份來源在共讀 briefing 中的展開行數，保留辨識檔案職責所需的開頭上下文。
# 物理意義: 完整來源仍以 related_docs 指向的原始檔為單一真相；briefing 僅是開工導覽，不取代原檔。
# 數值影響: 每個來源最多貢獻 100 行，避免單一大型 C# 或 workflow 壓縮其他記憶與文件的可讀空間。
READ_BRIEF_MAX_SOURCE_LINES = 100


# ===========================================================
# frontmatter — 輕量 flat 解析（value 含冒號 OK; list 認 [a, b] 單行式）
# ===========================================================
def parse_frontmatter(text: str) -> tuple[dict, str]:
    meta: dict = {}
    body = text
    if text.startswith("---"):
        parts = text.split("---", 2)
        if len(parts) >= 3:
            body = parts[2].strip()
            for line in parts[1].splitlines():
                line = line.strip()
                if not line or line.startswith("#") or ":" not in line:
                    continue
                key, _, val = line.partition(":")
                key, val = key.strip(), val.strip()
                if val.startswith("[") and val.endswith("]"):
                    meta[key] = [v.strip() for v in val[1:-1].split(",") if v.strip()]
                else:
                    meta[key] = val
    return meta, body


def dump_frontmatter(meta: dict) -> str:
    lines = ["---"]
    for k, v in meta.items():
        if isinstance(v, list):
            lines.append(f"{k}: [{', '.join(v)}]")
        else:
            lines.append(f"{k}: {v}")
    lines.append("---")
    return "\n".join(lines)


# ===========================================================
# 讀寫底層
# ===========================================================
def topic_dir(topic: str) -> Path:
    return WM_ROOT / topic


def load_fragment(path: Path) -> dict | None:
    try:
        meta, body = parse_frontmatter(path.read_text(encoding="utf-8"))
        meta["_body"] = body
        meta["_path"] = path
        return meta
    except OSError:
        return None


def list_fragments(topic: str) -> list[dict]:
    d = topic_dir(topic)
    if not d.is_dir():
        return []
    out = []
    for f in sorted(d.glob("*.md")):
        if f.name.startswith("_"):
            continue
        frag = load_fragment(f)
        if frag:
            out.append(frag)
    # active 優先, 再按 type 固定序
    type_order = {t: i for i, t in enumerate(FRAGMENT_TYPES)}
    out.sort(key=lambda x: (0 if x.get("status", "active") == "active" else 1,
                            type_order.get(x.get("type", ""), 99), str(x.get("id", ""))))
    return out


def save_fragment_meta(frag: dict) -> None:
    """只重寫 frontmatter, 正文原樣保留（更新 status/links 用 — 對齊「不重寫正文」鐵律）。"""
    path: Path = frag["_path"]
    meta = {k: v for k, v in frag.items() if not k.startswith("_")}
    path.write_text(dump_frontmatter(meta) + "\n\n" + frag["_body"] + "\n", encoding="utf-8")


# ===========================================================
# 主題卡讀寫 — 歸檔／task_indices 都只動 frontmatter, 正文原樣保留
# ===========================================================
def load_topic_card(topic: str) -> dict | None:
    card_path = topic_dir(topic) / "_topic.md"
    return load_fragment(card_path) if card_path.exists() else None


def save_topic_card(card: dict) -> None:
    save_fragment_meta(card)


def _int_list(vals) -> list[int]:
    """把 frontmatter 讀回來的 task_indices 正規化成 int list（壞值略過但**出聲**）。"""
    out: list[int] = []
    for v in (vals or []):
        s = str(v).strip().lstrip("#")
        if not s:          # 空字串＝「沒有值」, 不是「壞值」—— 兩者不可以走同一條路
            continue
        s = s[5:] if s.upper().startswith("TASK-") else s
        try:
            out.append(int(s))
        except ValueError:
            print(f"⚠ task_indices 裡有一個不是數字的值, 已略過: {v!r}")
    return sorted(dict.fromkeys(out))


# ===========================================================
# git 前置守衛 — 歸檔／刪除之前先驗「已入版控」
# ===========================================================
# 區塊職責: 回答「這個目錄現在在 git 裡乾淨嗎」, 並在不乾淨時把**實際的 git status** 印出來。
# 物理意義: 🩸 2026-08-24 血證: 寫完四筆記憶後 `git status` 顯示 untracked —
#          也就是在 `8c77758` 之前,「刪掉也沒關係, git 有」是**假的**。
#          📌「反正 git 有」不是狀態, 是一個需要被驗的前提。
# 數值影響: 純讀。回 (clean, detail)。git 不可用時回 clean=False —— **不假設乾淨**
#          （讀取失敗與真的乾淨在輸出上必須可分, 否則守衛會在最需要它的時候安靜地放行）。
# ===========================================================
def _git(args: list[str], cwd: Path) -> tuple[int, str, str]:
    try:
        r = subprocess.run(["git", "-C", str(cwd), *args], capture_output=True, text=True,
                           encoding="utf-8", errors="replace", timeout=30)
        return r.returncode, (r.stdout or "").strip(), (r.stderr or "").strip()
    except (OSError, subprocess.SubprocessError) as exc:
        return -1, "", str(exc)


def git_owning_worktree(path: Path) -> Path | None:
    """回傳**真正擁有** path 的 git 工作區根目錄（可能是巢狀 submodule）。

    🩸 2026-08-25 血證（本函式的存在理由）: 第一版直接對 REPO_ROOT 問
      `git status --porcelain -- <path>`, 而 `AgentCommands/WorkMemory` 是**巢狀 submodule** ——
      父 repo 只追蹤那顆 gitlink, 對子路徑一無所知 ⇒ 回傳**空字串、exit 0**。
      於是守衛把「我問錯 repo」讀成「目錄是乾淨的」, 在我改完 `_topic.md` 的當下**放行了歸檔**。
      📌 這正是這支守衛要防的那隻病, 而它第一次上場就自己犯了一次:
        **讀取失敗與真的 0 在輸出上必須可分。**
    """
    code, out, _ = _git(["rev-parse", "--show-toplevel"], path if path.is_dir() else path.parent)
    if code != 0 or not out:
        return None
    return Path(out)


def git_dir_status(path: Path) -> tuple[bool, str]:
    top = git_owning_worktree(path)
    if top is None:
        return False, ("⚠ 問不出這個路徑屬於哪個 git 工作區 —— 這不是「乾淨」, 是**沒有讀數**"
                       "（git 不可用／不在任何 repo 內）")
    try:
        rel = path.resolve().relative_to(top.resolve()).as_posix()
    except ValueError:
        rel = "."
    # ===========================================================
    # ① **先問「真的在版控裡嗎」（ls-files），再問「有沒有待處理的變更」（status）。**
    #
    # 🩸 2026-08-25 血證（summit QA，TASK-0017 第二條）: 原本只問 status，判準是 `out == ""`。
    #   而 `git status --porcelain -- <path>` 的**空字串有三種來源**：
    #     ① 真的乾淨　② **路徑被 ignore**　③ 路徑不存在
    #   ⇒ 守衛只認得第一種。她的實跑：同一個 untracked 主題，只加一行 `.git/info/exclude`，
    #     前一分鐘還 `🛑 擋下 exit=3`，加完就 `✅ 乾淨` → 歸檔放行，
    #     而墓碑寫進一顆**那個主題從來不存在的 sha**。
    #
    # 📌 她入典的一般形（我照抄，因為它比我原本的判準準）:
    #   **守衛量的是「沒有待處理的變更」，而標準要的是「真的在版控裡」——
    #     兩個量，只在「檔案有被追蹤」時才相等。**
    #   ⇒ 所以修法不是多加一個 if，是**把量換掉**：以「磁碟上每一個檔都在 ls-files 裡」為主判準。
    #
    # ⚠ 而 ignore 與 untracked 在輸出上**刻意分開講**（`check-ignore` 問一次）——
    #   兩者的處置不同（一個要改 ignore 規則、一個要 add），印成同一句就又是一次同形。
    # ===========================================================
    code, tracked_out, err = _git(["ls-files", "--", rel or "."], top)
    if code != 0:
        return False, f"⚠ `git ls-files` 回非零（{code}）：{err} —— 沒有讀數, 不放行"
    tracked = {ln.strip() for ln in tracked_out.splitlines() if ln.strip()}
    disk = sorted(p for p in path.rglob("*") if p.is_file()) if path.is_dir() else [path]
    prefix = (rel.rstrip("/") + "/") if rel not in ("", ".") else ""
    missing = []
    for p in disk:
        try:
            r = p.resolve().relative_to(top.resolve()).as_posix()
        except ValueError:
            r = p.name
        if r not in tracked:
            code_ci, ci_out, _ = _git(["check-ignore", "-v", "--", r], top)
            # `check-ignore -v` 的格式是 `<來源>:<行號>:<pattern>\t<路徑>`。
            # ⚠ Windows 的來源是 `D:/…` —— 用 `split(":")` 取第一段會得到「D」（實測踩過）。
            #   ⇒ 取 tab 前那整段, 那才是「哪一條規則、寫在哪」這個人要的答案。
            why = ci_out.split("\t")[0].strip() if (code_ci == 0 and ci_out) else ""
            missing.append(f"{r}　" + (f"← **被 ignore**（{why}）" if why else "← untracked"))
    if not tracked:
        return False, (f"[工作區 {top}]\n"
                       f"**`{prefix or '.'}` 底下沒有任何檔案在版控裡**（`git ls-files` 回 0 筆）\n"
                       + "\n".join(missing))
    if missing:
        return False, (f"[工作區 {top}]\n"
                       f"磁碟上有 {len(missing)} 個檔**不在版控裡**（ignore 與 untracked 分開標）：\n"
                       + "\n".join(missing))

    # ② 追蹤到了, 再問有沒有未提交的變更
    code, out, err = _git(["status", "--porcelain", "--untracked-files=all", "--", rel or "."], top)
    if code != 0:
        return False, f"⚠ git 回非零（{code}）：{err} —— 沒有讀數, 不放行"
    return (out == ""), (f"[工作區 {top}]\n" + out if out else "")


def git_head_sha(path: Path | None = None) -> str:
    """**擁有這份內容的那個工作區**的 HEAD —— 不是父 repo 的。

    🩸 2026-08-25 血證: 第一版取的是 `REPO_ROOT` 的 HEAD。而記憶住在巢狀 submodule
      `AgentCommands/WorkMemory` 裡, 父 repo 的 HEAD 只記著一顆 **gitlink** ——
      而本專案的父層 pointer **長期未 bump**（見林四代未解線）⇒ 那顆 sha 指到的
      WorkMemory 版本裡, 這些 fragment **根本還不在**。
      📌 它會寫進 `archived_commit`、被印在墓碑上、被接手的人拿去 `git show` ——
        然後找不到東西, 而 sha 本身長得完全正常。**這種錯不會叫。**
    """
    top = git_owning_worktree(path) if path is not None else REPO_ROOT
    if top is None:
        return ""
    code, out, _ = _git(["rev-parse", "HEAD"], top)
    return out if code == 0 else ""


# ===========================================================
# 反向索引 — task_indices → 那幾張單現在在哪一格
# ===========================================================
# 區塊職責: 把一個單號解讀成**一行現況**（狀態／參與者）。
# 物理意義: TASK-0015 驗收標準第三條（2026-08-25 由 PM 拆到本單）:
#          「讓接手的人不必人腦記單號, 也不必再跑一次 op=list 去查那幾張單在哪一格」。
# 數值影響: 純讀。⛔ **三種答案不可以同形**（契約③的鏡像）:
#            ① 單在且未關 ② 單在但已關 ③ **號碼在而單檔讀不到**
#          第三種若印成「沒有這張單」, 就是「找不到 vs 什麼都沒有」那隻病。
# ===========================================================
CLOSED_TASK_STATUS = ("done", "cancelled")


def _describe_task(index: int) -> str:
    path = TASKS_ROOT / f"{index:04d}.md"
    if not path.is_file():
        return (f"TASK-{index:04d}　⚠ **號碼在, 但單檔讀不到**（{path.name}）"
                f" —— 這是「連結壞了」不是「沒有這張單」")
    try:
        meta, _ = parse_frontmatter(path.read_text(encoding="utf-8"))
    except OSError as exc:
        return f"TASK-{index:04d}　⚠ **讀取失敗**：{exc} —— 沒有讀數, 不是沒有內容"
    status = str(meta.get("status", "")).strip() or "?"
    title = str(meta.get("title", "")).strip()
    # 參與者是巢狀 list, 輕量 frontmatter 解析器讀不到 ⇒ 直接掃原文, 掃不到就說掃不到
    who = _participants_of(path)
    mark = "✅" if status in CLOSED_TASK_STATUS else "🔸"
    return f"{mark} TASK-{index:04d}　`{status}`　{title}" + (f"　[{who}]" if who else "")


def _participants_of(path: Path) -> str:
    """從單檔的 participants 區塊抓 persona(role) —— 巢狀 YAML, 輕量解析器讀不到。"""
    try:
        lines = path.read_text(encoding="utf-8").splitlines()
    except OSError:
        return ""
    out, persona, in_block = [], "", False
    for line in lines:
        if line.startswith("participants:"):
            in_block = True
            continue
        if in_block:
            if line[:1] not in (" ", "-") or line.startswith("---"):
                break
            m = re.match(r"\s*-\s*persona:\s*(\S+)", line)
            if m:
                persona = m.group(1)
                continue
            m = re.match(r"\s*role:\s*(\S+)", line)
            if m and persona:
                out.append(f"{persona}({m.group(1)})")
                persona = ""
    return "、".join(out)


def describe_related_tasks(card: dict) -> list[str]:
    """主題卡 → 關聯單現況區塊（給 read 用）。**沒有關聯單也要印一行**, 不可靜默。"""
    idx = _int_list(card.get("task_indices"))
    if not idx:
        return ["🔗 關聯 Task：**尚未關聯任何單**"
                "（`work_memory.py tasks --topic <t> --add <n>` 建反向索引;"
                " 這不等於「沒有單」, 只等於**沒有人建過這個連結**）"]
    rows = [f"🔗 關聯 Task（{len(idx)} 張）："]
    rows.extend("    · " + _describe_task(i) for i in idx)
    open_n = sum(1 for i in idx
                 if (TASKS_ROOT / f"{i:04d}.md").is_file()
                 and str(parse_frontmatter((TASKS_ROOT / f"{i:04d}.md").read_text(encoding="utf-8"))[0]
                         .get("status", "")).strip() not in CLOSED_TASK_STATUS)
    rows.append(f"    ⇒ 未關 **{open_n}** / 共 {len(idx)} 張"
                + ("　✅ 全部關了 ⇒ 這個主題可以考慮歸檔（`archive`）"
                   if open_n == 0 else "　⇒ 還不到歸檔的時候"))
    return rows


# ===========================================================
# op: topics / init / add / read / link / supersede / index
#     / tasks / archive / delete（TASK-0017）
# ===========================================================
def op_topics(_args) -> int:
    if not WM_ROOT.is_dir():
        print("（工作記憶區為空 — 用 init 建第一個主題）")
        return 0
    # 區塊職責: 依 status 分組列出（TASK-0017 驗收標準⑤）。
    # 物理意義: 原本 active 與 archived 只靠排序分先後 —— 而**排序不是分界**:
    #          讀的人看到一長串, 分不出哪些是還在做的、哪些是已經退場的。
    # 數值影響: 純顯示。空的組別**也印一行**（「0 個」與「這組不存在」不可以同形）。
    groups: dict[str, list[str]] = {s: [] for s in TOPIC_STATUS}
    unknown: list[str] = []
    for d in sorted(WM_ROOT.iterdir()):
        if not d.is_dir():
            continue
        card = load_fragment(d / "_topic.md") if (d / "_topic.md").exists() else None
        n = len([f for f in d.glob("*.md") if not f.name.startswith("_")])
        title = card.get("title", "") if card else ""
        status = str(card.get("status", "?")).strip() if card else "?"
        rel = ", ".join(card.get("related_topics", [])) if card else ""
        tasks = _int_list(card.get("task_indices")) if card else []
        row = (f"  - {d.name}  「{title}」 fragments={n}"
               + (f"  🔗 TASK {','.join(str(i) for i in tasks)}" if tasks else "")
               + (f"  ↔ {rel}" if rel else ""))
        if status == "archived":
            sha = str(card.get("archived_commit", "")).strip() if card else ""
            row += f"  📦 已歸檔（{sha[:9] or 'sha 未記'}）"
        (groups[status] if status in groups else unknown).append(row)

    print("🧠 工作記憶主題:")
    print(f"\n■ active（{len(groups['active'])} 個）—— 還在做的")
    print("\n".join(groups["active"]) or "  （無）")
    print(f"\n■ archived（{len(groups['archived'])} 個）—— 已退場, 內容仍在磁碟上, 全文照樣 read 得到")
    print("\n".join(groups["archived"]) or "  （無）")
    if unknown:
        print(f"\n■ ⚠ status 認不得（{len(unknown)} 個）—— 主題卡壞了或缺 status, 不是 active 也不是 archived")
        print("\n".join(unknown))
    if TOMBSTONE_PATH.exists():
        n_tomb = sum(1 for ln in TOMBSTONE_PATH.read_text(encoding="utf-8").splitlines()
                     if ln.startswith("- "))
        print(f"\n🪦 另有 **{n_tomb}** 個主題已被刪除（墓碑在 {TOMBSTONE_PATH.name}, 內容在 git 裡）")
    return 0


def op_init(args) -> int:
    d = topic_dir(args.topic)
    card = d / "_topic.md"
    if card.exists():
        print(f"⚠ 主題已存在: {args.topic}")
        return 1
    d.mkdir(parents=True, exist_ok=True)
    meta = {
        "id": args.topic,
        "title": args.title,
        "status": "active",
        "created_at": datetime.date.today().isoformat(),
        "related_topics": [],
        "key_docs": [],
    }
    card.write_text(dump_frontmatter(meta) + "\n\n" + (args.desc or args.title) + "\n", encoding="utf-8")
    print(f"✅ 主題已建立: {args.topic}（{args.title}）")
    rebuild_index(args.topic)
    return 0


def op_add(args) -> int:
    if args.type not in FRAGMENT_TYPES:
        print(f"✗ type 必須是 {FRAGMENT_TYPES}")
        return 2
    d = topic_dir(args.topic)
    if not (d / "_topic.md").exists():
        print(f"✗ 主題不存在: {args.topic}（先 init）")
        return 2
    frag_id = f"{args.type}_{args.id}" if not args.id.startswith(args.type + "_") else args.id
    path = d / f"{frag_id}.md"
    if path.exists():
        print(f"✗ fragment 已存在: {frag_id} — 記憶寫一次不改寫; 要更新走 supersede 或追加 origins")
        return 2
    body = args.body or ""
    if args.body_file:
        body = Path(args.body_file).read_text(encoding="utf-8")
    if not body.strip():
        print("✗ 缺 --body 或 --body-file")
        return 2
    # 區塊職責: 防洗版 gate — 寫入「前」做輕量近似檢查（crest-001 測試回報②: 事後提醒是馬後炮）。
    # 物理意義: 掃全主題 fragment 標題做字詞重疊比對（零依賴、<10ms）; 命中僅警示不擋
    #          （語意級查重仍建議 KB search, 這裡是最後一道便宜防線）。
    similar = _find_similar_titles(args.title, exclude_topic_frag=(args.topic, frag_id))
    if similar:
        print("⚠ 防洗版警示 — 發現標題近似的既有 fragment（確認不是同一條再繼續; 是同一條請改用 link/追加）:")
        for t, fid, title in similar[:5]:
            print(f"    - {t}/{fid} — {title}")
    meta = {
        "id": frag_id,
        "topic": args.topic,
        "title": args.title,
        "type": args.type,
        "status": "active",
        "created_at": datetime.date.today().isoformat(),
        "created_by": args.by or "unknown",
        "links": [x.strip() for x in (args.links or "").split(",") if x.strip()],
        "related_docs": [x.strip() for x in (args.docs or "").split(",") if x.strip()],
    }
    path.write_text(dump_frontmatter(meta) + "\n\n" + body.strip() + "\n", encoding="utf-8")
    print(f"✅ fragment 已寫入: {args.topic}/{frag_id}")
    rebuild_index(args.topic)
    return 0


def _find_similar_titles(title: str, exclude_topic_frag: tuple[str, str]) -> list[tuple[str, str, str]]:
    """標題字詞重疊的輕量近似查找（bigram 重疊率 > 0.5 判近似）。回 [(topic, frag_id, title)]。"""
    def bigrams(s: str) -> set:
        s = "".join(ch for ch in s.lower() if ch.strip())
        return {s[i:i + 2] for i in range(len(s) - 1)} if len(s) > 1 else {s}
    q = bigrams(title)
    out = []
    if not WM_ROOT.is_dir() or not q:
        return out
    for d in WM_ROOT.iterdir():
        if not d.is_dir():
            continue
        for frag in list_fragments(d.name):
            if (d.name, frag.get("id")) == exclude_topic_frag:
                continue
            other = bigrams(str(frag.get("title", "")))
            if other and len(q & other) / max(len(q | other), 1) > 0.5:
                out.append((d.name, frag.get("id"), frag.get("title", "")))
    return out


def op_supersede(args) -> int:
    frags = {f.get("id"): f for f in list_fragments(args.topic)}
    frag = frags.get(args.id)
    if frag is None:
        print(f"✗ 找不到 fragment: {args.topic}/{args.id}")
        return 2
    new_id = args.by
    # 一步式（crest-001 測試回報③）: --new-id/--new-title/--new-body[-file] → 建新快照 + 舊檔標 superseded
    if args.new_id:
        new_body = args.new_body or ""
        if args.new_body_file:
            new_body = Path(args.new_body_file).read_text(encoding="utf-8")
        if not new_body.strip() or not args.new_title:
            print("✗ 一步式需要 --new-title 與 --new-body(-file)")
            return 2
        frag_type = frag.get("type", "state")
        full_new_id = f"{frag_type}_{args.new_id}" if not args.new_id.startswith(frag_type + "_") else args.new_id
        new_path = topic_dir(args.topic) / f"{full_new_id}.md"
        if new_path.exists():
            print(f"✗ 新 fragment 已存在: {full_new_id}")
            return 2
        meta = {"id": full_new_id, "topic": args.topic, "title": args.new_title, "type": frag_type,
                "status": "active", "created_at": datetime.date.today().isoformat(),
                "created_by": args.new_by or frag.get("created_by", "unknown"),
                "links": [f"{args.topic}/{frag.get('id')}"],
                "related_docs": frag.get("related_docs") or []}
        new_path.write_text(dump_frontmatter(meta) + "\n\n" + new_body.strip() + "\n", encoding="utf-8")
        new_id = full_new_id
        print(f"✅ 新 fragment 已寫入: {args.topic}/{full_new_id}")
    frag["status"] = "superseded"
    if new_id:
        links = frag.get("links") or []
        target = new_id if "/" in new_id else f"{args.topic}/{new_id}"
        if target not in links:
            links.append(target)
        frag["links"] = links
    save_fragment_meta(frag)
    print(f"✅ {args.topic}/{args.id} → superseded" + (f"（由 {new_id} 取代）" if new_id else ""))
    rebuild_index(args.topic)
    return 0


def _parse_ref(ref: str) -> tuple[str, str]:
    if "/" not in ref:
        raise ValueError(f"關聯引用需為 <topic>/<fragment-id>: {ref}")
    t, _, f = ref.partition("/")
    return t, f


def op_link(args) -> int:
    """雙向關聯 — 兩端 frontmatter links 互加（跨主題合法, 正是設計目的）。"""
    try:
        ta, fa = _parse_ref(getattr(args, "from"))
        tb, fb = _parse_ref(args.to)
    except ValueError as e:
        print(f"✗ {e}")
        return 2
    changed = 0
    for (t, f, other) in ((ta, fa, f"{tb}/{fb}"), (tb, fb, f"{ta}/{fa}")):
        frag = next((x for x in list_fragments(t) if x.get("id") == f), None)
        if frag is None:
            print(f"✗ 找不到 {t}/{f}")
            return 2
        links = frag.get("links") or []
        if other not in links:
            links.append(other)
            frag["links"] = links
            save_fragment_meta(frag)
            changed += 1
    print(f"✅ 已建立雙向關聯: {ta}/{fa} ↔ {tb}/{fb}（更新 {changed} 檔）")
    rebuild_index(ta)
    if tb != ta:
        rebuild_index(tb)
    return 0


def rebuild_index(topic: str) -> None:
    """機械生成 _index.md — 手改必被覆寫; 事實源永遠是 fragment 檔。"""
    d = topic_dir(topic)
    frags = list_fragments(topic)
    lines = [f"# 工作記憶索引 — {topic}", "",
             "> 機械生成（work_memory.py index）— 手改會被覆寫。事實源 = 各 fragment 檔。", ""]
    for t in FRAGMENT_TYPES:
        rows = [f for f in frags if f.get("type") == t]
        if not rows:
            continue
        lines.append(f"## {t}")
        for f in rows:
            mark = "" if f.get("status", "active") == "active" else f" ~~[{f.get('status')}]~~"
            links = f.get("links") or []
            link_note = f"  ↔ {', '.join(links)}" if links else ""
            lines.append(f"- **{f.get('id')}** — {f.get('title', '')}{mark}{link_note}")
        lines.append("")
    # 區塊職責: 輸出機械索引時正規化檔尾，只保留一個換行。
    # 物理意義: section 之間仍以空白行分隔，但最後一個 section 不會留下無內容的尾端段落。
    # 數值影響: 消除 git diff --check 的 EOF blank-line 警告，不改變任何 fragment 或索引項目。
    (d / "_index.md").write_text("\n".join(lines).rstrip() + "\n", encoding="utf-8")


def op_index(args) -> int:
    topics = [args.topic] if args.topic else [d.name for d in WM_ROOT.iterdir() if d.is_dir()]
    for t in topics:
        rebuild_index(t)
        print(f"✅ 索引重建: {t}")
    return 0


def resolve_related_doc(ref: str) -> tuple[Path | None, str]:
    """將 related_docs 的本地檔案 ref 安全解析；非本地 ref 保留原因供 briefing 顯示。"""
    # 區塊職責: 支援 `path:line` 與 `ucl_core:path`，同時拒絕 commit/tavern 等非檔案 ref。
    # 物理意義: 僅允許 repo 或 UCL_Core submodule 內的檔案，避免記憶資料意外要求 briefing 洩出工作區外內容。
    # 數值影響: 解析失敗只新增一條診斷文字，不中斷其餘來源的載入。
    raw = ref.strip()
    if raw.startswith(("commit:", "tavern:", "workmem:")):
        return None, "非本地檔案引用"
    base = REPO_ROOT
    if raw.startswith("ucl_core:"):
        base, raw = UCL_CORE_ROOT, raw.removeprefix("ucl_core:")
    raw = re.sub(r":\d+(?:-\d+)?$", "", raw)
    candidate = Path(raw)
    if not candidate.is_absolute():
        candidate = base / candidate
    try:
        resolved = candidate.resolve()
        if not (resolved.is_relative_to(REPO_ROOT.resolve()) or resolved.is_relative_to(UCL_CORE_ROOT.resolve())):
            return None, "路徑不在允許的工作區範圍"
    except OSError as exc:
        return None, f"路徑解析失敗: {exc}"
    if not resolved.is_file():
        return None, "檔案不存在或不是一般檔案"
    return resolved, ""


def save_read_brief(topic: str, with_links: bool, types: str, result: str,
                    lines: list[str], related_docs: list[str]) -> Path:
    """把 read 的記憶摘要及本地 related_docs 全文寫成 agent 與人類共讀的 briefing。"""
    # 區塊職責: 單一 Markdown 同時承載本次 read 的記憶清單與有效本地來源全文。
    # 物理意義: agent 在命令後開啟此檔即可取得與人類完全相同的輸入，不再靠終端截斷內容。
    # 數值影響: 每份來源最多嵌入 READ_BRIEF_MAX_SOURCE_LINES 行；不可解析 ref 以文字診斷取代全文。
    READ_BRIEF_ROOT.mkdir(parents=True, exist_ok=True)
    read_at = datetime.datetime.now(datetime.timezone.utc)
    brief_path = READ_BRIEF_ROOT / f"{read_at:%Y%m%d_%H%M%S_%fZ}_{topic}.md"
    brief = [
        "---",
        f"title: 工作記憶 Read Brief — {topic}",
        f"topic: {topic}",
        f"read_at_utc: {read_at.isoformat()}",
        f"result: {result}",
        f"with_links: {str(with_links).lower()}",
        f"types: {types or 'all'}",
        "---",
        "",
        "# 工作記憶 Read Brief",
        "",
        "## 本次記憶摘要",
        "",
        *lines,
    ]
    if related_docs:
        brief.extend(("## 已嵌入的本地來源", ""))
        for ref in dict.fromkeys(related_docs):
            source, reason = resolve_related_doc(ref)
            brief.extend((f"### `{ref}`", ""))
            if source is None:
                brief.extend((f"> 未嵌入：{reason}。", ""))
                continue
            try:
                content = source.read_text(encoding="utf-8")
            except OSError as exc:
                brief.extend((f"> 未嵌入：讀取失敗：{exc}", ""))
                continue
            suffix = source.suffix.lstrip(".") or "text"
            source_lines = content.splitlines()
            displayed_lines = source_lines[:READ_BRIEF_MAX_SOURCE_LINES]
            brief.extend((f"~~~~{suffix}", "\n".join(displayed_lines), "~~~~", ""))
            if len(source_lines) > READ_BRIEF_MAX_SOURCE_LINES:
                omitted = len(source_lines) - READ_BRIEF_MAX_SOURCE_LINES
                brief.extend((
                    f"> ⚠ 已截斷：僅顯示前 {READ_BRIEF_MAX_SOURCE_LINES} / {len(source_lines)} 行，後續 {omitted} 行請直接查看原始檔 `{ref}`。",
                    "",
                ))
    brief_path.write_text("\n".join(brief).rstrip() + "\n", encoding="utf-8")
    return brief_path


def op_read(args) -> int:
    d = topic_dir(args.topic)
    card_path = d / "_topic.md"
    if not card_path.exists():
        # 區塊職責: 不存在的 topic 也留下 read 快照，讓人類可以回查失敗的原因與當時的輸入。
        # 物理意義: 失敗輸出採用與成功讀取相同的稽核資料夾，避免讀取紀錄只保留「看似正常」的案例。
        # 數值影響: 失敗讀取同樣只多一個小型 UTF-8 檔，不改變命令的 exit code（仍為 2）。
        lines = [f"✗ 主題不存在: {args.topic}。現有主題:"]
        if WM_ROOT.is_dir():
            for topic in sorted(d.name for d in WM_ROOT.iterdir() if d.is_dir()):
                lines.append(f"  - {topic}")
        else:
            lines.append("（工作記憶區為空 — 用 init 建第一個主題）")
        brief_path = save_read_brief(args.topic, args.with_links, args.types, "not_found", lines, [])
        lines.append(f"📄 共讀 briefing: {brief_path.relative_to(REPO_ROOT)}（請開啟此檔繼續讀取）")
        print("\n".join(lines))
        return 2
    want_types = [t.strip() for t in (args.types or "").split(",") if t.strip()] or list(FRAGMENT_TYPES)

    # 區塊職責: 先累積輸出再一次印出與落檔，確保終端內容和可稽核快照逐字一致。
    # 物理意義: 人類日後可直接開啟快照驗證 agent 當時取得哪些 active/superseded/關聯記憶。
    # 數值影響: 所有 read 路徑（含不存在的關聯目標）都被保留，避免遺漏診斷訊號。
    lines: list[str] = []
    related_docs: list[str] = []
    card = load_fragment(card_path)
    lines.append(f"🧠 工作記憶 — {card.get('title', args.topic)}  [{card.get('status', '?')}]")
    # 區塊職責: 已歸檔的主題**照樣印全文**, 只是頭上多一條橫幅（TASK-0017 驗收標準④）。
    # 物理意義: ⛔「已歸檔」不可以印成「沒有這個主題」——「曾經有、內容在這裡、只是退場了」
    #          與「從來沒有」是兩件事, 而它們同形就是「找不到 vs 什麼都沒有」那隻病。
    # 數值影響: 純顯示; 不擋讀取, 不改任何內容。
    if str(card.get("status", "")).strip() == "archived":
        sha = str(card.get("archived_commit", "")).strip()
        at = str(card.get("archived_at", "")).strip()
        lines.append(f"   📦 **已歸檔**{f'（{at}）' if at else ''}"
                     f"{f'　commit `{sha}`' if sha else '　⚠ 沒有記 archived_commit —— 接手的人不知道去哪顆 commit 找'}")
        lines.append("   ⇒ **這不是「沒有記憶」** —— 正文照樣在下面, 它只是不再被維護了。")
    lines.extend(describe_related_tasks(card))
    if card.get("key_docs"):
        lines.append(f"   📚 權威文件: {', '.join(card['key_docs'])}")
        related_docs.extend(card["key_docs"])
    if card.get("related_topics"):
        lines.append(f"   ↔ 關聯主題: {', '.join(card['related_topics'])}")
    if card.get("_body"):
        lines.extend(("", card["_body"], ""))

    frags = [f for f in list_fragments(args.topic) if f.get("type") in want_types]
    linked_refs: list[str] = []
    for f in frags:
        status = f.get("status", "active")
        if status != "active":
            lines.append(f"--- ~~{f.get('id')}~~ [{status}]（正文略; 取代鏈見 links: {f.get('links')}）")
            continue
        lines.append(f"--- [{f.get('type')}] {f.get('title')}  (id: {f.get('id')}, by {f.get('created_by')} @ {f.get('created_at')})")
        if f.get("related_docs"):
            lines.append(f"    📚 {', '.join(f['related_docs'])}")
            related_docs.extend(f["related_docs"])
        if f.get("links"):
            lines.append(f"    ↔ {', '.join(f['links'])}")
            linked_refs.extend(f["links"])
        lines.extend((f.get("_body", ""), ""))

    # --with-links: 1-hop 拉關聯 fragment（跨主題）— 「讀取時根據情況一起讀關聯記憶」
    if args.with_links and linked_refs:
        seen = {f"{args.topic}/{f.get('id')}" for f in frags}
        lines.append("═══ 關聯記憶（1-hop, --with-links）═══")
        for ref in dict.fromkeys(linked_refs):   # 去重保序
            if ref in seen:
                continue
            seen.add(ref)
            try:
                t, fid = _parse_ref(ref)
            except ValueError:
                continue
            frag = next((x for x in list_fragments(t) if x.get("id") == fid), None)
            if frag is None:
                lines.append(f"--- ⚠ 關聯目標不存在（dangling, 可能是未來主題）: {ref}")
                continue
            lines.append(f"--- [來自 {t}] [{frag.get('type')}] {frag.get('title')}  (id: {frag.get('id')})")
            related_docs.extend(frag.get("related_docs") or [])
            lines.extend((frag.get("_body", ""), ""))

    brief_path = save_read_brief(args.topic, args.with_links, args.types, "success", lines, related_docs)
    lines.extend((f"📄 共讀 briefing: {brief_path.relative_to(REPO_ROOT)}（請開啟此檔繼續讀取）", ""))
    print("\n".join(lines))
    return 0


# ===========================================================
# op: tasks — 主題卡的 task_indices 寫入端（契約①：記憶側這一格歸 python）
# ===========================================================
def op_tasks(args) -> int:
    card = load_topic_card(args.topic)
    if card is None:
        print(f"✗ 主題不存在: {args.topic}")
        return 2
    before = _int_list(card.get("task_indices"))
    if args.set:
        after = _int_list(args.set.split(","))
    else:
        after = list(before)
        after += _int_list((args.add or "").split(","))
        drop = set(_int_list((args.remove or "").split(",")))
        after = [i for i in dict.fromkeys(after) if i not in drop]
    after = sorted(dict.fromkeys(after))
    if not (args.set or args.add or args.remove):
        print(f"🔗 {args.topic} 目前的 task_indices: {before or '（空）'}")
        print("\n".join(describe_related_tasks(card)))
        return 0
    card["task_indices"] = [str(i) for i in after]
    save_topic_card(card)
    # 回讀確認 —— 寫入成功不等於讀得回來
    reread = _int_list((load_topic_card(args.topic) or {}).get("task_indices"))
    print(f"✅ task_indices: {before or '（空）'} → {reread or '（空）'}（回讀自 _topic.md）")
    if reread != after:
        print(f"⚠ 回讀值與預期不符（預期 {after}）—— 有第二個寫入者, 或 frontmatter 解析漏了")
        return 1
    print("\n".join(describe_related_tasks(load_topic_card(args.topic))))
    print("\n📌 契約①提醒：Task 側的 `memory_topic` 由 Cmd_Task 寫, **本工具不碰它**。"
          "\n   要補另一半：`run_cmd.py run Task --arg op=update --arg index=<n> "
          f"--arg memory_topic={args.topic}`")
    return 0


# ===========================================================
# op: archive — 主題退場（status → archived）, 前置 git 守衛
# ===========================================================
def op_archive(args) -> int:
    card = load_topic_card(args.topic)
    if card is None:
        print(f"✗ 主題不存在: {args.topic}")
        return 2
    cur = str(card.get("status", "")).strip()
    target = "active" if args.undo else "archived"
    if cur == target:
        print(f"⚠ {args.topic} 已經是 `{target}` —— 什麼都沒做")
        return 0

    if not args.undo:
        # ① 未關的關聯單 ⇒ 警示不擋（PM 判斷; 硬擋會讓沒建反向索引的主題永遠歸不了檔）
        idx = _int_list(card.get("task_indices"))
        open_tasks = [i for i in idx if (TASKS_ROOT / f"{i:04d}.md").is_file()
                      and str(parse_frontmatter((TASKS_ROOT / f"{i:04d}.md")
                                                .read_text(encoding="utf-8"))[0]
                              .get("status", "")).strip() not in CLOSED_TASK_STATUS]
        if open_tasks:
            print("⚠ 這個主題還有未關的關聯單：")
            for i in open_tasks:
                print("    · " + _describe_task(i))
            print("  ⇒ **警示不是擋**（歸檔是 PM 的判斷）。但接手的人會拿不到「上次做到哪」。")
        elif not idx:
            print("⚠ 這個主題**沒有建過反向索引**（task_indices 是空的）"
                  " ⇒ 我無法替妳檢查「相關 Task 是不是都關了」。這不是「都關了」, 是**沒有讀數**。")

        # ② git 前置守衛 —— 血證驅動: 「反正 git 有」是一個需要被驗的前提
        d = topic_dir(args.topic)
        clean, detail = git_dir_status(d)
        if not clean:
            print(f"\n🛑 **擋下**：`{d.relative_to(REPO_ROOT)}` 在 git 裡不乾淨 ⇒ 不歸檔。")
            print("   實際的 git status（--porcelain --untracked-files=all）：")
            print("\n".join("     " + ln for ln in (detail.splitlines() or ["（空 —— 但上面說不乾淨, 這本身就是要看的訊號）"])))
            print("   ⇒ 先 commit 再來。**「刪掉也沒關係, git 有」不是狀態, 是一個需要被驗的前提。**")
            print("   （🩸 2026-08-24：四筆記憶寫完當下是 untracked, 那句話當時是假的）")
            return 3
        print(f"✅ git 守衛：`{d.relative_to(REPO_ROOT)}` 乾淨（無 modified／staged／untracked）")

    sha = (args.commit or "").strip() or (git_head_sha(topic_dir(args.topic)) if not args.undo else "")
    card["status"] = target
    if args.undo:
        card.pop("archived_at", None)
        card.pop("archived_commit", None)
    else:
        card["archived_at"] = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
        card["archived_commit"] = sha or ""
        if not sha:
            print("⚠ 拿不到 HEAD sha ⇒ `archived_commit` 留空。"
                  "接手的人會知道它是空的（而不是被塞一個假 sha）")
    save_topic_card(card)
    reread = load_topic_card(args.topic) or {}
    print(f"✅ {args.topic}: `{cur}` → `{reread.get('status')}`（回讀自 _topic.md）"
          + (f"　commit `{reread.get('archived_commit')}`" if reread.get("archived_commit") else ""))
    if not args.undo:
        print("\n📌 契約①提醒：Task 側那一格**我不寫**。要讓回看單子的人接得回來, 對每一張關聯單跑：")
        print(f"   `run_cmd.py run Task --arg op=update --arg index=<n> --arg memory_archived_commit={sha or '<sha>'}`")
        print("   ⇒ 沒補這一格的話, `op=show` 會印「⚠ 指向一個不存在的主題」而不是「📦 已歸檔」——"
              "\n     兩者都不是謊, 但後者才是真的。")
    return 0


# ===========================================================
# op: delete — 主題刪除, **強制留墓碑**
# ===========================================================
def op_delete(args) -> int:
    d = topic_dir(args.topic)
    card = load_topic_card(args.topic)
    if card is None:
        print(f"✗ 主題不存在: {args.topic}")
        return 2
    clean, detail = git_dir_status(d)
    if not clean:
        print(f"🛑 **擋下**：`{d.relative_to(REPO_ROOT)}` 在 git 裡不乾淨 ⇒ 不刪。")
        print("   實際的 git status：")
        print("\n".join("     " + ln for ln in (detail.splitlines() or ["（空）"])))
        print("   ⇒ **刪除是不可逆的**, 而沒入版控的內容刪掉就真的沒了。")
        return 3
    sha = (args.commit or "").strip() or git_head_sha(d)
    if not args.confirm:
        print(f"🛑 **dry-run**（沒帶 --confirm）⇒ 什麼都沒刪。")
        print(f"   git 守衛已通過（目錄乾淨, HEAD `{sha[:9] or '?'}`）—— 上面這一格是真的讀數。")
        print(f"   要真的刪：同一道指令加 `--confirm`。")
        return 0
    n = len(list(d.glob("*.md")))
    import shutil
    shutil.rmtree(d)
    TOMBSTONE_PATH.parent.mkdir(parents=True, exist_ok=True)
    if not TOMBSTONE_PATH.exists():
        TOMBSTONE_PATH.write_text(
            "# 工作記憶 — 墓碑（append-only）\n\n"
            "> **刪除可以, 失聯不行。** 每一行指向那個主題最後存在的 commit。\n"
            "> 這個檔以 `_` 開頭 ⇒ topics/index 的目錄掃描天生略過它。\n\n", encoding="utf-8")
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    with TOMBSTONE_PATH.open("a", encoding="utf-8") as fp:
        fp.write(f"- `{args.topic}` 「{card.get('title', '')}」 — 刪於 {stamp} by "
                 f"{args.by or 'unknown'}；{n} 個檔的內容在 commit `{sha or '（拿不到 HEAD sha）'}`\n")
    print(f"🪦 已刪除 `{args.topic}`（{n} 個檔）並留墓碑：{TOMBSTONE_PATH.relative_to(REPO_ROOT)}")
    print(f"   內容在 commit `{sha or '?'}` —— **刪除可以, 失聯不行。**")
    print(f"\n📌 對每一張關聯單補上墓碑指標（契約①：那一格歸 Cmd_Task 寫）：")
    print(f"   `run_cmd.py run Task --arg op=update --arg index=<n> --arg memory_archived_commit={sha or '<sha>'}`")
    return 0


# ===========================================================
# main
# ===========================================================
def main() -> int:
    ap = argparse.ArgumentParser(description="工作記憶區 CLI")
    sub = ap.add_subparsers(dest="op", required=True)

    sub.add_parser("topics")
    p = sub.add_parser("init")
    p.add_argument("--topic", required=True)
    p.add_argument("--title", required=True)
    p.add_argument("--desc", default="")
    p = sub.add_parser("add")
    p.add_argument("--topic", required=True)
    p.add_argument("--type", required=True, choices=FRAGMENT_TYPES)
    p.add_argument("--id", required=True)
    p.add_argument("--title", required=True)
    p.add_argument("--body", default="")
    p.add_argument("--body-file", dest="body_file", default="")
    p.add_argument("--links", default="")
    p.add_argument("--docs", default="")
    p.add_argument("--by", default="")
    p = sub.add_parser("read")
    p.add_argument("--topic", required=True)
    p.add_argument("--with-links", dest="with_links", action="store_true")
    p.add_argument("--types", default="")
    p = sub.add_parser("supersede")
    p.add_argument("--topic", required=True)
    p.add_argument("--id", required=True)
    p.add_argument("--by", default="")
    p.add_argument("--new-id", dest="new_id", default="")
    p.add_argument("--new-title", dest="new_title", default="")
    p.add_argument("--new-body", dest="new_body", default="")
    p.add_argument("--new-body-file", dest="new_body_file", default="")
    p.add_argument("--new-by", dest="new_by", default="")
    p = sub.add_parser("link")
    p.add_argument("--from", required=True)
    p.add_argument("--to", required=True)
    p = sub.add_parser("index")
    p.add_argument("--topic", default="")
    # ── TASK-0017：反向索引 / 歸檔 / 刪除 ──────────────────────────
    p = sub.add_parser("tasks", help="主題卡的 task_indices（反向索引）：不帶動作＝只印現況")
    p.add_argument("--topic", required=True)
    p.add_argument("--set", default="", help="整組覆寫，如 8,15,17")
    p.add_argument("--add", default="")
    p.add_argument("--remove", default="")
    p = sub.add_parser("archive", help="主題退場（status→archived）；前置 git 守衛")
    p.add_argument("--topic", required=True)
    p.add_argument("--commit", default="", help="不給則取 HEAD")
    p.add_argument("--undo", action="store_true", help="改回 active（不跑 git 守衛：它只保護退場方向）")
    p = sub.add_parser("delete", help="刪除主題並留墓碑；不帶 --confirm 是 dry-run")
    p.add_argument("--topic", required=True)
    p.add_argument("--commit", default="")
    p.add_argument("--by", default="")
    p.add_argument("--confirm", action="store_true")

    args = ap.parse_args()
    return {"topics": op_topics, "init": op_init, "add": op_add, "read": op_read,
            "supersede": op_supersede, "link": op_link, "index": op_index,
            "tasks": op_tasks, "archive": op_archive, "delete": op_delete}[args.op](args)


if __name__ == "__main__":
    sys.exit(main())
