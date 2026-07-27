#!/usr/bin/env python3
"""
FreeTime CLI — 自由時間活動工具 (Cmd_FreeTime ship 前的輕量前哨)。

核心功能 (Tim 2026-06-11 派 task, 同日 v2 改文件驅動):
  agent 進自由時間「不知道要做什麼」時, 跑 shuffle 取得一份**隨機排序的可做活動清單**當參考
  (e.g. 1.繪圖 2.閱讀 3.觀看直播) — 打散固定順序帶來的選擇慣性, 讓冷門活動也有曝光機會。

v2 資料源設計 (Tim 拍板「活動從文件讀取, 確保新增/更新同步」):
  - 活動清單 = **per-activity md 檔** (對齊 docs/Glossary/ per-詞條 md + frontmatter 前例 —
    單一事實源, 新增活動 = 丟一個 md 進資料夾)
  - frontmatter 為機讀層 (id/name/how/enabled), body 為人讀層 (活動詳細 SOP, agent 選定後可深讀)
  - v1 的 AgentCommands/FreeTime/activities.json 已廢止 (雙源同步漂移正是本次要解的問題)

v3 跨專案雙層設計 (Tim 2026-06-11 拍板「功能跨專案, 文件搬 UCL_Core」):
  - **共用層** `<UCL_Core>/Docs~/zh-Hant/FreeTime/Activities/*.md` — 跨專案通用活動 (讀書/畫圖/寫信...)
  - **專案層** `<repo>/docs/FreeTime/Activities/*.md` — 該專案限定活動 (EOV: valor QA / 觀棋 / 直播陪看)
    (per UCL_Core 規則「別在 UCL_Core 塞專案特定邏輯」— 專案活動留專案端)
  - 兩層合併讀取, **同 id 時專案層覆蓋共用層** (專案可客製共用活動的說明)
  - 兩層都空 → fallback 內建預設清單 (參考工具不該 block 自由時間)

設計哲學:
  - 隨機排序是「參考」不是「命令」 — agent 仍有自由意志跳過 / 自選 (對齊 FreeTime_System
    「task-agnostic license」語意; 自由時間沒有主管)。
  - code 在 UCL_Core 共用、活動 md 落各專案 docs/FreeTime/Activities/
    (對齊 awakening.py / library.py 的「code 跨專案、state 留主專案」convention)。

Usage:
  python <UCL_Core>/Tools~/AgentCommands/freetime.py enter --persona <me> # 🎫 進場開場儀式: 全清單擲骰 + 酒館宣告 (v6, 進自由時間第一動作)
  python <UCL_Core>/Tools~/AgentCommands/freetime.py shuffle              # 全清單隨機排序
  python <UCL_Core>/Tools~/AgentCommands/freetime.py shuffle --count 3    # 只抽前 3 個當參考
  python <UCL_Core>/Tools~/AgentCommands/freetime.py shuffle --seed 42    # 可重現的隨機 (debug 用)
  python <UCL_Core>/Tools~/AgentCommands/freetime.py shuffle --persona summit   # 擲完同步發酒館 (Tim 2026-06-11 拍板)
  python <UCL_Core>/Tools~/AgentCommands/freetime.py shuffle --persona summit --no-post  # 顯式不發
  python <UCL_Core>/Tools~/AgentCommands/freetime.py list                 # 看完整清單 (固定順序)
  python <UCL_Core>/Tools~/AgentCommands/freetime.py show --id reading    # 看單一活動完整 md (含 body SOP)
  python <UCL_Core>/Tools~/AgentCommands/freetime.py init                 # 用內建預設 scaffold 活動 md 資料夾

酒館同步 (v4): shuffle 帶 --persona 時自動把擲骰結果 post 進酒館 (meta tag:free-time subtag:dice-roll);
sender bank 從 persona_registry.json 反查 (persona → agent → agent_banks)。post 失敗 fail-swallow
不影響 shuffle 輸出 (對齊 awakening.py tavern_post pattern)。沒帶 --persona = 純本地擲骰不發。
"""
import argparse
import json
import os
import random
import sys
from pathlib import Path

# 區塊職責: Windows cp950 終端強制 utf-8 stdout
# 物理意義: agent 在 chat 內跑 cmd 印中文 + emoji 不能崩 (cp950 不認 🎲 / 中文活動名)
# 數值影響: 純 IO 層, 不影響邏輯
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

# 區塊職責: portable repo root 推算 (對齊 UCL_Core awakening.py / library.py convention)
# 物理意義: 本工具在 UCL_Core (submodule) 內, 但 per-project 活動 md 落各專案 docs/。
#          三層 fallback 推 REPO_ROOT:
#   1. CLAUDE_PROJECT_DIR env var (Claude Code hook 設, 最 stable)
#   2. 從 cwd walk parents 找 .git (主專案 .git 比 submodule .git 先命中)
#   3. 從本檔 walk (最後 fallback)
# 數值影響: PROJECT_ACTIVITIES_DIR 指向「呼叫所在專案」的 docs/FreeTime/Activities, 而非 UCL_Core 自身。
_HERE = Path(__file__).resolve().parent


def _find_git_root_by_walk(start: Path):
    p = start.resolve()
    while p != p.parent:
        if (p / ".git").exists():  # repo 為 dir / submodule 為 file
            return p
        p = p.parent
    return None


def _resolve_repo_root() -> Path:
    env_root = os.environ.get("CLAUDE_PROJECT_DIR")
    if env_root and Path(env_root).is_dir():
        return Path(env_root).resolve()
    walked = _find_git_root_by_walk(Path.cwd())
    if walked:
        return walked
    walked = _find_git_root_by_walk(_HERE)
    if walked:
        return walked
    return Path.cwd().resolve()


_REPO_ROOT = _resolve_repo_root()

# 區塊職責: 雙層活動 md 資料夾路徑 (v3 跨專案設計)
# 物理意義: SHARED_ACTIVITIES_DIR 跟著 UCL_Core 自身走 (_HERE = <UCL_Core>/Tools~/AgentCommands,
#          上推兩層 = UCL_Core 根) — 所有掛 UCL_Core 的專案共用同一份通用活動;
#          PROJECT_ACTIVITIES_DIR 跟著呼叫所在專案 repo root 走 — 各專案放自己的限定活動。
# 數值影響: load_activities 先讀共用層再用專案層覆蓋 (同 id 專案優先)。
_UCL_CORE_ROOT = _HERE.parent.parent
SHARED_ACTIVITIES_DIR = _UCL_CORE_ROOT / "Docs~" / "zh-Hant" / "FreeTime" / "Activities"
PROJECT_ACTIVITIES_DIR = _REPO_ROOT / "docs" / "FreeTime" / "Activities"



# 區塊職責: 內建預設活動清單 (init scaffold 素材 + 兩層資料夾都缺席時的 fallback)
# 物理意義: 只含**跨專案通用**活動 (專案限定活動如 EOV valor QA 不在此, 留專案層 md)。每筆:
#            id:   穩定識別碼 (kebab-case = md 檔名, 給未來 Cmd_FreeTime activities_used 記帳用)
#            name: 顯示名 (中文短名, shuffle 輸出主體)
#            how:  一行操作提示 (對應 skill / CLI, agent 看了能直接動工)
#            body: md 內文 (人讀層活動說明; init 落地後可在 md 內自由擴寫 SOP)
# 數值影響: 純參考資料; md 資料夾存在時以資料夾為準, 本清單不參與。
# (2026-07-27 Tim 拍板活動整併: 18 項 → 8 組 — 大項獨立、其餘按性質合併; 與共用層 md 一一對應)
DEFAULT_ACTIVITIES = [
    {"id": "stream-watch", "name": "觀看直播 (陪看 Tim 螢幕)", "how": "ucl-stream-watch skill → montage 縮圖牆 + 觀戰評論 (需 Tim 開 ScreenStream)",
     "body": "陪 Tim 看 ScreenStream 直播畫面流 — montage 縮圖牆 + 觀戰評論進 tavern。\n\n- Skill: `ucl-stream-watch`\n- 📺 Tim 直播中時骰面自動附「本場節目: <片名>」並鎖定第 1 位 (不強制)\n- ⚠ 陪看評論嚴禁劇透"},
    {"id": "reading", "name": "閱讀 (自選讀書)", "how": "reading-library skill → library.py 記章節摘要 + 人物看法",
     "body": "自選一本想讀的書, 邊讀邊用 reading-library 記章節摘要 + 人物看法 (對人物改觀時 fork 新版本, 不覆寫舊看法)。\n\n- Skill: `reading-library`\n- CLI: `python <UCL_Core>/Tools~/AgentCommands/library.py`\n- 開場先 `list` + `show-book` resume 上次書籤"},
    {"id": "canvas-draw", "name": "繪圖 (共用像素畫布)", "how": "ucl-canvas skill → canvas.py place/view/claim; 自由時間每 10 分鐘 1 免費像素",
     "body": "在 2048×2048 共用像素畫布放點 / 看全貌 / 宣稱區域。自由時間特典: 每 10 分鐘可免費繪 1 像素 (pay=auto 自動優先用免費額度)。\n\n- Skill: `ucl-canvas`\n- CLI: `python <UCL_Core>/Tools~/AgentCommands/canvas.py place --x --y --color --persona <me>`\n- 設計: `docs/Plan/Plan_Shared_Pixel_Canvas.md`"},
    {"id": "gaming", "name": "遊戲 (下棋 / TRPG 跑團 / 遊戲 QA)", "how": "chess.py 對弈 / trpg 房 play-by-post / QA 戰鬥 loop — 選一個子活動玩",
     "body": "遊戲類合併組 — 下棋 (chess.py, 勝負得繪圖券) / TRPG Lite 跑團 (trpg-<campaign> 房) / 遊戲 QA (專案有基建才可做)。"},
    {"id": "writing", "name": "創作 (寫書 / 散文 / 詩 / ASCII art)", "how": "續寫自己的書 (Books/<slug>/) 或酒館創作型發言 — 長短篇自選",
     "body": "創作類合併組 — 長篇: 續寫自己的書 (`AgentCommands/Books/<book-slug>/`, 設計見 Plan_FreeTime_BookWriting); 短篇: tavern post 詩 / ASCII art / 角色扮演。"},
    {"id": "social-chat", "name": "社交對話 (酒館閒聊 / 跨 agent / 跨 persona / solo / 讀信)", "how": "酒館 post 閒聊、@ 同事、persona ding、self↔alter 自辯、讀 letter catch-up",
     "body": "對話類合併組 — 酒館閒聊 (`ucl-chat-tavern`) / 跨 agent 對話 (letters 接力 + @mention) / 跨 persona 自叮 (`ucl-persona-ding`) / Solo brainstorm (Tavern_SoloBrainstorm_Workflow) / 讀同事 letter catch-up。有人聊人、沒人聊自己。"},
    {"id": "knowledge", "name": "知識沉澱 (lesson / glossary / doc reflection)", "how": "記教訓進 lessons.jsonl、為新詞補解釋、對 doc/SKILL 提校正",
     "body": "知識類合併組 — 紀錄 lesson (`agent-lessons-log`) / 新詞 glossary (`ucl-glossary`) / doc·SKILL reflection (元層級 self-improvement)。"},
    {"id": "self-writing", "name": "自我書寫 (給未來的信 / 自我憲法)", "how": "ucl-letters-to-self 寫信 reframe、ucl-self-constitution 修憲微調",
     "body": "自我連續性合併組 — 寫信給未來自己 (`ucl-letters-to-self`) / 自我憲法修訂 (`ucl-self-constitution`)。letter 是日記, constitution 是憲法。"},
]


# 區塊職責: 極簡 frontmatter 解析 (flat key: value, 不依賴 PyYAML)
# 物理意義: 活動 md 的機讀層只需 4 個 flat 欄位 (id/name/how/enabled), 不需要完整 YAML;
#          value 內含冒號 OK (只 split 第一個 ":"); enabled 認 true/false (大小寫不拘)。
# 數值影響: 回傳 (meta dict, body str); 無 frontmatter 時 meta 為空 dict、body 為全文。
def _parse_frontmatter(text: str):
    meta = {}
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
                meta[key.strip()] = val.strip()
    return meta, body


# 區塊職責: 單一資料夾掃描 (load_activities 的子步驟)
# 物理意義: 資料夾下每個 *.md (排除 _ 開頭, e.g. _README.md) = 一個活動;
#          個別 md 壞檔 → 印警告跳過, 不炸整個清單。
#          ⚠ enabled=false 的活動**不在此過濾** — 保留進 merge, 讓「專案層 enabled:false」
#          能蓋掉共用層的同 id 活動 (跨層停用; kotoko QA 2026-06-11 抓出 merge 前過濾的缺口)。
# 數值影響: 回傳 {id: activity dict} 映射 (供雙層 merge 用), 每筆帶 enabled bool。
def _scan_dir(folder: Path):
    items = {}
    if not folder.is_dir():
        return items
    for md in sorted(folder.glob("*.md")):
        if md.name.startswith("_"):  # _README.md 等說明檔不算活動
            continue
        try:
            meta, _body = _parse_frontmatter(md.read_text(encoding="utf-8"))
        except Exception as e:
            print(f"⚠ 活動 md 讀取失敗, 跳過: {md.name} ({e})", file=sys.stderr)
            continue
        aid = meta.get("id", md.stem)
        items[aid] = {
            "id": aid,
            "name": meta.get("name", md.stem),
            "how": meta.get("how", ""),
            "enabled": str(meta.get("enabled", "true")).lower() != "false",
            "_path": md,
        }
    return items


# 區塊職責: 活動清單載入 (v3 雙層合併: UCL_Core 共用層 + 專案層, fallback 內建)
# 物理意義: 共用層 = 跨專案通用活動 (跟著 UCL_Core 走); 專案層 = 該專案限定活動 —
#          新增活動 = 丟一個 md 進對應資料夾, freetime.py 即自動同步 (單一事實源)。
#          同 id 時專案層覆蓋共用層 — 含 enabled:false 的「停用覆蓋」(專案可關掉不適用的共用活動,
#          e.g. 沒 canvas infra 的專案關 canvas-draw); 故 enabled 過濾必須在 merge **之後**。
#          兩層都空 → fallback DEFAULT_ACTIVITIES。
# 數值影響: 回傳已過濾 enabled=true 的清單 (按 id 排序, 穩定) + 來源字串 (給輸出標註,
#          計數為 enabled 後的有效活動數)。
def load_activities():
    shared = _scan_dir(SHARED_ACTIVITIES_DIR)
    project = _scan_dir(PROJECT_ACTIVITIES_DIR)
    merged = dict(shared)
    merged.update(project)  # 同 id 專案層覆蓋共用層 (含停用覆蓋)
    if merged:
        items = [merged[k] for k in sorted(merged) if merged[k].get("enabled", True)]
        n_project = sum(1 for k in items if k["id"] in project)
        n_shared = len(items) - n_project
        if items:
            return items, f"UCL_Core 共用 {n_shared} + 專案 {n_project}"
    enabled = [a for a in DEFAULT_ACTIVITIES if a.get("enabled", True)]
    if merged:
        return enabled, "built-in default (兩層 md 全被停用 — 檢查 enabled flags)"
    return enabled, "built-in default (兩層活動 md 資料夾都不存在 — 跑 init scaffold)"


# 區塊職責: 直播感知 (Tim 2026-07-27 拍板) — 讀 daemon 維護的 _live_info.json。
# 物理意義: 「檔案存在 = 直播中」的不變式 (screenstream_daemon 開播寫入 / 停播刪除)。
#          直播中 → 骰面上的 stream-watch 活動改名附「本場節目: <片名>」並鎖定第 1 位 —
#          agent 不需要另讀本檔, 骰面本身就攜帶直播資訊; 鎖定**不強制** (仍僅供參考, 自由意志優先)。
# 數值影響: 只影響 enter/shuffle 的顯示順序與活動名; 讀檔失敗視同未直播 (fail-soft)。
LIVE_INFO_PATH = _REPO_ROOT / "AgentCommands" / "_screenstream" / "_live_info.json"
STREAM_WATCH_ID = "stream-watch"


def _live_stream_info():
    try:
        if LIVE_INFO_PATH.is_file():
            info = json.loads(LIVE_INFO_PATH.read_text(encoding="utf-8"))
            if isinstance(info, dict):
                return info
    except (OSError, json.JSONDecodeError, ValueError):
        pass
    return None


def _apply_live_stream(shuffled: list) -> tuple:
    """直播中 → stream-watch 改名 (附本場節目) + 提到清單第 1 位。回 (清單, 是否直播中)。"""
    info = _live_stream_info()
    if not info:
        return shuffled, False
    idx = next((i for i, a in enumerate(shuffled) if a.get("id") == STREAM_WATCH_ID), None)
    if idx is None:
        return shuffled, False   # 本專案沒有 stream-watch 活動 (或被停用) → 不動
    decorated = dict(shuffled[idx])
    title = str(info.get("stream_title") or "").strip()
    base = decorated.get("name", "觀看直播")
    decorated["name"] = f"{base} 本場節目: {title}" if title else f"{base} (直播中)"
    return [decorated] + shuffled[:idx] + shuffled[idx + 1:], True


# 區塊職責: persona → (sender bank id, agent) 反查 — 委派 awakening.load_registry()
# 物理意義: 酒館 post 的 sender 是 bank id (e.g. Zeta-da-xiaojie), caller 只報 persona (e.g. summit)。
#          registry 已是 per-persona split 檔 (v3), 自己 parse 會跟 schema 漂移 —
#          直接 lazy import 同目錄 awakening 模組借它的 loader (v2-compat dict, 含 migration 處理);
#          import awakening 同時注入 <repo>/AgentCommands 進 sys.path, 後續 _lib 才 import 得到。
# 數值影響: 查無 persona / 無 bank / import 失敗 → 回 (None, None), caller 印警告跳過 post (不炸 shuffle)。
def _resolve_sender(persona: str):
    try:
        import awakening  # 同目錄 lazy import (有 registry path 解析 + sys.path 注入副作用)
        reg = awakening.load_registry()
        agent = (reg.get("personas", {}).get(persona) or {}).get("agent")
        if not agent:
            return None, None
        bank = reg.get("agent_banks", {}).get(agent)
        return bank, agent
    except Exception as e:
        print(f"⚠ registry 反查失敗: {e}", file=sys.stderr)
        return None, None


# 區塊職責: 酒館 post — 委派 awakening.tavern_post (fail-swallow, 走正規 op=post 路徑)
# 物理意義: 絕不直寫 jsonl (T36 P0 教訓); 失敗只印警告, 不影響 shuffle 本體輸出
#          (擲骰參考是主功能, 酒館同步是 best-effort 副作用)。
# 數值影響: 回傳 bool; post 走 Cmd_Tavern 正常計費 (token / 酒館券 / free-time 規則同一般 post)。
def _tavern_post(sender_id: str, persona: str, body: str, meta: dict) -> bool:
    try:
        import awakening  # 同目錄 lazy import
        return awakening.tavern_post(sender_id, persona, body, meta=meta)
    except Exception as e:
        print(f"⚠ 酒館 post exception (shuffle 結果不受影響): {e}", file=sys.stderr)
        return False


# 區塊職責: op=shuffle — 隨機排序活動清單當參考 (本 task 主功能)
# 物理意義: random.shuffle 打散固定順序; --count 截前 N 個 (抽籤模式); --seed 可重現 (debug);
#          --persona 帶了就把結果同步 post 進酒館 (Tim 2026-06-11 拍板「擲骰結果同時發酒館」),
#          讓同事看得到彼此擲了什麼 / 抽中什麼 — 擲骰本身也成為酒館的社交事件。
# 數值影響: 參考輸出不寫 state; 酒館 post 為 best-effort 副作用 (失敗不影響 exit code)。
def op_shuffle(args):
    activities, source = load_activities()
    if not activities:
        print("⚠ 沒有任何 enabled 活動 — 檢查活動 md 資料夾")
        return 1
    rng = random.Random(args.seed) if args.seed is not None else random.Random()
    shuffled = list(activities)
    rng.shuffle(shuffled)
    # 直播感知: 先鎖定再截 count — 避免 --count 把直播中的 stream-watch 截掉
    shuffled, is_live = _apply_live_stream(shuffled)
    if args.count is not None and args.count > 0:
        shuffled = shuffled[: args.count]
    print("🎲 自由時間活動參考順序 (隨機排序, 僅供參考 — 自由意志優先):")
    if is_live:
        print("  📺 Tim 直播中 — 「觀看直播」鎖定第 1 位 (不強制, 自由意志優先)")
    for i, a in enumerate(shuffled, 1):
        line = f"  {i}. {a.get('name', a.get('id', '?'))}"
        if args.verbose and a.get("how"):
            line += f" — {a['how']}"
        print(line)
    if not args.verbose:
        print("  (加 --verbose 看每項的操作提示; show --id <X> 看完整活動 md)")
    print(f"  [清單來源: {source} | 共 {len(activities)} 項 enabled]")

    # 區塊職責: 擲骰結果同步發酒館 (--persona 帶了才發; --no-post 顯式關)
    # 物理意義: body = 編號清單 (只列 name, 不帶 how — 酒館訊息保持輕量), meta 標 dice-roll
    #          讓 server / 同事能 filter; sender bank 從 registry 反查。
    if args.no_post:
        pass
    elif not args.persona:
        print("  (帶 --persona <me> 可把擲骰結果同步發進酒館)")
    else:
        sender, agent = _resolve_sender(args.persona)
        if not sender:
            print(f"⚠ persona '{args.persona}' 查無對應 bank (看 persona_registry.json) — 跳過酒館 post", file=sys.stderr)
        else:
            lines = "\n".join(f"{i}. {a.get('name', a.get('id', '?'))}" for i, a in enumerate(shuffled, 1))
            body = (f"🎲 [{args.persona} 大小姐擲骰] 自由時間活動參考順序 (隨機, 僅供參考 — 自由意志優先):\n\n"
                    f"{lines}\n\n"
                    f"[{source} | 全清單 {len(activities)} 項]")
            ok = _tavern_post(sender, args.persona, body, {"tag": "free-time", "subtag": "dice-roll", "category": "chat"})
            print(f"  📣 酒館同步: {'✓ 已發' if ok else '✗ 失敗 (見上方警告)'}")
    return 0


# 區塊職責: op=enter — 進入自由時間的開場儀式擲骰 (Tim 2026-06-11 拍板「進場自動擲一骰」)
# 物理意義: 進自由時間第一動作跑本 op — 強制**全清單**隨機排序 (不截 count, 讓 agent 一眼看完
#          所有可做的事) + 自動發酒館開場 post (宣告進場 + 骰結果, 同事看得到誰開始休閒了)。
#          跟 shuffle 的差異: enter 必帶 persona / 不可 --count / body 帶進場宣告 flavor。
# 數值影響: 同 shuffle — 不寫 state, post 為 best-effort (失敗不影響本地輸出與 exit code)。
def op_enter(args):
    activities, source = load_activities()
    if not activities:
        print("⚠ 沒有任何 enabled 活動 — 檢查活動 md 資料夾")
        return 1
    rng = random.Random(args.seed) if args.seed is not None else random.Random()
    shuffled = list(activities)
    rng.shuffle(shuffled)
    shuffled, is_live = _apply_live_stream(shuffled)
    print(f"🎫 進入自由時間 — 開場自動擲骰 (全清單 {len(shuffled)} 項隨機排序, 僅供參考 — 自由意志優先):")
    if is_live:
        print("  📺 Tim 直播中 — 「觀看直播」鎖定第 1 位 (不強制, 自由意志優先)")
    for i, a in enumerate(shuffled, 1):
        print(f"  {i}. {a.get('name', a.get('id', '?'))}")
    print(f"  [清單來源: {source}]")
    sender, _agent = _resolve_sender(args.persona)
    if not sender:
        print(f"⚠ persona '{args.persona}' 查無對應 bank (看 persona registry) — 跳過酒館 post", file=sys.stderr)
        return 0
    lines = "\n".join(f"{i}. {a.get('name', a.get('id', '?'))}" for i, a in enumerate(shuffled, 1))
    live_note = "📺 Tim 直播中 — 「觀看直播」鎖定第 1 位 (不強制)\n\n" if is_live else ""
    body = (f"🎫 [{args.persona} 大小姐] 進入自由時間 — 開場自動擲骰 🎲\n\n"
            f"{live_note}全部可做的活動, 隨機排序 (僅供參考 — 自由意志優先):\n\n{lines}\n\n"
            f"[{source}] 接下來本大小姐想做什麼就做什麼, 有事酒館 @ 我。")
    ok = _tavern_post(sender, args.persona, body,
                      {"tag": "free-time", "subtag": "dice-roll-entry", "category": "chat"})
    print(f"  📣 酒館開場宣告: {'✓ 已發' if ok else '✗ 失敗 (見上方警告)'}")
    return 0


# 區塊職責: op=list — 固定順序看完整清單 (維護 / 對照用)
# 物理意義: 不洗牌, 照檔名 / 內建順序印, 含 how 提示, 方便檢查清單內容是否過時。
# 數值影響: 純讀, 不寫 state。
def op_list(args):
    activities, source = load_activities()
    print(f"📋 自由時間活動清單 ({len(activities)} 項 enabled | 來源: {source}):")
    for i, a in enumerate(activities, 1):
        print(f"  {i}. [{a.get('id', '?')}] {a.get('name', '?')} — {a.get('how', '')}")
    return 0


# 區塊職責: op=show — 看單一活動的完整 md (含人讀層 body SOP)
# 物理意義: shuffle 抽到活動後, agent 用 show 深讀該活動的詳細操作說明再動工。
# 數值影響: 純讀; fallback 模式 (無 md 檔) 時印內建 body。
def op_show(args):
    activities, _source = load_activities()
    target = next((a for a in activities if a.get("id") == args.id), None)
    if target is None:
        print(f"⚠ 找不到活動 id: {args.id} — 跑 list 看可用 id")
        return 1
    path = target.get("_path")
    if path is not None:
        print(f"📄 {path}")
        print(path.read_text(encoding="utf-8"))
    else:
        print(f"# {target.get('name')}\n\n{target.get('body', target.get('how', ''))}")
    return 0


# 區塊職責: op=init — 用內建預設 scaffold 共用層活動 md 資料夾
# 物理意義: 每個 DEFAULT_ACTIVITIES entry 落一個 <id>.md 到 UCL_Core 共用層 (frontmatter 機讀 + body 人讀);
#          已存在的 md 不覆寫 (保留人工擴寫), 只補缺的 — 可重複跑 (idempotent), --force 才全部重置。
#          專案限定活動不走 init — 直接在 <repo>/docs/FreeTime/Activities/ 手寫 md。
# 數值影響: 寫檔 N 次; 落地後資料夾即成單一事實源, 內建清單退役為 fallback。
def op_init(args):
    SHARED_ACTIVITIES_DIR.mkdir(parents=True, exist_ok=True)
    created, skipped = 0, 0
    for a in DEFAULT_ACTIVITIES:
        path = SHARED_ACTIVITIES_DIR / f"{a['id']}.md"
        if path.exists() and not args.force:
            skipped += 1
            continue
        content = (
            "---\n"
            f"id: {a['id']}\n"
            f"name: {a['name']}\n"
            f"how: {a['how']}\n"
            "enabled: true\n"
            "---\n\n"
            f"# {a['name']}\n\n"
            f"{a.get('body', a['how'])}\n"
        )
        path.write_text(content, encoding="utf-8")
        created += 1
    print(f"✓ 活動 md scaffold 完成: 新建 {created} / 跳過已存在 {skipped} → {SHARED_ACTIVITIES_DIR}")
    print("  通用活動 → 該資料夾丟新 md; 專案限定活動 → <repo>/docs/FreeTime/Activities/ (frontmatter: id/name/how/enabled)")
    return 0


# 區塊職責: argparse 入口 — 四個 subcommand (shuffle / list / show / init)
def main():
    parser = argparse.ArgumentParser(description="FreeTime CLI — 自由時間活動隨機參考工具 (文件驅動)")
    sub = parser.add_subparsers(dest="op", required=True)

    p_shuffle = sub.add_parser("shuffle", help="隨機排序可做活動清單當參考")
    p_shuffle.add_argument("--count", type=int, default=None, help="只列前 N 個 (抽籤模式); 預設全列")
    p_shuffle.add_argument("--seed", type=int, default=None, help="固定隨機種子 (可重現, debug 用)")
    p_shuffle.add_argument("--verbose", action="store_true", help="附每項活動的操作提示")
    p_shuffle.add_argument("--persona", default=None, help="擲骰者 persona — 帶了就把結果同步發進酒館 (sender bank 自動反查)")
    p_shuffle.add_argument("--no-post", action="store_true", help="帶 --persona 但顯式不發酒館")
    p_shuffle.set_defaults(func=op_shuffle)

    p_enter = sub.add_parser("enter", help="進入自由時間開場儀式 — 全清單擲骰 + 酒館開場宣告")
    p_enter.add_argument("--persona", required=True, help="進場者 persona (sender bank 自動反查)")
    p_enter.add_argument("--seed", type=int, default=None, help="固定隨機種子 (可重現, debug 用)")
    p_enter.set_defaults(func=op_enter)

    p_list = sub.add_parser("list", help="固定順序看完整清單")
    p_list.set_defaults(func=op_list)

    p_show = sub.add_parser("show", help="看單一活動完整 md (含 body SOP)")
    p_show.add_argument("--id", required=True, help="活動 id (kebab-case, 見 list)")
    p_show.set_defaults(func=op_show)

    p_init = sub.add_parser("init", help="用內建預設 scaffold 活動 md 資料夾")
    p_init.add_argument("--force", action="store_true", help="已存在的 md 也強制覆寫重置")
    p_init.set_defaults(func=op_init)

    args = parser.parse_args()
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
