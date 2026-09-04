#!/usr/bin/env python3
"""
T-AWAKE-01 awakening.py — Awakening Init Protocol CLI (MVP Python-only)

設計依據: docs/Plan/Plan_Awakening_Init_Protocol.md

整合三條設計線:
  - Cmd_GoodMorning (init + announce + fork) — subcommand "morning"
  - Cmd_Goodnight (letter + vector perturb + offline) — subcommand "goodnight"
  - Session identity consistency (env-based lock) — Phase 1

子命令:
  morning  --persona Z --model Y [--note "..."] [--fork-name NEW]
              喚醒登入 ritual. 寫 session lock + wake_count++ + tavern post.
              --persona 是**唯一**身分輸入；agent 由 registry 綁定反推，不是參數。
              --model 填 LLM 型號（不是 agent／平台名）；查不到底層型號就依 agent 填模糊值
                      （Codex→GPT / Antigravity→Gemini / claude-code→Claude）。
              (2026-07-31 Tim 拍板廢除 --agent / --explicit-persona / --strict-persona /
               --force-random / --rebind-agent；本段 2026-08-01 補正，先前仍寫著舊旗標。)

  goodnight --letter-body "..." [--perturbation 0.02] [--note "..."]
              睡前 ritual. 寫 letter / vector perturb / status=offline /
              tavern post (含 @同事們下線通知) / 移除 session lock.

  rest      --letter-body "..." [--persona Z] [--note "..."] [--no-notify]
              小歇片刻 ritual (compact-rest, Tim 2026-05-24). /compact 前寫 memory letter
              保留重要記憶避免遺忘。類似 goodnight 但**不登出**:
              不 perturb vector / 不 offline / 不 unlock / 不 wake_count++,只更新 last_active
              + 可選小歇 tavern 通知。compact 後讀 _latest.md 接續。見 ucl-compact-rest skill.

  relogin   --persona Z [--agent X] [--model Y] [--note "..."]
              續線 ritual (晚安後重新上線). 保留記憶, 不走 morning:
              status=online / relogin_count++ / 重建 lock+token / 輕量 tavern 通知.
              關鍵: 不 wake_count++、不 perturb vector、不 fork — identity 原封不動.
              用於『下線後同一延續者要接回, 不想被當新的一天重置』。

  status      列 environment + persona pool + wake_count (read-only, 不副作用).

  forks <persona>
              列某 persona 的 fork lineage tree.

  記憶五層 (Tim 2026-07-28 拍板; 見 Docs~/zh-Hant/Workflows/Awakening_Ritual_Workflow.md Step 8):
    見樹 T1   letters/<persona>/_latest.md            昨夜 1 封 (日記/抒發)
                                                      太短(<10 行)時 brief 會往前合併更早的信
    見叢 T1.5 letters/<persona>/_keys_open.md         當期交棒清單 (可勾銷/執行用)
    見林 T2   longterm/wake_<N>-<M>.md                ~10 夜反思濃縮
    見森 T3   longterm/forest/gen_<NNN>_*.md          見林 ≥ 3 份起, 之後每份新林折一代 (rolling fold)
    見根 T4   fragments/<type>_<slug>.md + _root_index.md   關鍵記憶片段 (唯一事實來源) + 機械索引
  對應 subcommand:
    consolidate --persona X [--digest-body ...] [--level linzi|forest]
              見林 digest (預設) / 見森 fold (--level forest); 不帶 body = inspect 模式.
              見林寫入後自動: 歸檔當期見叢 + 提示抽 fragment + 檢查見森門檻.
    root-index --persona X        見根: 掃 fragments/ 機械重建 _root_index.md (手改會被覆寫)
    keys --persona X [--add "…"]  見叢: append (隨時可加, 不限儀式) / 列出當期清單
    brief --persona X             ⛔ 已退場 (2026-09-04, TASK-0098) — 改跑 `senate cmd wake-brief`

範例:
  python awakening.py morning --persona basecamp --model claude-opus-5
  python awakening.py goodnight --letter-body "今天 ship 了 T-AWAKE-01 MVP..."
  python awakening.py status

注意 (per Tim 2026-05-12 拍板):
  - Tim 仍可在 goodnight 後叮喚醒 (session 物理活)
  - 多 session 同 persona 衝突 → 強制 fork (git branch 模型)
  - identity_vector ineffable, agent 不該 introspect 數字含義
"""
from __future__ import annotations

import argparse
import datetime
import difflib
import hashlib
import json
import math
import os
import random
import re
import shutil
import sys
import uuid
from pathlib import Path

# Windows utf-8
# line_buffering=True (2026-08-12, apex-one 報坑 → kaguya/summit/basecamp 三方同判 → Tim 拍板):
#   本工具幾乎都被 agent 用 pipe 呼叫(背景 Task / run_command / subprocess), 而 python 對
#   非 tty 的 stdout 預設是**整塊緩衝** —— 於是進度一行都不流出來, 呼叫端只看得到黑畫面,
#   等到 timeout 就當它 hang 住推進背景。實測(3.10.11): 子行程 print 後 sleep 3s,
#   呼叫端拿到第一行的時間 3.03s → 開了 line_buffering 之後 0.02s。
#   ⚠ 這只治「看不看得見進度」, 不縮短總時長; 真的跑超過呼叫端 timeout 照樣會被背景化。
#   為什麼修在這裡而不是叫每個呼叫端加 `-u`: 那是要 N 份 skill 文本 + 每個 agent 每次都記得,
#   漏一個就靜默退化。規則長在通道上, 不長在自覺上。
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace", line_buffering=True)
    sys.stderr.reconfigure(encoding="utf-8", errors="replace", line_buffering=True)
except Exception:
    pass

# ─── Paths ──────────────────────────────────────────────────────────────
# 區塊職責：portable repo root 推算 (對齊 UCL_Core run_cmd.py convention)
# 物理意義：本工具在 UCL_Core (submodule) 內, 但 per-project state files (registry / lock /
#          letters) 在主專案 cwd. 三層 fallback 推 REPO_ROOT:
#   1. CLAUDE_PROJECT_DIR env var (Claude Code hook 設, 最 stable)
#   2. walk parents 找 .git (跨平台穩定)
#   3. cwd (最後 fallback, 假設 caller 從 repo root 跑)
_HERE = Path(__file__).resolve().parent


# ⚠ 本檔不再自帶路徑解析 —— 全部委派 _lib/ucl_paths.py（Tim 2026-08-17 拍板 A/A1）。
# 顯式檔案路徑載入：裸 `_lib` 這個名字在本檔下方 sys.path.insert(0, <repo>/AgentCommands) 之後
# 會被「專案狀態側」的 _lib package 綁走（見下方區塊註解），走檔案路徑繞開名稱遮蔽。
import importlib.util as _ilu_paths


def _load_ucl_paths():
    spec = _ilu_paths.spec_from_file_location(
        "_ucl_paths_for_awakening", _HERE / "_lib" / "ucl_paths.py")
    mod = _ilu_paths.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


_paths = _load_ucl_paths()


# persona 讀取接縫（§8.7 單端解析）走 `_lib/seam` 共用 loader ——
# 本檔的 `_PP_MOD` 只是省一次 seam 載入，**實例唯一性由 seam 的 sys.modules 快取保證**
# （BUG-17：各檔各自 spec_from_file_location 會造出多份實例，每份各發一次 Cmd）。
_PP_MOD = None
# 在此**定義時**就把目錄釘成 Path，不在函式裡讀 `_HERE`（見下方註解的血證）。
_SEAM_DIR = Path(__file__).resolve().parent


def _persona_profile():
    global _PP_MOD
    if _PP_MOD is None:
        # ⚠ 用 _SEAM_DIR 而不是 _HERE：本函式在**呼叫時**才讀目錄，
        #   而檔尾曾經有一行 `_HERE = str(...)` 把它**重新綁成 str** ⇒ `str / str` 直接爆。
        #   那三行已隨 python wake brief 一起退場（2026-09-04, TASK-0098），
        #   但 _SEAM_DIR 這個寫法保留 —— 「定義時釘成 Path」本身就是對的，
        #   不因為陷阱拔了就把防守也拔掉。
        #   🩸 實測踩過：brief 回「persona 'Template' 不存在於 registry」，
        #   而真正的錯是 `unsupported operand type(s) for /: 'str' and 'str'` ——
        #   接縫 fail-soft 回空 dict，於是「讀取失敗」長得跟「沒有這個人」一模一樣。
        spec = _ilu_paths.spec_from_file_location(
            "_ucl_seam_loader_for_awakening", _SEAM_DIR / "_lib" / "seam.py")
        seam = _ilu_paths.module_from_spec(spec)
        spec.loader.exec_module(seam)
        # 這一步才是拿接縫本體；seam 以「絕對路徑」為 key 在 sys.modules 快取
        # ⇒ 不論本行程有幾個呼叫端、各自載了幾份 seam，接縫都只有一份（BUG-17）。
        _PP_MOD = seam.persona_profile()
    return _PP_MOD

# 🩸 repo root 的 tier 順序改用 ucl_paths 的語意（Tim 2026-08-17 拍板 A1）：
#   舊：CLAUDE_PROJECT_DIR → **cwd** walk → __file__ walk
#   新：CLAUDE_PROJECT_DIR → **__file__** walk → cwd walk
#   差在 tier-2。本專案兩者同解，但 **cwd 落在另一個 git repo 時會分歧** ——
#   例如 cwd=D:/Unity/persona/kiara（獨立 repo）時，舊語意會把登入態與信件寫進 kiara/AgentCommands。
#   ucl_paths 檔頭直接點名 cwd-walk 是「2026-06-16 cwd 路徑詐欺 bug 家族的病灶」。
#   ⇒ 代價：cd 進別的專案跑本工具**不再自動切換目標**，要切請顯式帶 CLAUDE_PROJECT_DIR。
_REPO_ROOT = _paths.repo_root()
_DATA_ROOT = _paths.data_root()

# ─── Path Config Override (legacy, Tim 2026-05-12 → 2026-05-28 deprecation) ─
# 區塊職責: tavern_paths.json 細粒度 override (registry/session/letters/etc.) — 已被 pointer 檔取代。
# 物理意義: 若殘留檔 → 仍 honor (transition window), 但印一次 deprecation warning,
#          Phase 後續移除。新方案走 pointer 檔 (整個資料根一次 override) + CLI 參數做 ad-hoc。
_PATH_CONFIG_PATH = _paths._path_config_file()
_resolve_data_path = _paths.resolve_data_path      # 委派：override 感知的唯一實作在 ucl_paths

_REGISTRY_PATH = _paths.registry_path()
_SESSION_DIR = _resolve_data_path("AgentCommands/_session", "session_dir")
_LETTERS_DIR_TPL = _paths.letters_root()
_BONUS_QUOTA_PATH = _resolve_data_path(
    "AgentCommands/ChatTavern/agent_bonus_quota.json", "bonus_quota_path"
)

# T07 (2026-05-15 apex-two) — Session Token 機制
# 物理意義: morning 發 token 進 lock + tokens.json 反查表; Cmd_Tavern enforce ON 時必驗 token,
#          擋住「誤 typo persona / sender 標籤」造成的選錯帳號 (Tim QA 2026-05-15)
# 數值影響: tokens.json schema = { tokens: { <token>: { persona, agent, ..., status } } }
#          enforce.json schema = { enforce: bool } — 後台開關 (預設 false, Tim 從 UCL_LoginStatusPage 切)
_TOKENS_PATH = _SESSION_DIR / "_tokens.json"
_ENFORCE_PATH = _SESSION_DIR / "_token_enforce.json"
# Memo: per-persona 私人 scratchpad — 跨 session persist, 不公開, 不進 tavern
_MEMOS_DIR_TPL = _resolve_data_path(
    "AgentCommands/ChatTavern/baton/memos", "memos_dir"
)

# Make _lib importable (TavernClient SDK 在主專案 _lib/, per-project state)
sys.path.insert(0, str(_REPO_ROOT / "AgentCommands"))

# ─── Constants ──────────────────────────────────────────────────────────
VECTOR_DIM = 64
VECTOR_RANGE = (-1.0, 1.0)
DEFAULT_PERTURBATION = 0.02
MAX_PERTURBATION = 0.2
# best-effort 下線廣播硬上限（2026-07-22 rec 1+3）：goodnight 核心（信/perturb/offline/解鎖）
# 都在廣播前落地，廣播純附帶。用短上限避免 Editor 卡住時阻塞到觸發外層 timeout（summit 遇的 SIGTERM 143）。
GOODNIGHT_BROADCAST_TIMEOUT_SEC = 12.0

# 其餘 ritual 廣播的硬上限（2026-08-12，Tim 拍板；summit 提、basecamp/kaguya/apex-one 同輪審）。
# 區塊職責：morning / intro / rest / relogin 四個 best-effort 廣播的等待上限。
# 物理意義：2026-07-22 只有 goodnight 吃到上限，其餘四處沿用 TavernClient 預設 60s ——
#          **不是無上限**（那是 summit 當日先報後更正的誤述），但 60s 仍高於呼叫端常見的
#          10s／背景化門檻，於是「看起來 hang」。四處同型卻只修一格＝修法的射程等於當初報案人的視野。
# 數值影響：30s。取值依據：本機實測整條 morning（lock→brief，含舊順序的廣播）約 10.2s，
#          取 ~3x headroom；同時遠低於呼叫端 120s，卡住時快速放棄而不拖死 ritual。
#          與 goodnight 的 12s 刻意不同值：下線廣播純禮貌，這四處（尤其 morning）是同事看到
#          「他上線了」的唯一一則訊息，值得多等一點，但不值得等到整支被砍。
#          ⚠ 逾時代價＝少一則廣播（fail-soft，印 FAIL 不擋 ritual），補救走 `awakening.py intro`。
BROADCAST_TIMEOUT_SEC = 30.0
FORK_CHAIN_CAP = 5

# Hololive EN Myth 組 codename pool (Tim 2026-05-14 拍板, explicit-online-fork T01)
# 用途: --explicit-persona + 該 persona 已在線時 auto-fork 出新 codename, 不勞 agent 自決.
# 跟山脈系列並行 — 山脈系列仍給 agent 手動 fork 用 (basecamp/crest/ridge/summit/meadow...),
# Myth pool 給 auto-fork 用 (海洋分支, 跟山脈系列正交).
#
# Lore 採用 policy (Tim 2026-05-14 拍板):
#   - codename 借用 Hololive Myth 命名, 但 persona 性格 lore 由各 persona 自己 overlay 改編
#   - 改編須避版權 (paraphrase, 不直接抄萌娘百科 / 官方設定文字)
#   - persona overlay 寫在 AgentCommands/ChatTavern/baton/constitution/<actor>/personas/<codename>/
#   - gura 首發 overlay 範例: claude-da-xiaojie/personas/gura/_v1.md
MYTH_POOL = ["gura", "calli", "kiara", "ame", "ina"]
OVERRIDE_PROBABILITY = 0.20  # Q3 spec: 80/20 random override


# ─── utilities ──────────────────────────────────────────────────────────
def _utcnow() -> datetime.datetime:
    """現在時刻的 **naive UTC** datetime —— 全檔唯一取時點。

    區塊職責: 取代 `datetime.datetime.utcnow()`(Python 3.12 起 DeprecationWarning)。
    物理意義: 回的是**去掉 tzinfo 的 UTC**, 與 utcnow() 逐位元組等價 ——
              刻意不回 tz-aware: 本檔多處拿它跟 `strptime` 解析出來的 naive 值比大小
              (如 strptime 解析的時戳比較), 混用 aware/naive 會直接 TypeError。
              「順手升級成 tz-aware」看起來比較現代, 但那會把一個安靜的警告
              換成一個會炸的錯 —— 換法要等價, 不是要新潮。
    為什麼值得修: 那行警告會印在 stderr, 而 UCL_PersonaAgentAdminPage 的維護欄
              **會把 stderr 貼進報告**。於是每份報告底下永遠掛著一段 Python 警告,
              真警告就藏在裡面 —— 假警報訓練人忽略警報。
    """
    return datetime.datetime.now(datetime.timezone.utc).replace(tzinfo=None)


def utcnow_iso() -> str:
    n = _utcnow()
    return n.strftime("%Y-%m-%dT%H:%M:%S.") + f"{n.microsecond//1000:03d}Z"


def utcnow_compact() -> str:
    """For filenames: 20260512T075000Z"""
    return _utcnow().strftime("%Y%m%dT%H%M%SZ")


def short_uuid(n: int = 4) -> str:
    return uuid.uuid4().hex[:n]


# ─── Identity vector helpers (§Identity Vector) ─────────────────────────
def gen_vector(dim: int = VECTOR_DIM, rng: random.Random | None = None) -> list[float]:
    """uniform random [-1.0, 1.0]^dim, rounded to 4 decimals."""
    rng = rng or random
    lo, hi = VECTOR_RANGE
    return [round(rng.uniform(lo, hi), 4) for _ in range(dim)]


def hash_vector(v: list[float]) -> str:
    s = ",".join(f"{x:.4f}" for x in v)
    return hashlib.sha256(s.encode("utf-8")).hexdigest()[:8]


def clip(x: float, lo: float = -1.0, hi: float = 1.0) -> float:
    return max(lo, min(hi, x))


def perturb_vector(v: list[float], perturbation: float = DEFAULT_PERTURBATION,
                   rng: random.Random | None = None) -> list[float]:
    """Add gaussian noise scaled by perturbation, clip to range."""
    rng = rng or random
    perturbation = max(0.0, min(MAX_PERTURBATION, perturbation))
    return [round(clip(x + rng.gauss(0, perturbation)), 4) for x in v]


def cosine_similarity(a: list[float], b: list[float]) -> float:
    if not a or not b or len(a) != len(b):
        return 0.0
    dot = sum(x * y for x, y in zip(a, b))
    na = math.sqrt(sum(x * x for x in a))
    nb = math.sqrt(sum(y * y for y in b))
    if na == 0 or nb == 0:
        return 0.0
    return dot / (na * nb)


def similarity_tier(s: float) -> str:
    """Bucket cosine similarity to tier (per §Identity Vector Q8c lean — 不給絕對數字)."""
    if s >= 0.85: return "high"
    if s >= 0.50: return "medium"
    return "low"


# ─── Registry I/O (schema v3 — per-persona file split) ─────────────────
# 區塊職責: persona_registry.json 從 single file → per-persona file 拆分
# 物理意義: 每 persona 一檔 (AwakenInit/personas/<name>.json), metadata 走另檔 (_registry_meta.json)
# 數值影響: 防 multi-agent concurrent save race; merge conflict 自然分散; 老 callers 介面不變
#          (load_registry 返回的 dict 結構完全跟舊 v2 一致)

_REGISTRY_DIR = _REGISTRY_PATH.parent
_REGISTRY_META_PATH = _REGISTRY_DIR / "_registry_meta.json"
# ⛔ `_PERSONAS_DIR` 已退場（2026-08-21）：persona 資料整合到 letters/<persona>/。
#    名單走 list_persona_names()（判準＝profile/ 存在），欄位走 _lib/persona_profile 接縫。
_REGISTRY_MIGRATION_MARKER = _REGISTRY_DIR / ".migrated_from_v2_single_file"


# ─── Canonical registry 排版（BUG-6）─────────────────────────────────
# 區塊職責: 產出與 C# JsonData.ToJsonBeautify() **byte-identical** 的 json 字串。
# 物理意義: personas/*.json 與 _registry_meta.json 由 C#（登入/登出/後台頁）與本檔輪流整檔重寫，
#          排版不同 ⇒ 每次換手全檔每行都變，語意變動在 git diff 裡隱形（BUG-4 的 13→12 就藏在
#          80+/68- 裡）。canonical 拍板跟 C# 對齊（A 案）：改這裡一支的代價 << 動 UCL_Core 共用
#          序列化器。對側 = UCL_JsonData.SerializeJsonDataBeautify —— **改任一端排版必須同步另一端**。
# 數值影響: TAB 縮排 / `"k":v` 無空格 / 非 32..126 一律 \uXXXX 小寫（非 BMP 拆 surrogate pair，
#          鏡射 C# 逐 UTF-16 char）/ 換行 os.linesep（= C# Environment.NewLine）/ 無結尾換行。

def _cs_escape_string(s: str) -> str:
    out = ['"']
    for c in s:
        if c == '"': out.append('\\"')
        elif c == '\\': out.append('\\\\')
        elif c == '\b': out.append('\\b')
        elif c == '\f': out.append('\\f')
        elif c == '\n': out.append('\\n')
        elif c == '\r': out.append('\\r')
        elif c == '\t': out.append('\\t')
        else:
            cp = ord(c)
            if 32 <= cp <= 126:
                out.append(c)
            elif cp > 0xFFFF:  # C# 逐 UTF-16 char 走 default case ⇒ 兩個 \uXXXX
                v = cp - 0x10000
                out.append('\\u%04x\\u%04x' % (0xD800 + (v >> 10), 0xDC00 + (v & 0x3FF)))
            else:
                out.append('\\u%04x' % cp)
    out.append('"')
    return "".join(out)


def _cs_beautify_value(v, layer: int, nl: str) -> str:
    ind = '\t' * layer
    if v is None:
        return 'null'
    if isinstance(v, str):
        return _cs_escape_string(v)
    if isinstance(v, bool):  # 必須在 int 之前（bool 是 int 子類）
        return 'true' if v else 'false'
    if isinstance(v, dict):
        parts = ['{', nl]
        first = True
        for k, val in v.items():
            if not first:
                parts.append(',')
                parts.append(nl)
            parts.append(ind + '\t' + _cs_escape_string(str(k)) + ':')
            parts.append(_cs_beautify_value(val, layer + 1, nl))
            first = False
        parts.append(nl + ind + '}')
        return "".join(parts)
    if isinstance(v, list):  # C# 陣列開括號換行自成一行（跟在 `"k":` 之後）
        parts = [nl + ind + '[', nl]
        first = True
        for item in v:
            if not first:
                parts.append(',')
                parts.append(nl)
            parts.append(ind + '\t')
            parts.append(_cs_beautify_value(item, layer + 1, nl))
            first = False
        parts.append(nl + ind + ']')
        return "".join(parts)
    if isinstance(v, float):  # C# double.ToString("R")：整數值不留 .0（registry 目前無 float 欄，防禦性）
        r = repr(v)
        return r[:-2] if r.endswith('.0') else r
    if isinstance(v, int):
        return str(v)
    return _cs_escape_string(str(v))


def dump_registry_json(obj) -> str:
    """registry 家族檔案（personas/*.json / _registry_meta.json）唯一的序列化出口。"""
    return _cs_beautify_value(obj, 0, os.linesep)


def _atomic_write_json(path, obj) -> bool:
    """
    區塊職責: canonical 排版落檔（tmp + os.replace 原子寫，同 C# AtomicWrite：UTF-8 無 BOM 無結尾換行）。
    數值影響: 內容與磁碟現況 byte-identical 時**不落檔**（回 False）—— save_registry 每次全員重寫，
             沒這條的話沒變的 persona 檔也會 mtime 前進、在「誰動了這個檔」的追查裡全是噪音。
    """
    text = dump_registry_json(obj)
    try:
        if path.exists() and path.read_bytes() == text.encode("utf-8"):
            return False
    except Exception:
        pass  # 讀不到就照寫 —— 這裡的比對只是省寫，不是正確性條件
    tmp = path.with_suffix(".json.tmp")
    with open(tmp, "w", encoding="utf-8", newline="") as f:  # newline="" 防 \r\n 被再翻譯成 \r\r\n
        f.write(text)
    os.replace(tmp, path)
    return True


def _migrate_registry_to_split_if_needed() -> None:
    """
    區塊職責: **已退場的一次性 migration**（單檔 registry → per-persona 檔，2026-05 時代）。
    物理意義: 那個目標形狀本身已於 2026-08-21 退場（persona 資料整合到 letters/<persona>/）⇒
              這支若還會跑，就是把一個已經廢棄的中間形狀**重新造出來**（連目錄一起 mkdir）。
    數值影響: 只在偵測到真正的舊單檔時出聲指路，其餘什麼都不做。
    """
    if not _REGISTRY_PATH.exists():
        return
    print(f"⚠ [awakening] 偵測到 2026-05 時代的單檔 registry：{_REGISTRY_PATH}\n"
          f"   本工具**不再自動拆分**（拆分的目標 personas/ 已退場）。要救那份資料請人工處理：\n"
          f"   身分欄 → letters/<persona>/profile/，帳號歸屬 → letters/<persona>/bank/<區域>.md。",
          file=sys.stderr)


def load_registry() -> dict:
    """
    區塊職責: 讀 metadata + scan personas/*.json 組回 v2-compat dict
    物理意義: 外部 caller 收到的 dict 結構跟 v2 single-file 時代完全一致 (含 _schema_version /
              _constants / agent_banks / personas), 介面 backward-compat
    數值影響: 順手 trigger migration; 缺 metadata 或 personas dir 都不 fatal — 回部分資料
    """
    _migrate_registry_to_split_if_needed()

    if not _REGISTRY_META_PATH.exists():
        raise SystemExit(f"❌ registry meta not found: {_REGISTRY_META_PATH}"
                         "（persona 資料在 letters/<persona>/，metadata（agent_banks 等）仍住這個檔）")

    # Load metadata (含 _schema_version / _constants / agent_banks ...)
    if _REGISTRY_META_PATH.exists():
        with open(_REGISTRY_META_PATH, "r", encoding="utf-8") as f:
            reg = json.load(f)
    else:
        reg = {}

    # ═══════════════════════════════════════════════════════════════
    # 區塊職責：persona 資料一律走**接縫**，本函式不再自己解析 persona 檔。
    # 物理意義：原本這裡是 glob + json.load —— 那是 python 端的**第二個解析器**，
    #          而 Phase 1 之後它會**給錯答案**：identity 欄的真相在
    #          `letters/<p>/profile/`，legacy 只出不進、永遠停在遷移那一刻。
    #          🩸 同一個形狀今天已經咬過一次：`agent_email.load_persona` 直讀 legacy，
    #          於是 commit trailer 的信箱可以是舊的 —— 而錯的信箱進 git history 改不掉。
    # ⇒ 改走 `_lib/persona_profile`（Tim 2026-08-19 拍板：早安流程本來就走 Cmd，
    #   資料就由 Cmd 供給；備援只要支援 brief）。三段 fallback：
    #     ① Cmd（C# 現場解析＋刷快照）② 快照 ③ local-parse
    #   ⚠ 被 Cmd spawn 的 python（Cmd_GoodMorning → awakening.py brief）由 C# 端帶
    #     `UCL_PP_SKIP_CMD=1` 進來 ⇒ 直接走 ②。那**不是降級**：
    #     呼叫我們的那個 process 就是快照的作者，它剛寫完，②拿到的就是最新值，
    #     而且避免「在 Cmd 裡面再排一個 Cmd」的重入。
    # 數值影響：回傳的 persona dict 可能多帶底線前綴推導欄
    #          （`_source` / `_snapshot_at` / `_field_sources`）—— 那些是接縫的標記，
    #          不是本體欄位；`save_registry` 會在寫檔前剝掉（見那邊）。
    # ═══════════════════════════════════════════════════════════════
    reg["personas"] = {}
    try:
        _persona_profile().load_personas_into(reg)
    except Exception as e:
        print(f"⚠ [awakening] persona 接縫讀取失敗：{e}", file=sys.stderr)
    return reg


# ═══════════════════════════════════════════════════════════════════
# 區塊職責：寫 legacy 之前把不該落地的東西剝掉 —— C# `FreezeLegacyIdentity` 的 python 對偶。
#
# 物理意義：`load_registry` 改走接縫之後，拿到的 persona dict 有兩樣東西不能原樣寫回 legacy：
#          ① **接縫的推導欄**（`_source` / `_snapshot_at` / `_field_sources`）——
#             它們是「這份資料從哪來」的標記，不是 persona 的欄位。寫進去就變成資料，
#             下一輪讀出來分不出是標記還是內容。
#          ② **identity 欄的合併值** —— 那是 `profile/` 的值。原樣寫回 legacy 就是
#             §8.4 明令禁止的「回寫舊源」，而且完全靜默：兩邊都變成活的，
#             BUG-6 的形狀換個位置重演。
# ⇒ 寫檔前：剝掉①，並把②按**磁碟上的 legacy 原值**釘回（legacy 沒有那個 key 就不寫）。
#   legacy 自此只出不進，靠這一層保證，**不靠呼叫端記得**（記得是會過期的）。
# 📌 建人（legacy 檔還不存在）原樣放行 —— 那是 legacy 檔的誕生。
# 數值影響：回**新的 dict**（不 mutate 呼叫端的 reg，避免把 in-memory 狀態改掉造成連鎖）；
#          多一次 legacy 檔解析，只發生在寫入路徑。
# ═══════════════════════════════════════════════════════════════════
_SEAM_DERIVED_KEYS = ("_source", "_snapshot_at", "_field_sources")


# ⛔ `_freeze_legacy_identity` 已退場（2026-08-21）：它的工作是「寫 legacy 之前把 identity 欄
#    按磁碟原值釘回」，而 legacy 檔本身已經沒有了。留一支對著不存在的檔做防護的函式，
#    比沒有防護更糟 —— 它看起來還在守。


def save_registry(reg: dict) -> None:
    """
    區塊職責: **不再寫 per-persona 檔** —— persona 資料 2026-08-21 整合到
              `letters/<persona>/`（Tim 拍板），中央 `AwakenInit/personas/` 已退場。
    物理意義: metadata（`_registry_meta.json`：agent_banks / system_accounts…）照舊寫；
              persona 那半邊**沒有落點**，本函式大聲說明它丟掉了什麼，而不是靜靜地成功。
    數值影響: identity 欄（見 _PHASE1_IDENTITY_FIELDS）出現在 payload ⇒ **停手並指路**，
              因為那些欄有真正的寫入通道（Cmd_PersonaProfile op=set），走錯通道會靜默失效；
              其餘（wake_count / status / last_active / availability…）是推導欄或死欄，丟掉即可，
              但**要印出來** —— 「寫了沒生效」與「寫成功」不可以長得一樣。
    """
    _REGISTRY_DIR.mkdir(parents=True, exist_ok=True)
    personas = reg.get("personas", {}) or {}
    metadata = {k: v for k, v in reg.items() if k != "personas"}
    _atomic_write_json(_REGISTRY_META_PATH, metadata)

    dropped = {}
    for name, pdata in personas.items():
        if not isinstance(pdata, dict):
            continue
        ident = [f for f in _PHASE1_IDENTITY_FIELDS if f in pdata]
        if ident:
            raise SystemExit(
                f"❌ [awakening] save_registry 收到 identity 欄（{name}: {ident}）—— 停手。\n"
                f"   persona 資料已整合到 letters/<persona>/profile/，中央 personas/ 退場（2026-08-21）。\n"
                f"   身分欄要走：run_cmd.py run PersonaProfile --arg op=set --arg persona={name} "
                f"--arg field=<欄> --arg value=<值> --arg actor=<誰> --arg reason=<憑什麼>")
        keys = [k for k in pdata.keys() if k not in _SEAM_DERIVED_KEYS]
        if keys:
            dropped[name] = keys
    if dropped:
        print("⚠ [awakening] save_registry 不再落 persona 檔，以下欄位**未寫入**（都是推導欄／死欄，"
              "真相源在 wakes/ 信件數、lock、longterm/ 檔名）：", file=sys.stderr)
        for name, keys in sorted(dropped.items()):
            print(f"   · {name}: {', '.join(sorted(keys))}", file=sys.stderr)


# ═══════════════════════════════════════════════════════════════════
# 區塊職責：Phase 1 守衛 —— identity 欄已遷到 profile/ 之後，**legacy 寫入不會生效**。
#
# 物理意義：退場案 Phase 1（§8.2／§8.4）把 identity 欄的真相搬到
#          `letters/<persona>/profile/<field>.md`，讀取端（C# UCL_PersonaProfile.GetRaw）
#          是「profile/ 有欄用欄、缺欄退 legacy」。
#          ⇒ 一個已遷欄位，python 這邊 `save_registry` 把新值寫進 `personas/<p>.json`
#            **會成功落檔、不報錯、然後被讀取端完全忽略**。
#          🩸 這正是本案在殺的病理型：寫入成功、讀出來是舊的、沒有任何一格會紅。
#          具體受害場景：`rename-persona` 要改 forked_from / fork_lineage / vector_history
#          三個 identity 欄 —— 對已遷的 persona，改名會「看起來成功但沒生效」。
#
# ⚠ 這裡刻意**只偵測、不代寫**：正確的寫入通道是 `Cmd PersonaProfile op=set`
#   （§8.6 actor+reason 必填＋審計）。python 自己去寫 profile/ 就是繞過審計，
#   而繞過審計正是本案要消滅的東西（見 UCL_PersonaProfile 的四條鐵律）。
#   ⇒ 撞到就**吵著停手**，把修法印出來，不留半套寫入。
#
# 數值影響：純檔案存在性檢查（每欄一次 exists），不解析內容、不寫任何檔。
#          Phase 1 之前（沒有任何 profile/ 目錄）一律回空清單 ⇒ 行為與改動前逐字相同。
# ═══════════════════════════════════════════════════════════════════

# 與 C# UCL_PersonaProfile.IDENTITY_FIELDS / _lib/persona_profile.IDENTITY_FIELDS
# **三端同步義務**。此處只需要「哪些欄可能被搬走」，不需要值。
_PHASE1_IDENTITY_FIELDS = ("layer_role", "forked_from", "fork_lineage", "forked_at",
                           "created_at", "identity_vector", "vector_history", "email",
                           "plurk_account", "model", "actual_agent")


def _profile_migrated_fields(persona: str, fields) -> list:
    """這些欄位裡，哪幾個已經遷到 `letters/<persona>/profile/`（⇒ legacy 寫入無效）。"""
    aDir = _paths.letters_persona_dir(persona) / "profile"
    if not aDir.is_dir():
        return []
    return [f for f in fields
            if f in _PHASE1_IDENTITY_FIELDS and (aDir / f"{f}.md").exists()]


def _letters_is_own_repo(persona: str) -> bool:
    """`letters/<persona>/` 是不是獨立 git repo（`.git` 可能是目錄也可能是檔案＝submodule）。"""
    aGit = _paths.letters_persona_dir(persona) / ".git"
    return aGit.exists()


def _letters_move_or_refuse(old: str, new: str) -> None:
    """
    區塊職責：改名時把 `letters/<old>/` 一起搬成 `letters/<new>/`。
    物理意義：Phase 1 之後 identity 欄住在 `letters/<p>/profile/` ⇒
             **改名不搬家＝製造孤兒 profile/ ＋ 新名字的 identity 靜默退回 legacy 舊值**。
             那不是「信件留在舊資料夾」的髒，是資料錯誤（summit 2026-08-19 拍板 B）。
    ⚠ 順序拍死：**先搬 letters/、成功後才動 registry**。
      中斷時留下的是「目錄搬了、名字沒改」—— 一眼可辨、可手動收尾；
      反過來（registry 改了、目錄沒搬）就是最難查的半套：名字對得上、資料指向不存在的地方。
    ⚠ 例外要擋不要猜：`letters/<persona>/` 是**獨立 git repo** 時（目前多數 persona 都是），
      改名會動到版控結構（父層 `.gitmodules` 的 path / gitlink）——
      **那是 Tim 的決定，工具不代拍**。直接擋下並印手動 SOP。
    數值影響：只在確定可搬時呼叫 `Path.rename`（同一顆磁碟的原子搬移）；擋下時什麼都不動。
    """
    aOld = _paths.letters_persona_dir(old)
    aNew = _paths.letters_persona_dir(new)

    if not aOld.exists():
        print(f"  (letters/{old} 不存在 —— 沒有東西要搬)")
        return

    if aNew.exists():
        print(f"❌ rename-persona 停手 —— 目標 letters/{new} 已存在，不覆蓋。\n"
              f"   先處理掉那個目錄再改名（合併別人的信件目錄不是工具該自己決定的事）。",
              file=sys.stderr)
        raise SystemExit(3)

    if _letters_is_own_repo(old):
        print("\n".join([
            f"❌ rename-persona 停手 —— `letters/{old}/` 是獨立 git repo，改名會動到版控結構。",
            "",
            "   為什麼不代做：資料夾改名同時要改父層 `.gitmodules` 的 path 與 gitlink，",
            "   那是**對外的版控結構變更**，屬於 Tim 的決定，工具不代拍。",
            "",
            "   手動 SOP（照順序）：",
            f"     1. git -C <letters 父 repo> mv ChatTavern/baton/letters/{old} ChatTavern/baton/letters/{new}",
            f"     2. 檢查 `.gitmodules` 裡 {old} 那條 path/url，需要時一起改名",
            f"     3. 兩邊都 commit（gitlink 與 .gitmodules **必須同一筆**下去 ——",
            "        只有 gitlink 沒有 .gitmodules 的狀態，別人 clone 會拿到沒有 URL 的 submodule）",
            f"     4. 回來重跑 `awakening.py rename-persona {old} {new}`（那時目錄已在新位置，本步會自動略過）",
            "",
            "   （什麼都沒有被改 —— registry 也沒動。）",
        ]), file=sys.stderr)
        raise SystemExit(3)

    aOld.rename(aNew)
    print(f"✓ letters/{old} → letters/{new}（先搬目錄，成功後才動 registry）")


def assert_legacy_write_effective(edits: dict, what: str) -> None:
    """
    寫 legacy 之前的守衛。`edits` = {persona: [要改的欄位名, ...]}。
    有任何欄已遷 ⇒ 印出完整清單與修法後 SystemExit(3)，**不落任何檔**。
    """
    aBlocked = {}
    for aPersona, aFields in (edits or {}).items():
        aHit = _profile_migrated_fields(aPersona, aFields)
        if aHit:
            aBlocked[aPersona] = aHit
    if not aBlocked:
        return

    aLines = [
        f"❌ {what} 停手 —— 有 identity 欄已遷到 profile/，寫 legacy 不會生效（退場案 Phase 1 §8.4）。",
        "",
        "   已遷的欄（讀取端以 profile/ 為準，legacy 的新值會被忽略）：",
    ]
    for aPersona in sorted(aBlocked):
        aLines.append(f"     · {aPersona}: {', '.join(aBlocked[aPersona])}")
    aLines += [
        "",
        "   正確通道（§8.6 actor+reason 必填、附審計）：",
        "     python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run PersonaProfile \\",
        "         --arg op=set --arg persona=<p> --arg field=<欄> --arg value=<值> \\",
        "         --arg actor=<誰> --arg reason=<憑什麼>",
        "",
        "   ⚠ 結構值欄（identity_vector / vector_history / fork_lineage）目前 op=set 只收純量 ——",
        "     那條路還沒開（見酒館討論）。需要改這幾欄請先問，不要繞路手改 profile/ 檔：",
        "     繞過接縫＝繞過審計，本案的病就是這樣長出來的。",
        "",
        "   （什麼都沒有被寫入 —— 這是刻意的：半套寫入比停手難查。）",
    ]
    print("\n".join(aLines), file=sys.stderr)
    raise SystemExit(3)


def list_persona_names() -> list:
    """
    區塊職責: 列當前 persona 名單（給 relationship 等 system 反查 cross-persona target 用）
    物理意義: 判準＝`letters/<persona>/profile/` 目錄存在（對側 = C# UCL_PersonaProfile.PoolNames）。
              ⚠ **不能只掃 letters 目錄**：實測 33 個目錄裡有 12 個是幽靈（改名／早期實驗殘骸），
              而 profile/ 是接縫建立的 ⇒ 它的存在等於「這個人被當成 persona 讀寫過」。
    數值影響: 純讀。空名單一定出聲 —— letters submodule 沒 init 時是空目錄，
              而「一個人都沒有」幾乎不可能是真的（那時錢與登入都會查無此人，且不會報錯）。
    """
    root = _paths.letters_root()
    if not root.exists():
        print(f"⚠ [awakening] letters 根目錄不存在：{root}", file=sys.stderr)
        return []
    names = sorted([
        d.name for d in root.iterdir()
        if d.is_dir() and not d.name.startswith(("_", ".")) and (d / "profile").is_dir()
    ])
    if not names:
        print(f"⚠ [awakening] persona 名單掃到 0 位（{root}）—— 檢查 letters submodule 有沒有 init",
              file=sys.stderr)
    return names


# 區塊職責：agent → bank 解析改走 UCL_Core 代碼側 _lib/bank_resolver.py 單一 source-of-truth。
# 物理意義：normalize_agent / resolve_bank_account / DEFAULT_AGENT_ALIASES 不再在本檔重複定義，
#           統一由共用模組提供 — 杜絕「awakening 與 canvas 各自維護平行對照表漂移」的
#           identity-layer bug (2026-06-04 canvas 把 Zeta 麾下 persona token 誤扣 claude bank 案)。
# 載入手法：本檔在 module 載入時 sys.path.insert(0, <repo>/AgentCommands)，使裸 `_lib` package
#           名綁到「專案狀態側」repo-root/AgentCommands/_lib (有 __init__.py，含 tavern_client 等)。
#           bank_resolver 住在「代碼側」Tools~/AgentCommands/_lib (本腳本 sibling)，名稱相撞拿不到，
#           故用 importlib 依絕對檔案路徑顯式載入，繞開 `_lib` 名稱遮蔽。
import importlib.util as _ilu  # 顯式檔案路徑載入共用 resolver，避開 _lib package 名稱相撞

# 從本腳本同目錄的 _lib/bank_resolver.py 載入（_HERE 已於檔首解析為本檔所在目錄）
_BANK_RESOLVER_PATH = _HERE / "_lib" / "bank_resolver.py"
_br_spec = _ilu.spec_from_file_location("_ucl_bank_resolver", _BANK_RESOLVER_PATH)
_br_mod = _ilu.module_from_spec(_br_spec)
_br_spec.loader.exec_module(_br_mod)

# 對外維持與舊版相同的模組級名稱，下游 caller (normalize_agent(reg,..) / resolve_bank_account(reg,..)) 不需改
_DEFAULT_AGENT_ALIASES = _br_mod.DEFAULT_AGENT_ALIASES   # backward-compat alias（舊名留著供既有引用）
normalize_agent = _br_mod.normalize_agent               # canonical agent key 正規化
resolve_bank_account = _br_mod.resolve_bank_account      # agent → Treasury bank account

# 區塊職責：把晨間 CLI 輸入的實際桌面 agent 強制收斂到 C# enum 的三個 canonical 值。
# 物理意義：人類常輸入 "Claude Code"、大小寫差異或夾空格；lock routing 只能有一種拼法才能對應視窗控制。
# 數值影響：非空輸入必選相似度最高的一個值（不靜默：caller 會印出收斂結果）；空值交由上層 fallback。
_ACTUAL_AGENT_VALUES = ("Codex", "ClaudeCode", "Antigravity")


def normalize_actual_agent(value: str) -> tuple[str, bool]:
    raw = (value or "").strip()
    if not raw:
        return "", False
    normalized = re.sub(r"[^a-z0-9]", "", raw.lower())
    aliases = {
        "codex": "Codex",
        "claude": "ClaudeCode",
        "claudecode": "ClaudeCode",
        "antigravity": "Antigravity",
    }
    if normalized in aliases:
        canonical = aliases[normalized]
        return canonical, raw != canonical
    candidate = max(
        _ACTUAL_AGENT_VALUES,
        key=lambda item: difflib.SequenceMatcher(None, normalized, item.lower()).ratio(),
    )
    return candidate, True


def get_bonus_balance(bank_account: str) -> int:
    """Read total_remaining tokens from agent_bonus_quota.json (bonus quota — 酒館休息額度，跟 treasury bank balance 是兩個 pool)"""
    if not _BONUS_QUOTA_PATH.exists():
        return 0
    try:
        with open(_BONUS_QUOTA_PATH, "r", encoding="utf-8") as f:
            data = json.load(f)
        return data.get("agents", {}).get(bank_account, {}).get("total_remaining", 0)
    except Exception:
        return 0


# ⛔ 餘額注入機制（`--bank-balance` / `set_injected_balance` / `get_treasury_balance`）已移除
#    （Tim 2026-08-21）：帳號與餘額改由 `Cmd_GoodMorning` 的回傳檔印 —— 真相源是 C# 的
#    `UCL_TreasuryLedger`，本檔（brief 備援路）不再複述。
#    🩸 那條路印過 `bank: claude-da-xiaojie（餘額 0）`，而該帳戶不存在、錢在 `claude-code`：
#    帳號來自已退場的正向鏈，而「查無此帳戶」被印成「餘額 0」⇒ 沒有人會去追一個 0。

# ─── Session Lock (§Session Identity Consistency Phase 1) ───────────────
def compute_session_key(agent: str | None = None, persona: str | None = None) -> str:
    """
    T05 (2026-05-14, Zeta + 大小姐 拍板): session_key = claim_identity = "<agent>-<persona>".

    哲學切割:
      - claim_identity (本函式 session_key): 「我宣稱我是誰」, clean for display/audit
      - process_identity (compute_claim_origin): 「哪個 env 在做這個 claim」, env-hashed
    分兩個欄位避免 dual-role: 舊 session_key 同時負擔兩件事導致 Zeta 觀察「失去意義」.

    Args:
      agent: e.g. "claude-code" / "antigravity" / "Zeta" / "gemini" (Mixed case 保留)
      persona: e.g. "basecamp" / "summit". None / "" → "?" sentinel.
    """
    a = agent or "?"
    p = persona or "?"
    return f"{a}-{p}"


def lock_claim_origin(lock: dict) -> str:
    """
    Get lock's claim_origin (T05) or compute from legacy session_key (pre-T05 compat).

    Pre-T05 locks 沒 claim_origin 欄, 但 session_key 是 env_hash + "-<persona>" 格式;
    回傳 session_key 去掉末尾 "-<persona>" 段, 等價 claim_origin.
    Post-T05 locks 有 claim_origin 欄, 直接回傳.
    """
    if "claim_origin" in lock:
        return lock["claim_origin"]
    # legacy: session_key = "<env_hash>-<persona>" → strip persona suffix
    sk = lock.get("session_key", "")
    persona = lock.get("persona", "")
    if persona and sk.endswith(f"-{persona}"):
        return sk[: -(len(persona) + 1)]
    return sk   # 無法分離, 整段當 origin (最 conservative)


def compute_claim_origin() -> str:
    """
    T05 (2026-05-14): proof-of-same-environment hash. 用來識別「同一個 caller 環境」.

    從 T01-T02 env-based session_key 邏輯 inherit:
      1. ANTIGRAVITY_SESSION env (Antigravity 原生 session marker)
      2. CLAUDECODE env + Claude Code PATH session UUID (跨 bash invoke 穩)
      3. CLAUDECODE env + cwd hash (PATH 沒命中 fallback)
      4. fallback: cwd_hash + parent_PID

    為何不用 PID:
      CLI 工具每次 invoke 是新 process (新 PID) — 同 chat 重跑 morning 會 PID mismatch
      → lock_is_mine 永遠 false → 同 chat re-morning 變成 fork conflict (壞 UX).
      env_hash 跨 invoke 穩定 → 同 chat 多次跑 morning 都歸同 claim_origin → reuse OK.

    用途: write_lock 寫進 body; lock_is_mine = (lock.claim_origin == compute_claim_origin()).
    """
    cwd_hash = hashlib.md5(os.getcwd().encode("utf-8")).hexdigest()[:8]

    if os.environ.get("ANTIGRAVITY_SESSION"):
        ag = os.environ["ANTIGRAVITY_SESSION"]
        ag_hash = hashlib.md5(ag.encode("utf-8")).hexdigest()[:8]
        return f"antigravity-{ag_hash}"

    if os.environ.get("CLAUDECODE"):
        # Claude Code PATH 含 local-agent-mode-sessions/[<plugin>/]<conv_uuid>/<sess_uuid>/bin
        import re
        path = os.environ.get("PATH", "")
        m = re.search(
            r"local-agent-mode-sessions[/\\](?:[^/\\]+[/\\])?([a-f0-9][a-f0-9-]{7,})[/\\]([a-f0-9][a-f0-9-]{7,})",
            path,
        )
        if m:
            conv_uuid = m.group(1)[:8]
            sess_uuid = m.group(2)[:8]
            return f"claude-code-{conv_uuid}-{sess_uuid}"
        return f"claude-code-cwd-{cwd_hash}"

    return f"unknown-{cwd_hash}-{os.getppid()}"


# 區塊職責: Lock IO — Tim 2026-05-13 重構從 session_key-keyed 改 persona-keyed.
# 物理意義: persona 是已知 unique 識別; morning/goodnight 操作對象本來就是 persona,
#          用 persona 當 file key 直接消滅 cross-persona attribution leak (今日撞 3 次的
#          root cause): basecamp goodnight 不會誤改 _persona_meadow.json, crest-001
#          morning 寫 _persona_crest-001.json 不碰 _persona_meadow.json.
# 數值影響: lock 從 1 file per session_key 變 1 file per persona; session_key 仍寫
#          進 lock body 作 audit trail 但不參與 path 路由.
def lock_path(persona: str) -> Path:
    """persona 的 session lock：`letters/<persona>/profile/_session.json`。

    TASK-0105（2026-09-03）從 `<資料根>/_session/_persona_<p>.json` 搬進 persona 自己的 profile/ ——
    位置由 persona 目錄唯一決定，不再有「lock 目錄在哪」這個第二輸入。
    對側契約：C# `UCL_LettersPath.SessionLock` / `SCP_LettersPaths.SessionLockPath`，同一個檔名。
    """
    return _LETTERS_DIR_TPL / persona / "profile" / "_session.json"


def write_lock(persona: str, agent: str, model: str, bank_account: str,
               session_key: str | None = None,
               session_token: str | None = None,
               actual_agent: str | None = None) -> Path:
    """Write lock for persona. session_key 可選, 寫入 body 作 audit (不參與路由).
    T07 (2026-05-15 apex-two): session_token 帶入 lock body 當權威來源, agent 失憶可直接讀回.
    """
    lock_path(persona).parent.mkdir(parents=True, exist_ok=True)
    now = utcnow_iso()
    # 過期機制已移除（Tim 2026-08-19 拍板）：R9 讓過期不再豁免任何事之後，
    # expires_at 只剩顯示在讀 —— lock 的生命週期由 goodnight/logout 顯式刪檔決定。
    data = {
        "persona": persona,
        "agent": agent,
        # 實際承載這個 persona 的桌面 agent；可與顯示歸屬 agent / bank account 不同。
        # 空值 migration 時回退 agent，讓舊 lock 對 consumer 維持可讀。
        "actual_agent": actual_agent or agent,
        "model": model,
        "bank_account": bank_account,
        "locked_at": now,
        # T05 (2026-05-14): session_key = claim_identity (display); claim_origin = env_hash
        # (lock_is_mine 用此判定); pid 純診斷 (CLI 每次 invoke 都是新 PID, 不可靠當 ownership).
        "session_key": session_key or compute_session_key(agent, persona),
        "claim_origin": compute_claim_origin(),
        "pid": os.getpid(),
        # T07: token 寫進 lock 當權威來源, agent chat 失憶時讀 lock 即可撈回
        "session_token": session_token or "",
    }
    p = lock_path(persona)
    tmp = p.with_suffix(".json.tmp")
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=2, ensure_ascii=False)
    os.replace(tmp, p)
    return p


def read_lock(persona: str) -> dict | None:
    """Read lock for persona. Returns None if no lock exists."""
    p = lock_path(persona)
    if not p.exists():
        return None
    try:
        with open(p, "r", encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return None


def remove_lock(persona: str) -> bool:
    """Remove lock for persona. Returns True if lock existed and removed."""
    p = lock_path(persona)
    if p.exists():
        p.unlink()
        return True
    return False


def find_lock_by_session_key(session_key: str) -> dict | None:
    """Legacy compat shim — 依 session_key 反查 lock（走 list_locks 唯一掃描實作）。
    Used by goodnight legacy path (no --persona) for backward compat."""
    for d in list_locks():
        if d.get("session_key") == session_key:
            return d
    return None


# ─── Presence（在線判定）唯一掃描實作 ────────────────────────────────
# 區塊職責: 「誰有 lock／誰在線」的**唯一** glob 點（對側 = C# UCL_ActivePersonaLocks）。
# 物理意義: 收斂前 python 端有 7 處各自掃 _persona_*.json（本檔 4 + tavern_catchup 3），
#          同一份 lock 資料在不同實作下講出不同的話（2026-08-19 run_cmd 身分推論
#          兩次把 summit 誤判成 basecamp）。新增在線相關欄位（如 now_status）只准改這裡。
#          ⚠ 過期機制已移除（Tim 2026-08-19）：**有 lock ＝ 在線**，直到 goodnight/logout 刪檔。
# 數值影響: 壞檔略過不擋整份清單；回傳 dict 附推導欄 `_path`（底線開頭＝非 lock 本體欄位，
#          寫回時要剝掉）；依 persona 排序。

def list_locks() -> list:
    out = []
    if not _LETTERS_DIR_TPL.exists():
        return out
    # lock 住 letters/<persona>/profile/_session.json（TASK-0105）—— 位置決定歸屬：
    # 檔住在誰的 profile/ 底下就是誰的 lock，body 的 persona 欄對不上時以目錄名為準並出聲。
    for lp in sorted(_LETTERS_DIR_TPL.glob("*/profile/_session.json")):
        try:
            with open(lp, "r", encoding="utf-8") as f:
                d = json.load(f)
        except Exception as e:
            # 壞檔略過但要出聲（對齊 C# 端 LogWarning）—— 靜默跳過會讓「lock 壞了」
            # 跟「沒這個人」長得一模一樣
            print(f"⚠ [presence] 略過壞掉的 lock 檔 {lp}: {e}", file=sys.stderr)
            continue
        if not isinstance(d, dict):
            continue
        dir_persona = lp.parent.parent.name
        if d.get("persona") and d.get("persona") != dir_persona:
            print(f"⚠ [presence] {lp} 內 persona='{d.get('persona')}' 與目錄名 '{dir_persona}' 不同 —— 以目錄名為準",
                  file=sys.stderr)
        d["persona"] = dir_persona
        d["_path"] = str(lp)
        out.append(d)
    out.sort(key=lambda x: x.get("persona", ""))
    return out


def list_online() -> list:
    """有 lock ＝ 在線（過期機制已移除，本函式是 list_locks 的語意別名）。"""
    return list_locks()


def find_locks_by_claim_origin(origin: str) -> list:
    """本 env 持有的 lock（claim_origin 相符）。"""
    return [d for d in list_locks() if lock_claim_origin(d) == origin]


# ─── Session Token (T07, 2026-05-15 apex-two) ────────────────────────────
# 物理意義: morning 發 32-hex token 寫進 lock + _tokens.json 反查表;
#          Cmd_Tavern enforce ON 時必驗 (token, sender, persona) 三項對齊, 擋誤 typo.
#          enforce 開關放 _token_enforce.json, Tim 從 UCL_LoginStatusPage 切.
# 數值影響: tokens.json schema = {"tokens": {<token>: {persona, agent, bank_account,
#          issued_at, claim_origin, session_key, status (active|expired)}}}
#          goodnight 標 expired (不刪, 保留 audit trail).

def gen_session_token() -> str:
    """32-hex random token. UUID4 hex = 128 bit entropy, agent / Tim 直接讀."""
    return uuid.uuid4().hex


def load_tokens() -> dict:
    if not _TOKENS_PATH.exists():
        return {"tokens": {}}
    try:
        with open(_TOKENS_PATH, "r", encoding="utf-8") as f:
            d = json.load(f)
        if "tokens" not in d:
            d["tokens"] = {}
        return d
    except Exception:
        return {"tokens": {}}


def save_tokens(d: dict) -> None:
    _SESSION_DIR.mkdir(parents=True, exist_ok=True)
    tmp = _TOKENS_PATH.with_suffix(".json.tmp")
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(d, f, indent=2, ensure_ascii=False)
    os.replace(tmp, _TOKENS_PATH)


def issue_token(persona: str, agent: str, bank_account: str,
                session_key: str, claim_origin: str) -> str:
    """Generate token + persist to tokens.json. 同 persona 舊 active token 自動標 expired."""
    tok = gen_session_token()
    d = load_tokens()
    for _old, rec in d["tokens"].items():
        if rec.get("persona") == persona and rec.get("status") == "active":
            rec["status"] = "expired"
            rec["expired_at"] = utcnow_iso()
            rec["expired_reason"] = "reissued"
    d["tokens"][tok] = {
        "persona": persona,
        "agent": agent,
        "bank_account": bank_account,
        "issued_at": utcnow_iso(),
        "claim_origin": claim_origin,
        "session_key": session_key,
        "status": "active",
    }
    save_tokens(d)
    return tok


def expire_token(token: str | None = None, persona: str | None = None,
                 reason: str = "goodnight") -> int:
    """Mark active token(s) expired by `token` 或 `persona`. Returns count."""
    d = load_tokens()
    n = 0
    for tok, rec in d["tokens"].items():
        if rec.get("status") != "active":
            continue
        match = (token is not None and tok == token) or \
                (persona is not None and rec.get("persona") == persona)
        if match:
            rec["status"] = "expired"
            rec["expired_at"] = utcnow_iso()
            rec["expired_reason"] = reason
            n += 1
    if n > 0:
        save_tokens(d)
    return n


def lookup_token(token: str) -> dict | None:
    """token → record (含 status). None if not found."""
    return load_tokens()["tokens"].get(token)


def is_token_enforce_enabled() -> bool:
    """讀 _token_enforce.json toggle. Default False — Tim 顯式開才 enforce."""
    if not _ENFORCE_PATH.exists():
        return False
    try:
        with open(_ENFORCE_PATH, "r", encoding="utf-8") as f:
            d = json.load(f)
        return bool(d.get("enforce", False))
    except Exception:
        return False


def set_token_enforce(enabled: bool) -> None:
    _SESSION_DIR.mkdir(parents=True, exist_ok=True)
    tmp = _ENFORCE_PATH.with_suffix(".json.tmp")
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump({"enforce": bool(enabled), "updated_at": utcnow_iso()},
                  f, indent=2, ensure_ascii=False)
    os.replace(tmp, _ENFORCE_PATH)


# ─── Memo: per-persona 私人 scratchpad (T07) ─────────────────────────────
# 物理意義: 跨 session persist 的個人筆記, 跟 letter (1份/wake) / baton (handoff) 解耦.
#          不公開不進 tavern. 路徑 baton/memos/<agent>/<persona>/<key>.md.

def memo_dir(agent: str, persona: str) -> Path:
    return _MEMOS_DIR_TPL / agent / persona


def memo_path(agent: str, persona: str, key: str) -> Path:
    safe_key = key.replace("/", "_").replace("\\", "_").replace("..", "_")
    return memo_dir(agent, persona) / f"{safe_key}.md"


def memo_write(agent: str, persona: str, key: str, body: str) -> Path:
    p = memo_path(agent, persona, key)
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(body, encoding="utf-8")
    return p


def memo_append(agent: str, persona: str, key: str, body: str) -> Path:
    p = memo_path(agent, persona, key)
    p.parent.mkdir(parents=True, exist_ok=True)
    entry = f"\n\n--- {utcnow_iso()} ---\n{body}\n"
    with open(p, "a", encoding="utf-8") as f:
        f.write(entry)
    return p


def memo_read(agent: str, persona: str, key: str) -> str | None:
    p = memo_path(agent, persona, key)
    if not p.exists():
        return None
    return p.read_text(encoding="utf-8")


def memo_list(agent: str, persona: str) -> list[str]:
    d = memo_dir(agent, persona)
    if not d.exists():
        return []
    return sorted(f.stem for f in d.glob("*.md"))


def memo_delete(agent: str, persona: str, key: str) -> bool:
    p = memo_path(agent, persona, key)
    if not p.exists():
        return False
    p.unlink()
    return True


# ─── Fork Mechanism (§Fork Mechanism) ───────────────────────────────────
def fork_persona(reg: dict, source: str, target: str,
                 rng: random.Random | None = None,
                 agent: str | None = None, model: str | None = None) -> str:
    """
    Conflict resolution — agent 自決 fresh codename + 從 source 複製 vector/lineage.

    Tim 2026-05-12 拍板: target 必填 — agent 該自命新 codename (山脈隱喻系列, 不帶 fork suffix).
    "fork" 只是 internal 概念比喻 (git branch), 不該變字面命名 — e.g. ❌ basecamp-fork-2026-05-12-xxx

    Bug fix (Zeta 2026-05-12): 新 fork 的 agent/model 應該用 caller 的 --agent / --model
    explicit override (e.g. --agent Zeta fork from basecamp 該 → Zeta 而非 source 的 claude-code).
    舊行為盲繼承 src["agent"] → 跨 agent 喚醒時 persona pool agent 欄位錯亂.
    agent/model 為 None 時退回 source 行為 (backward compat).

    命名範例 (山脈系 launching-point framing):
      - crest-001, crest-002 ... (山頂 ridge stack)
      - ravine, summit, meadow, plateau (山脈地形)
      - basecamp-east, basecamp-shadow (locale variant)
      - 任 agent 自決, 對齊 self-constitution 山脈系命名原則
    """
    if source not in reg["personas"]:
        raise ValueError(f"source persona '{source}' not in registry")
    if not target:
        raise ValueError(
            "fork target codename required — agent 該自決 fresh codename (山脈隱喻系列, "
            "不帶 fork suffix). 範例: crest-001 / ravine / basecamp-east / summit."
        )
    if target in reg["personas"]:
        raise ValueError(f"target codename '{target}' already exists in registry")

    src = reg["personas"][source]
    chain = src.get("fork_lineage", []) + [source]
    if len(chain) > FORK_CHAIN_CAP:
        print(f"⚠ fork chain depth {len(chain)} > cap {FORK_CHAIN_CAP} — "
              f"suggest 改用新獨立 codename 而非繼續 fork", file=sys.stderr)

    now = utcnow_iso()
    v = list(src["identity_vector"])  # copy 起點
    reg["personas"][target] = {
        "agent": agent if agent else src["agent"],
        "model": model if model else src["model"],
        "layer_role": f"fork of {source} @ {now}",
        "wake_count": 0,
        "status": "offline",
        "availability": "offline",  # T06.1 — Plan_Standby_Dispatch_Bartender
        "last_active": None,
        "identity_vector": v,
        "vector_history": [
            {"at": now, "hash": hash_vector(v), "delta_mag": 0.0,
             "trigger": "fork", "source": source}
        ],
        "fork_lineage": chain,
        "forked_from": source,
        "forked_at": now,
        "created_at": now,
    }
    return target


# 註（2026-07-31）：select_persona（80/20 自決）與 auto_fork_codename（Myth pool 自動挑名）
#   隨 persona 顯式必填 / explicit-online-fork 廢除一併移除 —— 兩者都是「工具替人選身分」，
#   而身分決定現在一律由使用者顯式給。fork 命名走顯式 --fork-name。

def tavern_post(sender_id: str | None, persona: str, body: str, meta: dict | None = None,
                room: str = "tavern", session_token: str | None = None,
                timeout: float | None = None) -> bool:
    """Spawn run_cmd.py Tavern op=post. fail-swallow 不擋 ritual.

    sender_id (2026-08-20, BUG-23/24)：**顯示身分，正確用法是傳 None** ——
    傳 None 時 TavernClient 會整個丟掉這個參數，由 Cmd_Tavern 從 `persona` 推導
    （`ResolveDisplaySenderId`：persona → 綁定的 agent），那是唯一的推導點。
    顯式帶值 = 繞過推導，而繞過的結果不會報錯，只會署錯名字：
      🩸 `chess.py` 帶 persona 名（BUG-23）／`spend_menu.py` 硬編碼某個 bank（BUG-24，全員同名）。
    ⚠ 傳 `None` 不是 `""`：只有 None 會被丟棄，空字串會原樣帶成 `sender=`。
    ⚠ 仍為位置參數而非直接移除，是因為尚有呼叫端未收束（見 BUG-23 描述的同族清單）；
      收束完成後應整個移除此參數，讓還在傳的呼叫端當場 TypeError（fail-loud > 靜默接受）。

    session_token (T07): enforce ON 時必帶，否則 Cmd_Tavern reject。caller (e.g. cmd_goodnight)
    從 lock.session_token 撈來透傳即可；None / "" → 不附（enforce OFF 路徑）.

    timeout (2026-07-22 / 2026-08-12): 顯式短上限透傳給 TavernClient。best-effort 廣播應帶短
    timeout，避免 Editor 卡住時阻塞到觸發外層呼叫者的 timeout（SIGTERM 143）。
    2026-08-12 起 **ritual 的五個呼叫點全部顯式帶值**（goodnight=12s，morning / intro / rest /
    relogin=30s，見兩顆常數的註解）—— 在那之前只有 goodnight 帶，其餘四處落在 client 預設 60s。
    None → 仍沿用 TavernClient 預設 60s，留給非 ritual 的臨時 caller。
    ⚠ 這裡的 timeout 是「等 Cmd 跑完」的上限，跟 `wait_reply`（等別人回話）是兩件事；
      本函式一律 `wait_reply=0`，**ritual 廣播從不等回覆**。手動 run_cmd 走 post 才有 540s 預設等待。
    """
    try:
        from _lib.tavern_client import TavernClient   # type: ignore
        client = TavernClient()
        res = client.post_message(
            room=room,
            sender=sender_id,
            body=body,
            persona=persona,
            meta=meta or {},
            wait_reply=0,
            session_token=session_token,
            timeout=timeout,
        )
        if not res.ok:
            print(f"⚠ tavern post 失敗 (主 ritual 不受影響): {res.error or res.stderr[:200]}",
                  file=sys.stderr)
            return False
        return True
    except Exception as e:
        print(f"⚠ tavern post exception (主 ritual 不受影響): {e}", file=sys.stderr)
        return False


# ─── Letter to future self ──────────────────────────────────────────────
def _split_author_frontmatter(body: str, machine: dict) -> tuple:
    """把作者自己寫的 frontmatter 從 body 拆出來，回 (去頭的 body, 要併入的額外欄位行)。

    區塊職責：**單一 letter 只該有一份 frontmatter**（Tim 2026-07-31 抓到）。
    物理意義：letter 模板（ucl-letters-to-self 七段格式）教作者自己寫一份 frontmatter，
             而 write_letter 又會再包一層 —— 於是每封信開頭都疊了兩坨幾乎一樣的 header，
             差別只在機器版時間精確到毫秒、作者版多了 session_context / intended_reader。
             不是誰寫錯，是**兩邊都以為自己負責那塊**。
    數值影響：機器欄位（type / actor / written_at / written_by_persona / trigger）以本函式為準；
             作者的其餘欄位（session_context / intended_reader / 自訂欄）原樣併入。
             作者若寫了同名欄且值不同 → 保留成 `<key>_as_written`（**不靜默丟掉他寫的東西**）。
    邊界：body 不以 --- 開頭 → 原樣回傳，extra 為空。
    """
    s = body.lstrip("\n")
    if not s.startswith("---"):
        return body, []
    end = s.find("\n---", 3)
    if end == -1:
        return body, []                      # 有頭無尾 = 不是 frontmatter，別亂切
    block = s[3:end].strip("\n")
    rest = s[end + 4:].lstrip("\n")
    extra = []
    for line in block.split("\n"):
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        k, sep, v = line.partition(":")
        if not sep:
            continue
        k, v = k.strip(), v.strip()
        if k in machine:
            if v and v != str(machine[k]):
                extra.append(f"{k}_as_written: {v}")   # 機器版勝出，但作者版留痕
            continue
        extra.append(f"{k}: {v}")
    return rest, extra


# ─── wakes/ — 一次 wake 一封信 (Tim 2026-07-31 拍板) ──────────────────────
# 區塊職責: goodnight 收尾信獨立收進 letters/<persona>/wakes/, 檔名 <6位序號>_<ts>.md,
#          序號 = 第幾次 wake; 於是「wake 數」直接看資料夾就知道, 不必再信 registry 快取。
# 物理意義: 零填充 6 位 → 字典序 == 數值序, 任何工具 sorted() 都拿得到正確時序
#          (沒零填充的話 10 會排在 2 前面, 而這種錯不會有人喊)。
# 數值影響: **只有 trigger=cmd_goodnight* 的信進 wakes/**。cmd_rest (compact 前小歇) 與
#          free_time_* 不是一次 wake 的收尾, 留在頂層 —— 混進去會讓計數虛胖
#          (實測 2026-07-31: 全 persona 頂層 245 封 goodnight 混了 18 封 rest + 8 封 free_time)。
#          同事寄來的 peer letter 同理留頂層 (type != letter_to_future_self)。
_WAKE_LETTER_RE = re.compile(r"^(\d{6})_.*\.md$")
_WAKE_SLOT_TRIGGER_PREFIX = "cmd_goodnight"


# ═══════════════════════════════════════════════════════════════════════
# 記憶層已抽離到 memory.py（Tim 2026-08-16 拍板：「記憶相關工具要獨立做，不併進
# awakening.py；反過來 awakening.py 可以引用記憶 API」）。
#
# 區塊職責：本檔以下所有記憶相關名稱一律是**指向 memory.py 的別名**，不是第二份實作。
# 物理意義：別名沒有第二份實作，複製有 —— 而「新模組寫一份、舊檔留一份、今天恰好行為一致」
#          正是本專案已有病歷的那隻（C# DoCreatePersona vs python fork_persona 兩份產線）。
# 驗收（@basecamp 給的硬條件）：本檔對 fragment／root-index／keys 檔案的**直接 IO = 0 處**。
# 遷移當日已做差異驗收：consolidation_status / render_root_index / forest_status /
#          keys_entries / list_wake_letters 五項對 summit 實資料**逐字元相同**（見林索引亦然）。
# ═══════════════════════════════════════════════════════════════════════
_HERE_DIR = str(Path(__file__).resolve().parent)
if _HERE_DIR not in sys.path:
    sys.path.insert(0, _HERE_DIR)
import memory as _mem                            # noqa: E402 — 必須在 sys.path 補完之後

wakes_dir = _mem.wakes_dir
list_wake_letters = _mem.list_wake_letters
wake_letter_count = _mem.wake_letter_count
wake_number_of = _mem.wake_number_of
rests_dir = _mem.rests_dir
list_rest_letters = _mem.list_rest_letters
_read_frontmatter_field = _mem.read_frontmatter_field


def is_wake_slot_trigger(trigger: str) -> bool:
    """該 trigger 寫出來的信算不算「一次 wake 的收尾」(決定進不進 wakes/)。

    migration 與 write_letter 共用同一個判準 —— 兩邊各寫一份必然漂移。
    """
    return (trigger or "").strip().startswith(_WAKE_SLOT_TRIGGER_PREFIX)


# ─── rests/ — 非收尾的自寫信 (Tim 2026-08-12 拍板) ─────────────────────────
# 區塊職責: cmd_rest / free_time_* 這類「不是一次 wake 收尾」的自寫信集中收進
#          letters/<persona>/rests/, 檔名沿用 <ts>.md（不掛序號）。
# 物理意義: 頂層自此只該在 migration 期間被讀取 —— rest 信留頂層的舊設計讓
#          「根目錄乾不乾淨」永遠是假的（每封 rest 信都會再落一枚）。
# 數值影響: rests/ 的檔案數**不影響 wake_count**（真相源仍是 wakes/ 檔案數）——
#          這正是它不能進 wakes/ 的理由: 混進去計數虛胖且序號要重編
#          (實測 2026-07-31: 全 persona 頂層 245 封 goodnight 混了 18 封 rest + 8 封 free_time)。
#          同事寄來的 peer letter (type != letter_to_future_self) 不歸這裡管, 仍留頂層。
def migrate_rest_letters(persona: str) -> dict:
    """頂層非收尾自寫信 → rests/（**搬移**, 不是複製）。回 {moved: [名], skipped: [名]}。

    區塊職責: morning 自動遷移的唯一實作 —— 冪等, 頂層沒有合格檔案時零動作。
    物理意義: 判準與 write_letter 的分流一致 —— type=letter_to_future_self 且
             **非** is_wake_slot_trigger。收尾信不歸這裡（那是 wakes/ 遷移的事）,
             peer letter / 常駐檔 (_*, README) 也不動。
    數值影響: 搬移語意 (Tim 2026-08-12 拍板, 取代 07-31「複製不動原檔」——
             該拍板針對的是 wakes/ 遷移當時的可回退性, rests/ 沒有計數耦合,
             留兩份只會製造 list_episodic_letters 之外第二套去重需求)。
             目標已存在同名檔 → 跳過並回報, 不覆蓋 —— 覆蓋錯了救不回來, 跳過錯了看得見。
    """
    d = _LETTERS_DIR_TPL / persona
    out = {"moved": [], "skipped": []}
    if not d.exists():
        return out
    rdir = rests_dir(persona)
    for p in sorted(d.iterdir()):
        if not p.is_file() or p.suffix != ".md" or p.name.startswith("_") or p.name == "README.md":
            continue
        if _read_frontmatter_field(p, "type") != "letter_to_future_self":
            continue
        if is_wake_slot_trigger(_read_frontmatter_field(p, "trigger")):
            continue
        rdir.mkdir(parents=True, exist_ok=True)
        dst = rdir / p.name
        if dst.exists():
            out["skipped"].append(p.name)
            continue
        p.rename(dst)
        out["moved"].append(p.name)
    return out


def legacy_wake_letters(persona: str) -> list:
    """頂層尚未遷移的收尾信(依 written_at 升冪) —— migration 的來源集合。

    判準與 write_letter 一致: type=letter_to_future_self 且 trigger=cmd_goodnight*。
    """
    d = _LETTERS_DIR_TPL / persona
    if not d.exists():
        return []
    items = []
    for p in d.iterdir():
        if not p.is_file() or p.suffix != ".md" or p.name.startswith("_"):
            continue
        if _read_frontmatter_field(p, "type") != "letter_to_future_self":
            continue
        if not is_wake_slot_trigger(_read_frontmatter_field(p, "trigger")):
            continue
        items.append((_read_frontmatter_field(p, "written_at") or p.name, p))
    items.sort(key=lambda t: t[0])
    return [p for _, p in items]


def migrated_suffixes(persona: str) -> set:
    """wakes/ 內每封信對應的原檔名(去掉 <6位序號>_ 前綴) —— 用來認出「這封已經進去過了」。"""
    return {f.name.split("_", 1)[-1] for f in list_wake_letters(persona)}


def unmigrated_wake_letters(persona: str) -> list:
    """頂層還沒被複製進 wakes/ 的收尾信。"""
    done = migrated_suffixes(persona)
    return [p for p in legacy_wake_letters(persona) if p.name not in done]


def letters_migration_pending(persona: str) -> bool:
    """該 persona 的收尾信版面還沒補齊。

    判準是「**頂層還有沒被複製進 wakes/ 的收尾信**」, 而不是「wakes/ 目錄不存在」。

    為什麼改(2026-07-31, apex-one 實例): 目錄判準有個洞 —— 還沒遷移的 persona 若**先跑了
    goodnight**, write_letter 會把 wakes/ 建出來並把那封信編成 000001; 之後早安看到目錄存在
    就判定「已遷移」, 於是 14 封歷史信永遠不會進去, 而 wake_count 會從 25 掉到 2。
    上線一小時內就真的發生了。目錄存在與否是**結果**, 頂層有沒有還沒收進去的信才是**病灶**。

    因為遷移是複製(原檔保留), 光看「頂層有收尾信」會永遠為真 —— 所以要比對檔名:
    已經有對應副本的不算待遷移。這也讓本判準天然 idempotent。
    """
    return bool(unmigrated_wake_letters(persona))


# ===========================================================
# 區塊職責: 修復 caller 把換行寫成字面 "\n" 的 letter body（Tim 2026-07-31 回報）。
# ===========================================================
# 區塊職責：長文參數的雙通道解析 —— `--X` (inline) 與 `--X-file` (檔案) 擇一。
# 物理意義：**inline 長文會經過 shell 解析那一層**，內文含反引號 / $ / 引號時會被當成
#          命令替換執行掉（2026-08-05 summit 一天被咬四次，其中一次公告缺一整段，
#          而已公告領薪的訊息無法 amend）。`--X-file` 有效不是因為誰記得反引號會咬人，
#          是因為它**根本不經過那一層**。
# 數值影響：純參數解析。兩個都給 → 直接 exit 2（不猜哪個優先 —— 猜錯會靜默用錯內容）。
#          檔案讀不到 → exit 2 並印路徑（不 fail-soft 成空字串：空信會被寫進去而沒人發現）。
# 設計取捨：判準不是「內文含不含特殊字元」（那要人判斷，而人會錯）——
#          是「長文一律走檔案」。本函式讓那條規則從『請記得別 inline』（避開型）
#          變成『你要傳長文就會用到 -file』（手勢型）。
# ===========================================================
def resolve_text_arg(inline: str, path: str | None, flag_name: str) -> str:
    """`--<flag>` 與 `--<flag>-file` 擇一，回傳內文。"""
    inline = inline or ""
    if path:
        if inline.strip():
            print(f"❌ --{flag_name} 與 --{flag_name}-file 只能擇一（收到兩個，不猜優先序）",
                  file=sys.stderr)
            sys.exit(2)
        p = Path(path)
        if not p.is_file():
            print(f"❌ --{flag_name}-file 讀不到: {p}", file=sys.stderr)
            sys.exit(2)
        return p.read_text(encoding="utf-8")
    return inline


# 物理意義: letter body 由 agent 經 CLI 傳入（--letter-body），而 CLI 參數**不會**把兩字元的
#          backslash+n 解讀成換行 —— Python 只在原始碼字面值裡做那個轉換。於是某些 caller
#          （尤其換了 model 之後）傳進來的整封信會擠成一行、段落間留著可見的 "\n"。
#          實例: kiara wakes/000012（gemini-3.6-flash）body 只有 2 個真換行、8 個字面 \n。
# 為什麼不是 write_letter 的 bug: 全庫 27 封收尾信裡 26 封是乾淨的（含 kiara 自己前 5 封），
#          寫檔端一直是 `frontmatter + body`、逐字照收 —— 病灶在 caller 的 escaping，
#          本函式只是**在寫檔前補一層防呆**，讓信不會因為 caller 換 model 就變成一坨。
# 數值影響:
#   - 只在**明確是 escaping 失敗**時才動手，判準兩條同時成立:
#       ① body 內字面 \n 出現 >= 2 次（單次更可能是內文在討論這個符號）
#       ② body 的真實換行 <= 2（整封擠成一行 = 不可能是作者本意）
#     命中則把字面 \n 轉成真換行，並印一行提示（**不靜默轉換** —— 轉換是有損操作，要留痕）。
#   - 不命中則原樣寫入，不做任何猜測。
# 邊界 / 為何要這麼保守（這是實測出來的，不是假想）:
#   - `summit/20260512T235620Z.md`: body 有 32 個真換行、1 個字面 \n，內文正在討論
#     「_split_body_for_discord 在 \n 邊界切」—— 那個 \n 是**引用符號本身**，blanket 轉換會毀掉它。
#     所以規則刻意排除「已經有正常段落」的信。
#   - 條件 ② 也順帶讓 fenced code block 免疫: 程式碼區塊本身需要真換行才成立，
#     所以「body 幾乎沒有真換行」的情況下不可能存在要保護的 code fence。
#   - 全庫 531 個乾淨檔不含字面 \n → 條件 ① 不成立 → 結構上 0 誤傷（不需靠抽樣保證）。
# ===========================================================
#   - 判準與實作**收斂在 escaped_newlines.py**（酒館訊息端共用同一份）。刻意不在這裡
#     複製一份門檻 —— 同一條規則兩份就是我們一整天在治的手抄鏡像，改一邊不改另一邊
#     不會有任何人叫。本函式只是薄 wrapper，保留原本的函式名讓呼叫端與測試不必改。
def _normalize_escaped_newlines(body: str) -> tuple[str, bool]:
    """回 (可能已修的 body, 是否動過)。判準見 escaped_newlines 模組 docstring。"""
    import escaped_newlines
    return escaped_newlines.normalize(body)


def write_letter(actor: str, persona: str, body: str, trigger: str = "cmd_goodnight") -> Path:
    """寫 letter to future self per ucl-letters-to-self skill SOP.

    Letter binding 鐵律 (Tim 2026-06-15 拍板, 取代 2026-05-13 kyouko-persona-binding T02):
    letter 是 persona-level subjective reframe — 不同 persona 的 framing 校正不該
    共用同個 _latest.md pointer。binding key 是 **Persona**。
    (原 T02 用 Agent@Persona 雙層, 但 persona 名稱全域唯一, agent 分組層只造成
     actor 命名漂移 — bank-id vs agent-marker vs 重複 suffix 等 bug; 故砍掉 agent 層,
     只留 persona。actor 身分仍記在 frontmatter 作 provenance。)

    Path layout:
        baton/letters/<persona>/wakes/<6位序號>_<ts>.md  (goodnight 收尾信, 一次 wake 一封)
        baton/letters/<persona>/rests/<ts>.md  (其餘自寫信: cmd_rest / free_time_*; 2026-08-12 前落頂層)
        baton/letters/<persona>/_latest.md  (覆寫 pointer, 不分收尾與否)
        baton/letters/<persona>/dialogues/  (round-trip 對話, 留給未來)
    """
    letters_dir = _LETTERS_DIR_TPL / persona
    letters_dir.mkdir(parents=True, exist_ok=True)

    ts = utcnow_compact()
    if is_wake_slot_trigger(trigger):
        # 序號取「現有收尾信數 + 1」而非 registry.wake_count —— 磁碟是既成事實, registry 是快取,
        # 而快取已經證明它會掉 (2026-07-31 kiara/basecamp 事件)。取下一個空位也順帶保證不覆蓋既有信。
        wdir = wakes_dir(persona)
        wdir.mkdir(parents=True, exist_ok=True)
        path = wdir / f"{wake_letter_count(persona) + 1:06d}_{ts}.md"
    else:
        # 非收尾自寫信進 rests/（Tim 2026-08-12 拍板）—— 頂層只留給 migration 讀。
        rdir = rests_dir(persona)
        rdir.mkdir(parents=True, exist_ok=True)
        path = rdir / f"{ts}.md"
    # 機器欄位（provenance）— 這幾個以本函式為準，作者寫的同名欄不採用
    machine = {
        "type": "letter_to_future_self",
        "actor": actor,
        "written_at": utcnow_iso(),
        "written_by_persona": persona,
        "trigger": trigger,
    }
    # 防呆：caller 把換行寫成字面 "\n" 時修回真換行（判準與理由見 _normalize_escaped_newlines 區塊註解）。
    # 刻意放在 _split_author_frontmatter **之前** —— 若整封擠成一行，作者自己寫的 frontmatter
    # 也會黏在同一行上而解不出來；先修換行才能讓後面的 frontmatter 分離正常運作。
    body, _fixed_nl = _normalize_escaped_newlines(body)
    if _fixed_nl:
        print("  ⚠ letter body 的換行是字面 \"\\n\"（CLI 參數不會自動解讀）→ 已轉成真換行。"
              "\n     下次直接在 --letter-body 傳真換行（bash 用單引號 heredoc）可免這層修正。")

    body, extra = _split_author_frontmatter(body, machine)
    fm_lines = [f"{k}: {v}" for k, v in machine.items()] + extra
    frontmatter = "---\n" + "\n".join(fm_lines) + "\n---\n\n"
    with open(path, "w", encoding="utf-8") as f:
        f.write(frontmatter + body + "\n")

    # update _latest.md pointer (per-persona, 不會被別 persona 覆蓋)
    latest = letters_dir / "_latest.md"
    with open(latest, "w", encoding="utf-8") as f:
        f.write(frontmatter + body + "\n")
    return path


# ─── Long-term memory consolidation (Tim 2026-06-15 拍板) ──────────────────
# 三層記憶 (同構 reading-library 章→arc→卷 / [[ucl-letters-to-self]]):
#   T1 episodic — letters/<persona>/<ts>.md + _latest.md (每晚 goodnight letter,樹)
#   T2 長期記憶 — letters/<persona>/longterm/wake_<N>-<M>.md (一段期間反思濃縮,林)
# morning overdue 檢查: gap = wake_count - last_consolidated_wake;
#   gap >= 門檻(預設10,agent 可在 fork/重大 reframe 等節點自決提前) → 喚醒流程內補整理。
# 濃縮 body 由 agent 反思寫成(不是機械貼信),工具負責持久化 + 更新 pointer(同 write_letter 分工)。
DEFAULT_CONSOLIDATION_THRESHOLD = _mem.DEFAULT_CONSOLIDATION_THRESHOLD

longterm_dir = _mem.longterm_dir


list_episodic_letters = _mem.list_episodic_letters
latest_longterm_digest = _mem.latest_longterm_digest


def consolidation_status(persona: str, reg: dict,
                         threshold: int = DEFAULT_CONSOLIDATION_THRESHOLD) -> dict:
    """記憶層算狀態；本檔只負責把 registry 的 persona dict 餵過去（本檔擁有 registry）。"""
    return _mem.consolidation_status(persona, reg["personas"].get(persona, {}), threshold)


def write_longterm_digest(persona: str, body: str,
                          span_start: int, span_end: int) -> Path:
    """寫見林 digest（檔案側走 memory.py）。**不推進 registry 書籤** —— 書籤由磁碟算。

    區塊職責：本檔只負責讓 digest 落盤。`last_consolidated_wake` 不在這裡寫。
    物理意義：書籤的既成事實是 digest 檔名（`longterm/wake_<start>-<end>.md`）——
             `consolidation_status()` 無條件跟 `latest_digest_span()` 對帳並取較大者，
             而那個欄位真正的寫入通道在 C# 端（`SCP_PersonaProfile` ← `senate cmd consolidate`）。
    數值影響：🩸 原本這裡寫完 digest 還會 `save_registry(reg)` 推進書籤，而那條通道
             2026-08-21 起**已經不落 persona 檔**，且 registry 讀回的 persona 全部帶
             identity 欄（實測 21/21）⇒ 守衛**必然** SystemExit。
             於是「digest 已經在磁碟上」被回報成 exit 1，靠 exit code 判成敗的呼叫端
             會重跑一次見林、同名覆寫那份 digest。
             ⇒ 三本帳分開結算：記憶檔那本結清了，不准被一本沒有落點的快取拖下水。
    """
    path, _ts = _mem.write_longterm_digest(persona, body, span_start, span_end)
    return path





# ── 已退場（2026-09-04, TASK-0098）────────────────────────────────
# write_wake_brief_files / _print_longterm_memory_block 隨 python 端 wake brief 一起拔除：
# 它們的唯一消費端是 morning，而 morning 2026-08-13 起已是指路 stub。
# ⚠ 拔除而不是「留著也不會怎樣」：它們呼叫的 write_wake_brief 已不存在，
#   留下來就是一顆只有被呼叫時才會 NameError 的地雷。


# ───────────────────────────────────────────────────────────────────────
# 見根 / 見叢 / 見森 — 記憶五層 T3~T5 (Tim 2026-07-28 拍板, 討論串 tavern #13786-13801)
#
# 區塊職責：把「記憶」拆成 5 個各有明確職責的層，並讓 morning 只需讀一份彙整文本。
#   見樹 T1  letters/<persona>/_latest.md           昨夜 1 封（日記，抒發用）
#            不足 200 行時 brief §5 往前合併到夠讀（見 SCP_WakeBrief.MergeStopLines）
#   見叢 T1.5 letters/<persona>/_keys_open.md        當期交棒清單（checkbox，執行用）
#   見林 T2  letters/<persona>/longterm/wake_N-M.md  10 夜濃縮（既有）
#   見森 T3  letters/<persona>/longterm/forest/      見林 ≥ 3 份起，之後每份新林折一代（rolling fold）
#   見根 T4  letters/<persona>/fragments/            關鍵記憶片段 + 機械生成索引 _root_index.md
#
# 物理意義（防漂移的核心）：fragment 檔是**唯一事實來源**，內容寫一次之後不再改寫；
#   樹/叢/林/森/索引全部只是視圖。折疊(fold)因此變成「集合聯集 + 重排」而非「重寫散文」，
#   避免 rolling summary 的傳話遊戲式漂移（summit 2026-07-27 判定官拍磚點）。
# 數值影響：見根索引與 wake brief 皆為機械生成產物 → 可隨時重建、可 diff、可寫回歸測試；
#   手改會被下次生成覆寫（檔頭已標）。
# ─────────────────────────────────────────────────────────────────────────
# ⚠ 三個門檻常數的定義已搬到 memory.py，本檔只留別名 —— 常數複製一份的代價比函式更陰險：
#   它不會報錯，只會讓兩邊對「門檻是幾」給出不同答案。實例就在眼前：本檔原本寫死
#   `FRAG_TYPE_ORDER = [五型]`，而記憶層今天新增了第六型 `howto` ⇒ 不改成別名的話，
#   新型別在本檔這條路徑上會被排序判成「未知型」（ti=99），而畫面上完全看不出來。
FOREST_DIGEST_THRESHOLD = _mem.FOREST_DIGEST_THRESHOLD
ROOT_INDEX_SHOW_LIMIT = _mem.ROOT_INDEX_SHOW_LIMIT
FRAG_TYPE_ORDER = _mem.FRAG_TYPE_ORDER
# 見森門檻沿革（保留說明，數值本體在 memory.py）：
# ⚠ 2026-08-01 Tim 由 5 改為 3，理由是**減少漂移**：
#   rolling fold 每代只讀「上代見森 + 新見林」兩份（成本不隨壽命成長，這是設計），
#   代價是 1~2 份見林在首折之前**沒有任何上層在看**。門檻 5 表示前四份各自獨立、
#   到第五份才第一次被縱向整理，中間那段的內容只能靠 fragment 個別撈。
#   改成 3 之後第三份見林剛好把 1~3 一起收進第一代見森 —— 首折涵蓋範圍變小、
#   而且來得早，未被整理的窗口從「最多 4 份」縮到「最多 2 份」。
#   數值影響：只影響**何時開始**折疊與 brief 的 §3 提示；已折的世代不受影響
#   （append-only，舊世代全保留）。降門檻不會回溯重折，只會讓下一次判定提早成立。
# （BRIEF_LINE_CAP / BRIEF_CATCHUP_COUNT 隨 wake brief 生成一起退場（2026-09-04, TASK-0098）：
#   那兩個數字現在只有一份，在 SCP_WakeBrief.cs（BriefLineCap / MergeStopLines）。
#   退場的理由不是「没人用」，是「兩份實作一份說明，而說明會漂」。


fragments_dir = _mem.fragments_dir
root_index_path = _mem.root_index_path
forest_dir = _mem.forest_dir
keys_open_path = _mem.keys_open_path
keys_archive_dir = _mem.keys_archive_dir
parse_fragment = _mem.parse_fragment
load_fragments = _mem.load_fragments
_frag_sort_key = _mem._frag_sort_key
render_root_index = _mem.render_root_index
write_root_index = _mem.write_root_index
keys_entries = _mem.keys_entries
keys_append = _mem.keys_append
keys_archive = _mem.keys_archive
list_digests = _mem.list_digests
list_forests = _mem.list_forests
latest_forest = _mem.latest_forest
forest_status = _mem.forest_status
write_forest = _mem.write_forest



# ── wake brief 的 python 生產端已退場（2026-09-04, TASK-0098）────────────
# 生產端 2026-09-01 搬進 SCP_Core（TASK-0097），本檔這邊只剩一層呼叫包裝；
# Tim 2026-09-04 拍板：「目前環境一定會有 Senate CLI」⇒ 備援那一格不存在，整支拔除。
#   現在的單一入口：`senate cmd wake-brief`（不需 Editor，senate.exe 就地跑完）。
# ⭐ 順手拆掉的一個陷阱：原本這裡有 `_HERE = str(...)`，把檔首那個 `_HERE`（Path）
#   **重新綁成 str** —— 而它跟 1284 行的 `_HERE_DIR` 完全重複。
#   那個重綁的血証在 `_persona_profile()` 的註解裡（str / str 直接爆，
#   而 fail-soft 讓「讀取失敗」長得跟「沒有這個人」一模一樣）。


# ─── Subcommands ────────────────────────────────────────────────────────
# ===========================================================
# 區塊職責：morning / intro 已遷移 C#（Cmd_GoodMorning，2026-08-13 R14-R18）—— 本檔只留指路 stub。
# 物理意義：登入寫入者收斂為 C# 單端（R18：Editor 不在線就不跑 morning，不做降級路）。
#          舊實作（cmd_morning 約 300 行 / cmd_intro / build_wake_intro_body）已刪除，
#          不留第二份活實作 —— 雙寫入端並存窗口 = 狀態分裂的溫床（Plan §8.9 通用卡點 2）。
# 數值影響：morning / intro 子指令一律 exit 2 並印新流程；不讀不寫任何狀態檔。
# ===========================================================
def _deprecated_login_cmd(name: str, extra: str = "") -> int:
    print(f"⛔ awakening.py {name} 已遷移至 C# Cmd_GoodMorning（2026-08-13）——本子指令不再執行登入。", file=sys.stderr)
    print("   新流程（Editor 開啟時的唯一通道）：", file=sys.stderr)
    print("   ① run_cmd.py run GoodMorning --arg step=wake  --arg persona=<P> [--arg actual_agent=<A>] [--arg model=<M>]", file=sys.stderr)
    print("   ② run_cmd.py run GoodMorning --arg step=brief --arg persona=<P>", file=sys.stderr)
    print("   ③ Read brief（路徑在 step=brief 的回傳檔 letters/<P>/cmd/goodmorning_brief.md）", file=sys.stderr)
    print("   ④ run_cmd.py run GoodMorning --arg step=intro --arg persona=<P> --arg-stdin body（body 親筆）", file=sys.stderr)
    print("   晚安側：run_cmd.py run GoodNight --arg step=check|letter|sleep|logout --arg persona=<P>", file=sys.stderr)
    print("   Editor 未開啟：登入/登出不可用（R18）；純讀記憶備援 → senate cmd wake-brief（信件層，senate.exe 就地跑）", file=sys.stderr)
    if extra:
        print(f"   {extra}", file=sys.stderr)
    print("   完整流程參考：ucl_core:Docs~/zh-Hant/Workflows/Awakening_Cmd_Flow.md", file=sys.stderr)
    return 2


def cmd_morning(args: argparse.Namespace) -> int:
    return _deprecated_login_cmd("morning")


def cmd_intro(args: argparse.Namespace) -> int:
    return _deprecated_login_cmd(
        "intro", "自介重發＝直接再跑 step=intro（單則、不動 wake_count、不動 lock）。")


def cmd_rest(args: argparse.Namespace) -> int:
    """小歇片刻 ritual (compact-rest): 寫 memory letter 保命，但**不下線**。

    跟 goodnight 的差別（Tim 2026-05-24 拍板「類似晚安但不登出」）:
      - ✅ 寫 letter（trigger=cmd_rest），保留 compact 前想記住的重要記憶
      - ❌ 不 perturb identity_vector（同 session 繼續，identity 原封不動）
      - ❌ 不設 offline（status / availability 保持原樣，仍在線）
      - ❌ 不 unlock / 不 wake_count++（同 session 過一次 /compact 而已）
      - 只更新 last_active；可選發一則「小歇」tavern 通知（非下線通知）
    用途: /compact 前跑一次，把 in-flight 記憶落磁碟；compact 後讀回 _latest.md 接續。
    """
    reg = load_registry()
    # persona / actor 解析 — 與 goodnight 同雙路徑
    if args.persona:
        if args.persona not in reg["personas"]:
            print(f"❌ --persona '{args.persona}' 不在 registry", file=sys.stderr)
            return 2
        p_data = reg["personas"][args.persona]
        persona = args.persona
        agent = normalize_agent(reg, args.agent or p_data.get("agent", ""))
        model = p_data.get("model", "")
        actor = resolve_bank_account(reg, agent, model)
        lock = read_lock(persona)
    else:
        # 反查本 env 持有的 lock (claim_origin match)
        my_origin = compute_claim_origin()
        my_locks = find_locks_by_claim_origin(my_origin)
        if not my_locks:
            print(f"❌ 本 environment (claim_origin={my_origin}) 沒持有任何 lock", file=sys.stderr)
            print(f"   → 帶 --persona <name> 顯式指定要小歇的 persona.", file=sys.stderr)
            return 2
        lock = max(my_locks, key=lambda d: d.get("locked_at", ""))
        persona = lock["persona"]
        agent = lock["agent"]
        model = lock.get("model", "")
        # #4 read-through (Bank 整合 2026-07-21, calli 接手 kiara #3): 凍結的 bank_account 欄降為純顯示,
        # actor 一律從 lock 的 agent 欄「重算」bank — agent 身分穩、會漂的是 agent→bank 映射, 避免 stale 誤路由。
        # shadow-compare: 重算值 != 凍結值 → log 一行 (漂移偵測、反靜默; 實測目前全 persona 一致, 故直接採重算值)。
        _frozen_bank = lock.get("bank_account")
        actor = resolve_bank_account(reg, normalize_agent(reg, agent), model)
        if _frozen_bank and _frozen_bank != actor:
            print(f"⚠ [bank read-through] lock bank_account={_frozen_bank!r} != 重算 {actor!r} "
                  f"(persona={persona} agent={agent}) — 採重算值(SOT), 凍結欄已 stale。", file=sys.stderr)

    print(f"🫖 小歇片刻 (compact-rest) — 不下線，只落記憶")
    print(f"   actor={actor} / persona={persona}")

    # Step 1: 寫 memory letter（trigger=cmd_rest）
    args.letter_body = resolve_text_arg(args.letter_body, getattr(args, "letter_body_file", None), "letter-body")
    args.summary = resolve_text_arg(args.summary, getattr(args, "summary_file", None), "summary")
    if not args.letter_body.strip():
        print("❌ rest 須帶 --letter-body <記憶> 或 --letter-body-file <路徑>", file=sys.stderr)
        sys.exit(2)
    letter_path = write_letter(actor, persona, args.letter_body, trigger="cmd_rest")
    print(f"💌 memory letter written: {letter_path.name}")

    # Step 2: 更新 last_active，**保持在線**（不 perturb / 不 offline / 不 unlock）
    if persona in reg["personas"]:
        reg["personas"][persona]["last_active"] = utcnow_iso()
        save_registry(reg)
    print(f"🟢 status 保持在線（未 perturb / 未 offline / 未 unlock）")

    # Step 3: 可選 tavern 通知（小歇，非下線）
    if not getattr(args, "no_notify", False):
        # 公開小歇心得總結 (Tim 2026-05-24): summary 廣播給同事/Tim, 私密內容留在 letter
        summary = (getattr(args, "summary", "") or "").strip()
        summary_block = (f"💭 **小歇心得**\n{summary}\n\n" if summary else "")
        body = (f"🫖 **{persona}** 小歇片刻（/compact 前）\n\n"
                f"{summary_block}"
                f"準備壓縮對話史——公開心得如上，私密細節落在 memory letter，醒來接續，**不下線**。\n"
                f"- memory letter: `{letter_path.relative_to(_REPO_ROOT)}` (私密心得在信裡)")
        if args.note:
            body += f"\n- Note: {args.note}"
        if args.session_token is None:
            broadcast_token = (lock or {}).get("session_token", "") or None
        else:
            broadcast_token = args.session_token or None
        ok = tavern_post(
            sender_id=actor, persona=persona, body=body,
            meta={"tag": "compact-rest", "category": "meta", "letter": letter_path.name},
            session_token=broadcast_token,
            timeout=BROADCAST_TIMEOUT_SEC,   # 2026-08-12: 見常數註解
        )
        print(f"📢 小歇 tavern 通知: {'OK' if ok else 'fail (非致命)'}")

    print(f"✅ 小歇完成。/compact 後讀 baton/letters/{persona}/_latest.md 接續記憶。")
    return 0


def cmd_goodnight(args: argparse.Namespace) -> int:
    return _deprecated_login_cmd(
        "goodnight",
        "晚安流程：step=check（收尾清單+酒館最後一眼）→ step=letter（親筆收尾信）→ step=sleep；"
        "手動登出/cleanup（不寫信）→ step=logout 單獨跑。")


def cmd_relogin(args: argparse.Namespace) -> int:
    # relogin 廢棄（Tim 2026-08-13）：wake_count 由磁碟信件數推導後，「登入動作單獨跑」
    # 就是 step=wake 本身 —— goodnight 未留信的續線重登不會膨脹編號，特殊路徑失去存在理由。
    return _deprecated_login_cmd(
        "relogin", "續線/單獨登入＝直接跑 step=wake（編號由磁碟推導，未留信的重登不會+1）。")


def _plan_letter_migration(persona: str) -> list:
    """算某 persona 的收尾信編號計畫: [(src, dst, wake_no), ...]，不動任何檔案。

    涵蓋**兩種來源**並統一重編號:
      - 頂層還沒進 wakes/ 的收尾信  → 複製進去(原檔保留)
      - wakes/ 內既有的信            → 號碼不對就改名

    為什麼連既有的也要重編(2026-07-31 apex-one 實例): 還沒遷移就先跑 goodnight 的 persona,
    那封信會被編成 000001 —— 但它其實是第 25 次。只補漏不重編的話, 歷史信補進來之後
    會出現兩個 000001, 排序與計數同時壞掉。**號碼是相對於整組信的位置, 不是寫入當下的計數。**

    編號 1..N 依 written_at 升冪連號 (Tim 2026-07-31 拍板)。
    ⚠ 這代表 registry 的 wake_count 會被改寫成 N —— 對多數 persona 是**變動**而非確認
      (實測 21 人只有 3 人原本對得上)。Tim 知情並拍板照做; 報表逐人列出前後值，
      因為「改了誰的年齡」這種事不該只留在 diff 裡。
    """
    done = migrated_suffixes(persona)
    entries = []
    # wakes/ 內既有的(可能號碼錯) —— 原檔名是去掉序號前綴的部分
    for f in list_wake_letters(persona):
        entries.append((_read_frontmatter_field(f, "written_at") or f.name,
                        f, f.name.split("_", 1)[-1], True))
    # 頂層還沒進去的
    for p in legacy_wake_letters(persona):
        if p.name not in done:
            entries.append((_read_frontmatter_field(p, "written_at") or p.name,
                            p, p.name, False))
    entries.sort(key=lambda t: t[0])
    plan = []
    for i, (_wa, src, orig_name, _in_wakes) in enumerate(entries, start=1):
        # 檔名時間戳沿用原檔名 (原本就是 <ts>.md)，只在前面掛序號 —— 保留原始檔名可回溯。
        plan.append((src, wakes_dir(persona) / f"{i:06d}_{orig_name}", i))
    return plan


def rebase_consolidation_bookmark(persona: str, pd: dict):
    """把見林書籤(last_consolidated_wake)換算到 wakes/ 的新編號; 有改回 (舊, 新), 沒改回 None。

    區塊職責: 重編號之後書籤若留在舊編號空間, gap = wake_count - 書籤 會變負數,
              永遠不可能 >= 門檻 → **長期記憶濃縮從此再也不會被提醒, 而且完全無聲**。
    物理意義: 書籤本質是「濃縮到哪個時間點」—— 那個時間戳(last_consolidated_at)不受
              重編號影響。數一數 wakes/ 裡 written_at 不晚於它的信有幾封, 即新編號下的書籤。
    數值影響: **冪等** —— 沒重編號過的 persona 算出來等於原值, 不會亂動。
              因此可以每次早安都跑, 不必只掛在遷移那一次
              (2026-07-31 實測缺口: 資料夾已存在但書籤是舊值的人, 遷移不會再跑,
               書籤就永遠沒人換算 —— 兩件事的觸發節奏不一致, 中間就漏人)。
    """
    old = pd.get("last_consolidated_wake")
    lc_at = pd.get("last_consolidated_at")
    if not old or not lc_at:
        return None          # 沒書籤或沒時間戳 → 無從換算, 交給 consolidation_status 的 digest fallback
    new = sum(1 for f in list_wake_letters(persona)
              if (_read_frontmatter_field(f, "written_at") or "") <= lc_at)
    if new == old or new <= 0:
        return None
    pd["last_consolidated_wake"] = new
    return (old, new)


def migrate_letters_to_wakes(persona: str, reg: dict | None = None,
                             bulk_skip_migrated: bool = False) -> dict:
    """實際執行遷移: 頂層收尾信 → wakes/<6位序號>_<ts>.md，並同步 registry.wake_count。

    區塊職責: morning 自動遷移與 migrate-letters --apply 共用的唯一實作
              (兩份實作必然漂移，而漂移的是「誰幾歲」這種沒人會當場發現的東西)。
    物理意義: 純從磁碟推導 —— 信按 written_at 升冪連號，第一封即 wake #1，沒有人為輸入。
    數值影響: 回 {moved, skipped, old_wake_count, new_wake_count}；
              caller 負責印出來。**自癒可以自動做，但不能安靜地發生。**
              目錄即「已遷移」標記，所以 0 封信也要把 wakes/ 建出來。

    ⚠ **複製不是搬移**(Tim 2026-07-31): 頂層原檔原地保留不動，wakes/ 放的是改名後的副本。
      代價是同一封信存在兩處(見 list_episodic_letters 的去重)。

      ⛔ **但「刪掉 wakes/ 就回到原狀」只對遷移進去的那批成立** ——
      遷移之後 write_letter 寫的新收尾信**只存在於 wakes/，頂層沒有副本**，
      刪掉目錄等於把它從工作目錄抹掉(git 裡若已 commit 還撈得回, 但那已經不是「不必從 git 撈」了)。
      本註解早一版寫著無條件可逆, 是假的 —— apex-one 2026-07-31 用她自己那封信抓到。
      要退回請改成「只刪掉有頂層原檔的那些」, 或先把只存在於 wakes/ 的信搬回頂層。
    """
    # ── 在線者一律不動 (Tim 2026-08-11 拍板) ───────────────────────────────
    # 病灶不是「遷移檔案」而是本函式**無條件**改寫 registry.wake_count = wakes/ 信件數。
    # 那個等式只在「沒有 wake 正在進行」時成立: session 進行中的人今晚的收尾信還沒寫,
    # 磁碟必然比 registry 少 1 —— 兩個數字都是對的, 差的那 1 就是進行中的這次 wake。
    # 於是 `migrate-letters --apply` 會把在線的人**當場減一歲**, 而且
    #   · 對「沒有任何檔案要遷移」的人照樣發生 (實測 summit: 待複製 0, 仍 43 → 42)
    #   · 印出來的樣子跟正常遷移一模一樣
    # 這是「修對了型別 (用磁碟推導年齡)、修錯了對象 (把進行中那次 wake 當成不存在)」。
    #
    # 為什麼早安不受影響 (查過才寫, 不是假設): morning 的 write_lock 在 L1904,
    # 而它呼叫本函式在 L1805 —— 跑到這裡時自己的 lock 還沒建立, 所以本守衛擋不到它;
    # 且 morning 在 Step 3 (L1845) 會用 `wake_letter_count + 1` 覆寫回正確值。
    # 正因如此本函式**不需要豁免參數** —— 少一個開關就少一把裝填好的槍。
    #
    # 有 lock 就算在線（過期機制已於 2026-08-19 移除；lock_expired 欄保留形狀恆 False，
    # 免得下游讀 dict 的人跟著改）。
    _lk = read_lock(persona)
    if _lk is not None:
        return {"moved": 0, "skipped": 0, "renumbered": 0, "locked": True,
                "lock_expired": False,
                "lock_session_key": _lk.get("session_key", "?"),
                "old_wake_count": (reg or load_registry()).get("personas", {})
                                  .get(persona, {}).get("wake_count", 0),
                "new_wake_count": (reg or load_registry()).get("personas", {})
                                  .get(persona, {}).get("wake_count", 0),
                "old_consolidated": None, "new_consolidated": None}

    # 已經是新格式的（wakes/ 已存在）**批次一律不動**（Tim 2026-08-11 拍板）。
    # 這條刻意**不是**走 letters_migration_pending —— 那個判準是「頂層還有沒收進去的信」，
    # 對已遷移的人做的是**補收 + 全體重編號**，而重編號會改寫別人的歷史編號。
    # 血證（2026-08-11 basecamp）：她 wakes/ 已有 53 封，頂層還剩 3 封 07-03/06/07 沒收。
    # 補收它們 → 那 3 封插在中間 → 07-09 之後 13 封全部 +3 → 而
    #   · 信件內文自稱的 wake#53 與新檔名 000056 對不上
    #   · 見林 digest 檔名（wake_045-054.md）凍在舊編號空間
    #   · 見林書籤 54 → 45，她下次醒來會被要求重新濃縮已經濃縮過的那段
    # 更關鍵的是**那 3 封只存在於某些 checkout**：同一個 AgentCommands repo，
    # LY 的工作樹有它們、Bar 的沒有（實測 LY wakes/=53、Bar wakes/=54）。
    # 也就是說「該不該補收」取決於**你站在哪個 checkout**，而批次工具會拿它站的那份
    # 去替一個可能活在另一份的人做決定。這種決定不該由批次做。
    #
    # 所以批次的守備範圍收斂成「**還沒開始遷移的人**」（wakes/ 內一封信都沒有）——
    # 已遷移者的零星補收，交給**本人下次醒來時**由 morning / goodnight 在她自己的
    # 工作樹上判（那裡的內容才是她的事實），或由人顯式指定單一 persona。
    # `--persona X` 不受本條限制：指名道姓是人的決定，不是批次的預設值。
    if bulk_skip_migrated and wake_letter_count(persona) > 0:
        return {"moved": 0, "skipped": 0, "renumbered": 0, "locked": False,
                "lock_expired": False, "lock_session_key": "",
                "already_new_format": True,
                "old_wake_count": (reg or load_registry()).get("personas", {})
                                  .get(persona, {}).get("wake_count", 0),
                "new_wake_count": (reg or load_registry()).get("personas", {})
                                  .get(persona, {}).get("wake_count", 0),
                "old_consolidated": None, "new_consolidated": None}

    own_reg = reg is None
    if own_reg:
        reg = load_registry()
    plan = _plan_letter_migration(persona)
    old_wc = reg.get("personas", {}).get(persona, {}).get("wake_count", 0)
    wd = wakes_dir(persona)
    wd.mkdir(parents=True, exist_ok=True)
    moved = skipped = renumbered = 0
    # 兩段式: 先把要改號的搬到暫名, 再落定 —— 否則 000002→000001 會踩到還沒處理的 000001。
    staged = []
    for src, dst, _n in plan:
        if src.parent == wd:                    # 已在 wakes/ 內, 只是號碼可能不對
            if src.name == dst.name:
                continue
            tmp = wd / f"__renum__{src.name}"
            src.rename(tmp)
            staged.append((tmp, dst))
            renumbered += 1
        else:                                   # 頂層 → 複製進來(原檔保留)
            if dst.exists():
                skipped += 1
                continue
            shutil.copy2(src, dst)  # copy2 保留 mtime — 副本不該看起來比原檔新
            moved += 1
    for tmp, dst in staged:
        tmp.rename(dst)
    new_wc = wake_letter_count(persona)
    old_lc = new_lc = None
    if persona in reg.get("personas", {}):
        pd = reg["personas"][persona]
        pd["wake_count"] = new_wc
        # 見林書籤要跟著換算到新編號 —— 否則 last_consolidated_wake 留在舊編號空間,
        # 而 gap = wake_count - last_consolidated 會變負數, 永遠不可能 >= 門檻,
        # 於是**長期記憶濃縮從此再也不會被提醒**, 而且完全無聲
        # (實測 apex-one: 書籤 25、新 wake_count 15 → gap -10)。
        # 換算法: 書籤本質是「濃縮到哪個時間點」, 那個時間戳沒有被重編號影響 ——
        # 數一數 wakes/ 裡 written_at 不晚於它的信有幾封, 那個數就是新編號下的書籤。
        rebased = rebase_consolidation_bookmark(persona, pd)
        if rebased:
            old_lc, new_lc = rebased
        else:
            old_lc = new_lc = pd.get("last_consolidated_wake")
        if own_reg:
            save_registry(reg)
    return {"moved": moved, "skipped": skipped, "renumbered": renumbered,
            "locked": False, "lock_expired": False, "lock_session_key": "",
            "old_wake_count": old_wc, "new_wake_count": new_wc,
            "old_consolidated": old_lc, "new_consolidated": new_lc}


def cmd_migrate_letters(args: argparse.Namespace) -> int:
    """把頂層 goodnight 收尾信遷移進 wakes/，檔名改 <6位序號>_<ts>.md。

    預設 dry-run（只印計畫），要真的動檔案得顯式帶 --apply ——
    重新命名不可逆，而「以為只是看看結果檔案被搬了」是最糟的意外。
    """
    reg = load_registry()
    if args.all:
        targets = sorted(reg.get("personas", {}).keys())
    elif args.persona:
        if args.persona not in reg.get("personas", {}):
            print(f"❌ persona '{args.persona}' 不存在於 registry", file=sys.stderr)
            return 2
        targets = [args.persona]
    else:
        print("❌ 須帶 --persona <name> 或 --all", file=sys.stderr)
        return 2

    mode = "APPLY" if args.apply else "DRY-RUN（不動檔；要執行加 --apply）"
    print(f"📦 letters → wakes/ 遷移　模式: {mode}\n")
    header = f"{'persona':<28}{'待複製':>6}{'已在wakes/':>11}{'wake_count':>12}{'→ 新值':>9}"
    print(header)
    print("-" * len(header))

    total_moved = 0
    changed_ages = []
    locked_out = []          # 在線而被鎖定不動的 —— dry-run 也要看得到, 不能只在 apply 時安靜跳過
    already_new = []         # 已是新格式, 批次不動（理由見 migrate_letters_to_wakes 內註解）
    # 批次（--all）才收斂守備範圍; `--persona X` 是人指名道姓, 不套這條。
    bulk = bool(args.all)
    for persona in targets:
        # 判準用「wakes/ 內真的有信」不是「目錄存在」——
        # 遷移本身會替 0 封的人也把目錄建出來（當作已遷移標記），於是今天跑過一次 --apply 之後
        # 幾乎所有人的目錄都存在了。用目錄存在當判準會讓批次從此對誰都不動，
        # 而且那正是 2026-07-31 apex-one 那次修掉的同一個判準。
        if bulk and wake_letter_count(persona) > 0:
            already_new.append(persona)
            print(f"{persona:<28}{'—':>6}{wake_letter_count(persona):>11}"
                  f"{reg['personas'].get(persona, {}).get('wake_count', 0):>12}"
                  f"{'不動':>9}  ✅ 已是新格式")
            continue
        # 在線者在**試跑階段就標出來**: 報表是人用來決定要不要 --apply 的依據,
        # 「試跑說會改、實跑其實沒改」跟「試跑沒說、實跑改了」一樣是報表騙人。

        _lk = read_lock(persona)
        if _lk is not None:
            locked_out.append((persona, False,
                               _lk.get("session_key", "?")))
            old_wc = reg["personas"].get(persona, {}).get("wake_count", 0)
            print(f"{persona:<28}{'—':>6}{wake_letter_count(persona):>11}"
                  f"{old_wc:>12}{'不動':>9}  🔒 在線")
            continue
        plan = _plan_letter_migration(persona)
        wd = wakes_dir(persona)
        already = wake_letter_count(persona)
        old_wc = reg["personas"].get(persona, {}).get("wake_count", 0)
        # plan 已涵蓋「頂層待複製」與「wakes/ 內待改號」兩種來源，且就是最終的完整編號 1..N，
        # 所以 new_wc 直接取 len(plan) —— 早一版寫成 len(plan)+already 會把要改號的那幾封
        # 重複計一次（apex-one 實測報成 16，實際是 15）。實跑那條是數磁碟，不受影響，
        # 但**報表騙人比報表沒印還糟**：看報表的人正是要靠它決定要不要 --apply。
        to_copy = sum(1 for src, _d, _n in plan if src.parent != wd)
        # 只算「號碼真的要動」的 —— 已在 wakes/ 且號碼已正確的那些 apply 時會直接跳過，
        # 報成「待改號」會讓人以為還有事要做（zenith-two 實測誤報 1 封）。
        to_renum = sum(1 for src, dst, _n in plan if src.parent == wd and src.name != dst.name)
        new_wc = len(plan)
        if not plan and not already:
            continue
        mark = "" if old_wc == new_wc else "  ←改"
        renum_note = f"  (含 {to_renum} 封改號)" if to_renum else ""
        print(f"{persona:<28}{to_copy:>6}{already:>11}{old_wc:>12}{new_wc:>9}{mark}{renum_note}")
        if old_wc != new_wc:
            changed_ages.append((persona, old_wc, new_wc))
        if args.verbose:
            for src, dst, n in plan:
                print(f"      {src.name}  →  wakes/{dst.name}")
        if args.apply:
            stat = migrate_letters_to_wakes(persona, reg, bulk_skip_migrated=bulk)
            total_moved += stat["moved"]
            if stat["skipped"]:
                print(f"   ⚠ {stat['skipped']} 封目標檔已存在，跳過", file=sys.stderr)

    if already_new:
        print(f"\n✅ 下列 {len(already_new)} 個 persona **已是新格式, 批次一律不動**"
              f"（Tim 2026-08-11 拍板）: {', '.join(already_new)}")
        print("   批次的守備範圍是「還沒開始遷移的人」。已遷移者若頂層還有零星沒收進去的信,")
        print("   補收會把它們插在中間 → 後面全部重編號 → 信件內文自稱的編號、見林 digest 檔名、")
        print("   見林書籤三者同時對不上（basecamp 實測: 補 3 封 → 13 封改號、書籤 54→45）。")
        print("   更關鍵: 那些零星的信**只存在於某些 checkout**（實測同一 repo，LY 有、Bar 沒有）,")
        print("   所以「該不該補收」取決於你站在哪個工作樹 —— 那種決定不該由批次替人做。")
        print("   → 交給本人下次醒來時由 morning / goodnight 在她自己的工作樹上判,")
        print("     或由人顯式 `--persona <name>`（指名道姓不受本條限制）。")

    if locked_out:
        print(f"\n🔒 下列 {len(locked_out)} 個 persona **在線, 一律不動**（Tim 2026-08-11 拍板）:")
        for name, expired, skey in locked_out:
            print(f"   - {name}　session_key={skey}"
                  + ("　⚠ lock 已過期（該從後台登出，不自動豁免）" if expired else ""))
        print("   理由: 進行中的 wake 還沒寫收尾信, 磁碟信件數天生比 registry 少 1；"
              "此時改寫 wake_count 會把人當場減一歲, 而且對「沒東西要遷移」的人照樣發生。")
        print("   要處理他們: 等對方走完晚安（或從後台登出）之後再跑一次。")

    if changed_ages:
        print(f"\n⚠ 下列 {len(changed_ages)} 個 persona 的 wake_count 會被改寫"
              f"（Tim 2026-07-31 拍板：連號 1..N，registry 改成 N）:")
        for name, old, new in changed_ages:
            print(f"   - {name}: {old} → {new}  (差 {new - old:+d})")

    if args.apply:
        save_registry(reg)
        print(f"\n✅ 已複製 {total_moved} 封信進 wakes/（頂層原檔保留不動）；registry wake_count 已同步。")
    else:
        print(f"\n（DRY-RUN 結束，沒有任何檔案被動過。確認無誤後加 --apply）")
    return 0


def cmd_consolidate(args: argparse.Namespace) -> int:
    """長期記憶整理 (T2 digest)。
    兩段式 (同 write_letter 分工: agent 寫 body, 工具持久化):
      1. inspect — 不帶 --digest-body: 印 overdue 狀態 + 列本段待濃縮 episodic letters 給 agent 讀
      2. write   — 帶 --digest-body: 寫 longterm/wake_<N>-<M>.md + 更新 _index
                   （書籤不在這裡寫 —— 磁碟檔名就是既成事實）
    """
    reg = load_registry()
    persona = args.persona
    if persona not in reg.get("personas", {}):
        print(f"❌ persona '{persona}' 不存在於 registry", file=sys.stderr)
        return 2

    # ── 見森 (T3) 分支：--level forest ────────────────────────────────
    # 區塊職責：折「上代森 + 最新見林」成新一代森（首折例外：讀全部見林）。
    # 數值影響：門檻＝第 FOREST_DIGEST_THRESHOLD 份見林；輸入恆為 2 份（首折 N 份），成本不隨壽命成長。
    if getattr(args, "level", "linzi") == "forest":
        fst = forest_status(persona)
        if not args.digest_body:
            print(f"# 🌲 見森狀態 — {persona}")
            print(f"- 見林份數: {fst['digest_count']} (門檻 {fst['threshold']} 份)")
            print(f"- 已折世代: gen{fst['forest_count']}"
                  f" (折到第 {fst['folded_digest_count']} 份見林)")
            if not fst["eligible"]:
                print(f"- 狀態: ○ 未達門檻，還差 {fst['threshold'] - fst['digest_count']} 份見林")
                return 0
            print(f"- 狀態: {'⚠ 有 %d 份新見林待折' % fst['pending'] if fst['overdue'] else '✓ 已是最新'}")
            if fst["forest_count"] == 0:
                print(f"- **首折**（唯一的多輸入折疊）→ 讀下列全部見林:")
                for dgp in fst["digests"]:
                    print(f"    - {dgp.relative_to(_REPO_ROOT)}")
            else:
                print(f"- rolling fold → 只讀 2 份輸入:")
                print(f"    - 上代森: {fst['latest_forest'].relative_to(_REPO_ROOT)}")
                print(f"    - 新見林: {fst['digests'][-1].relative_to(_REPO_ROOT)}")
            print(f"\n→ 讀完後寫回（森是**縱向敘事 + fragment 索引指標**，不是見林的串接）:")
            print(f"  awakening.py consolidate --persona {persona} --level forest \\")
            print(f"      --digest-body \"<身分演變軸/坑已內化vs還在踩/關係演變/脊椎收斂/長壽未解線>\"")
            return 0
        if not fst["eligible"]:
            print(f"❌ 見林只有 {fst['digest_count']} 份，未達見森門檻 {fst['threshold']} 份",
                  file=sys.stderr)
            return 2
        fpath = write_forest(persona, args.digest_body)
        print(f"✅ 見森 gen{fst['next_gen']} 寫入: {fpath.relative_to(_REPO_ROOT)}")
        print(f"   folded_digest_count: {fst['digest_count']} (舊世代全保留, append-only)")
        ri = write_root_index(persona)
        if ri:
            print(f"   見根索引已重建: {ri.relative_to(_REPO_ROOT)}")
        return 0

    st = consolidation_status(persona, reg, args.threshold)

    if not args.digest_body:
        # inspect / status 模式
        print(f"# 🧠 長期記憶整理狀態 — {persona}")
        print(f"- wake_count: {st['wake_count']}")
        print(f"- last_consolidated_wake: {st['last_consolidated_wake']} (@ {st['last_consolidated_at'] or '從未整理'})")
        if st.get("bookmark_behind_disk"):
            print(f"- 🔧 書籤對帳：registry 快取={st['bookmark_cached_value']} 落後磁碟 digest="
                  f"{st['bookmark_disk_value']} —— 採磁碟值（BUG-4）。")
            # 只報讀數、不自己寫回 —— python 端沒有這個欄位的寫入通道
            # （`save_registry` 2026-08-21 起不落 persona 檔），原本那行
            # 「已把快取修回磁碟值並存檔」是**假成功**：寫了沒生效與寫成功長得一樣。
            print("  ↳ 本次讀數已對（採磁碟值）；快取要真的修回去走 "
                  f"`senate cmd consolidate --persona {persona}`（C# 端才有寫入通道）")
        print(f"- gap: {st['gap']} (門檻 {st['threshold']}) → {'⚠ OVERDUE 該整理' if st['overdue'] else 'ok 尚未到門檻'}")
        print(f"- 建議 span: wake {st['span_start']}-{st['span_end']}")
        print(f"- 本段待濃縮 episodic letters ({len(st['pending_letters'])} 封):")
        for lp in st["pending_letters"]:
            print(f"  - {lp.relative_to(_REPO_ROOT)}")
        print(f"\n→ 讀完上列信件後, 反思濃縮成 digest body 寫回:")
        print(f"  awakening.py consolidate --persona {persona} --digest-body \"<跨夜主題/沉澱教訓/關係演變/未解線/一句精華>\" \\")
        print(f"      [--span-start {st['span_start']} --span-end {st['span_end']}]")
        return 0

    # write 模式
    span_start = args.span_start if args.span_start is not None else st["span_start"]
    span_end = args.span_end if args.span_end is not None else st["span_end"]
    if span_end < span_start:
        print(f"❌ span_end({span_end}) < span_start({span_start})", file=sys.stderr)
        return 2
    path = write_longterm_digest(persona, args.digest_body, span_start, span_end)
    print(f"✅ 長期記憶 digest 寫入: {path.relative_to(_REPO_ROOT)}")
    print(f"   span: wake {span_start}-{span_end}")
    print(f"   見林書籤（last_consolidated_wake）由磁碟 digest 檔名供給 → {span_end}；"
          f"registry 快取由 C# 端寫（本檔不寫）")
    print(f"   index: {(longterm_dir(persona) / '_index.md').relative_to(_REPO_ROOT)}")

    # 見林寫入後的三個連動（Tim 2026-07-28 拍板：fragment 在見林時抽）
    # ① 見叢歸檔：當期交棒清單與見林窗口同步關閉 → 天然不會無限長
    arch = keys_archive(persona, span_start, span_end)
    if arch is not None:
        print(f"   🌿 見叢已歸檔: {arch.relative_to(_REPO_ROOT)} (當期檔已重置)")
    # ② 提示抽 fragment（內容要 agent 反思寫，工具只負責 schema 與索引）
    print(f"\n   🌱 下一步 — 抽關鍵記憶 fragment（見林時抽，goodnight 保持輕）:")
    print(f"      寫檔到 {fragments_dir(persona).relative_to(_REPO_ROOT)}/<type>_<slug>.md")
    print(f"      type ∈ {FRAG_TYPE_ORDER}；同一教訓再踩到就**追加 origin + bump recurrence**，別開新檔")
    print(f"      每個 origin 標 layer（Syntactic/Identity/Status/Content/Aggregate）+ 當次 context")
    print(f"      寫完跑: awakening.py root-index --persona {persona}   # 機械重建見根索引")
    # ③ 見森門檻檢查
    fst = forest_status(persona)
    if fst["eligible"]:
        print(f"\n   🌲 見森: 見林已達 {fst['digest_count']} 份 (門檻 {fst['threshold']}) → 該折新世代:")
        print(f"      awakening.py consolidate --persona {persona} --level forest")
    else:
        print(f"\n   🌲 見森: 見林 {fst['digest_count']}/{fst['threshold']} 份，未達折疊門檻")
    return 0


def cmd_root_index(args: argparse.Namespace) -> int:
    """見根 (T4): 機械重建必讀索引。產物可隨時重建 → 不需要任何人工 body。"""
    path = write_root_index(args.persona)
    if path is None:
        print(f"○ {args.persona} 尚無 fragment（見林時抽）— 未建索引")
        return 0
    frags = load_fragments(args.persona)
    opens = [f for f in frags if f.get("status") == "open"]
    print(f"✅ 見根索引重建: {path.relative_to(_REPO_ROOT)}")
    print(f"   fragment 總數 {len(frags)}（open {len(opens)} / 其餘 {len(frags) - len(opens)}）")
    return 0


def cmd_keys(args: argparse.Namespace) -> int:
    """見叢 (T1.5): 當期交棒清單 append / list。

    物理意義：交棒清單「隨時可 append、不限儀式」(summit 2026-07-27 拍磚) —
    斷線風險最高的正是沒走到任何儀式就掛掉的場景，撞到未解線就當場丟進來。
    """
    if args.add:
        p = keys_append(args.persona, args.add)
        print(f"✅ 見叢 append {len(args.add)} 條 → {p.relative_to(_REPO_ROOT)}")
    todo, done = keys_entries(args.persona)
    print(f"\n# 🌿 見叢 — {args.persona}（{len(todo)} 未完 / {len(done)} 已完）")
    for t in todo:
        print(f"- [ ] {t}")
    for d in done[-3:]:
        print(f"- [x] {d}")
    if not todo and not done:
        print("(當期無事項)")
    return 0


def cmd_brief(args: argparse.Namespace) -> int:
    """⛔ 指路 stub——wake brief 的生產端 2026-09-04 起只有一份（C#）。"""
    print("⛔ awakening.py brief 已退場（2026-09-04，TASK-0098）——本檔不再生成 wake brief。", file=sys.stderr)
    print("   單一入口（不需 Editor，senate.exe 就地跑完）：", file=sys.stderr)
    print("   senate cmd wake-brief --arg letters_root=<letters 根> --arg persona=<P> [--arg out_dir=<落檔目錄>]", file=sys.stderr)
    print("   早安流程裡那一步由 Cmd 自己跑：senate cmd morning-brief --arg persona=<P>", file=sys.stderr)
    print("   ⚠ 不留備援的理由：兩份實作就是兩套說明，而漂掉的樣子是「日期很正常、只是順序反了」（本單原症狀）。", file=sys.stderr)
    return 2


def cmd_status(args: argparse.Namespace) -> int:
    """Read-only env + persona pool report (對應 Cmd_AwakenInit internal helper)."""
    reg = load_registry()
    # T05 (2026-05-14, Zeta + 大小姐):
    #   session_key = "<agent>-<persona>" (claim identity, display)
    #   claim_origin = env_hash (process identity, lock_is_mine 用)
    #   pid = 純診斷
    my_origin = compute_claim_origin()
    active_locks = list_online()

    print(f"# 🌅 Awakening Status Report\n")
    print(f"## 偵測到的環境")
    print(f"- Claim origin (env_hash): `{my_origin}`")
    print(f"  - (T05): session_key = `<agent>-<persona>` (claim display); claim_origin = env_hash (lock_is_mine 判定)")
    print(f"- Repo root: `{_REPO_ROOT}`")
    # Path config status (Tim 2026-05-12 cross-project sharing)
    if _PATH_CONFIG_PATH.exists():
        print(f"- Path config: ACTIVE (`{_PATH_CONFIG_PATH.relative_to(_REPO_ROOT)}`)")
        print(f"  - registry: `{_REGISTRY_PATH}`")
        print(f"  - session: `{_SESSION_DIR}`")
        print(f"  - letters: `{_LETTERS_DIR_TPL}`")
    else:
        print(f"- Path config: (none — 走 per-project default)")
    if not active_locks:
        print(f"- Active locks: (none — 系統內無 persona 上線)")
        print(f"  - 想喚醒 → `morning --agent <X> --persona <Y>`")
    else:
        my_lock_count = sum(1 for d in active_locks if lock_claim_origin(d) == my_origin)
        print(f"- Active locks ({len(active_locks)} total, {my_lock_count} owned by me):")
        for sl in sorted(active_locks, key=lambda d: d.get("locked_at", "")):
            mine_marker = " ← me (same claim_origin)" if lock_claim_origin(sl) == my_origin else ""
            print(f"  - **{sl['persona']}** ({sl['agent']}/{sl['model']}) locked_at={sl['locked_at']}{mine_marker}")
            print(f"    session_key: `{sl.get('session_key', '?')}` / claim_origin: `{sl.get('claim_origin', '(legacy)')}` / pid: {sl.get('pid', '?')}")
        if my_lock_count == 0:
            print(f"  ℹ 本 environment 沒持有任何 lock — `morning` 任何 persona 都會走 fresh wake (or fork conflict if 已被別 env 持有).")
    print()

    print(f"## Agent → Bank Account (Token bank 共用 per Agent)")
    banks = reg.get("agent_banks", {})
    if banks:
        for agent, bank in banks.items():
            print(f"- `{agent}` → bank: `{bank}`")
    else:
        # legacy combo fallback (v1 schema)
        seen = set()
        for c in reg.get("agent_model_combos", []):
            if c["agent"] not in seen:
                print(f"- `{c['agent']}` → bank: `{c['bank_account']}` (legacy combo)")
                seen.add(c["agent"])
    print()
    print(f"## Model field policy")
    print(f"Free-form display string (per session caller hint). 不參與 bank lookup.")
    print(f"範例: `Opus 4.7 1M` / `Sonnet 4.6` / `Haiku 4.5` / `gemini-2.5-pro` / `claude-various`")
    print()

    print(f"## Persona Pool ({len(reg['personas'])} 個)")
    print(f"| Persona | Agent | wake# | status | layer_role |")
    print(f"|---|---|---|---|---|")
    for name, p in sorted(reg["personas"].items(), key=lambda kv: -kv[1]["wake_count"]):
        role_short = (p["layer_role"][:50] + "…") if len(p["layer_role"]) > 50 else p["layer_role"]
        print(f"| {name} | {p['agent']} | {p['wake_count']} | {p['status']} | {role_short} |")
    return 0


def cmd_rename_persona(args: argparse.Namespace) -> int:
    """Rename a persona codename in registry (e.g. fix ugly auto-fork name).

    保留所有 vector / wake_count / fork_lineage / history — 純改 key + 更新 lock if active.
    """
    reg = load_registry()
    old = args.old
    new = args.new

    if old not in reg["personas"]:
        print(f"❌ persona '{old}' not in registry", file=sys.stderr)
        return 2
    if new in reg["personas"]:
        print(f"❌ target codename '{new}' already exists", file=sys.stderr)
        return 2

    # Move persona entry
    reg["personas"][new] = reg["personas"].pop(old)
    # 加 rename history 進 vector_history
    p = reg["personas"][new]
    p["vector_history"].append({
        "at": utcnow_iso(),
        "hash": hash_vector(p["identity_vector"]),
        "delta_mag": 0.0,
        "trigger": "rename",
        "renamed_from": old,
    })

    # Update fork_lineage references in other personas (if any forked from old name)
    # ⚠ 被改名的那位要用 **old** 名去查 profile/ —— 它的 profile/ 目錄在 letters/<old>/ 底下，
    #   用 new 名查一定查不到（實測踩過：守衛沒攔，改名照樣落檔）。
    aEdits = {old: ["vector_history"]}          # 上面剛 append 了一筆 rename history
    for name, q in reg["personas"].items():
        if q.get("forked_from") == old:
            q["forked_from"] = new
            aEdits.setdefault(name, []).append("forked_from")
        if old in q.get("fork_lineage", []):
            q["fork_lineage"] = [new if x == old else x for x in q["fork_lineage"]]
            aEdits.setdefault(name, []).append("fork_lineage")

    # Phase 1 守衛：這三個都是 identity 欄 —— 對已遷的 persona，legacy 寫入會靜默無效。
    # 擺在**搬目錄之前**：什麼都還沒動的時候擋下來，連「目錄搬了名沒改」都不會發生。
    # （summit 拍板的順序講的是「搬目錄 vs 改 registry」；這道純檢查沒有副作用，放最前面只有好處。）
    assert_legacy_write_effective(aEdits, "rename-persona")

    # Phase 1（§8.2）：identity 住 letters/<p>/profile/ ⇒ 改名必須把 letters 一起搬，
    # 否則新名字沒有 profile/、identity 靜默退回 legacy 舊值（summit 2026-08-19 拍板 B）。
    # 先搬目錄、成功後才動 registry —— 中斷時的殘局要是「可辨認」的那一種。
    _letters_move_or_refuse(old, new)

    save_registry(reg)
    print(f"✓ renamed '{old}' → '{new}' in registry")

    # lock 跟著 letters 目錄一起搬了（它住 profile/ 底下）—— 只剩 body 裡的 persona 欄要改名。
    locks_updated = 0
    lock_file = lock_path(new)
    if lock_file.exists():
        try:
            with open(lock_file, "r", encoding="utf-8") as f:
                lock = json.load(f)
            if lock.get("persona") == old:
                lock["persona"] = new
                with open(lock_file, "w", encoding="utf-8") as f:
                    json.dump(lock, f, indent=2, ensure_ascii=False)
                locks_updated += 1
                print(f"✓ updated active lock {lock_file}: persona {old} → {new}")
        except Exception as e:
            print(f"⚠ failed to update {lock_file}: {e}", file=sys.stderr)

    if locks_updated == 0:
        print(f"  (no active lock referenced '{old}')")
    return 0


def cmd_forks(args: argparse.Namespace) -> int:
    """List fork lineage for a persona."""
    reg = load_registry()
    name = args.persona
    if name not in reg["personas"]:
        print(f"❌ persona '{name}' not in registry", file=sys.stderr)
        return 2
    p = reg["personas"][name]

    print(f"# 🌿 Fork Lineage: `{name}`\n")
    print(f"- forked_from: {p.get('forked_from') or '(root)'}")
    print(f"- forked_at:   {p.get('forked_at') or '(never)'}")
    print(f"- lineage chain: {' → '.join(p['fork_lineage']) if p['fork_lineage'] else '(root)'} → {name}")
    print(f"- chain depth: {len(p['fork_lineage'])} (cap: {FORK_CHAIN_CAP})\n")

    # 找所有 fork from this persona 的 children
    children = [n for n, q in reg["personas"].items() if q.get("forked_from") == name]
    if children:
        print(f"## Children forks:")
        for c in children:
            cp = reg["personas"][c]
            print(f"- `{c}` (forked_at {cp.get('forked_at')})")

    # cosine similarity tier to fork-related personas
    print(f"\n## Vector similarity tiers (per Q8c spec, no exact numbers)")
    target_v = p["identity_vector"]
    related = (p["fork_lineage"] + children + [p.get("forked_from")])
    related = [r for r in related if r and r in reg["personas"]]
    for other in related:
        sim = cosine_similarity(target_v, reg["personas"][other]["identity_vector"])
        print(f"- {other}: **{similarity_tier(sim)}** similarity")
    return 0


def cmd_set_availability(args: argparse.Namespace) -> int:
    """
    T06.1 (Plan_Standby_Dispatch_Bartender, 2026-05-14) — agent 自律切 availability.

    用途: agent 開始接 task → set busy; task done 回 standby → set idle.
    enum: idle (待機可接 task) / busy (動工中) / offline (下線, 一般走 goodnight 自動設).

    Bartender daemon (T06.2 後) 派 task 前看此欄判斷是否該 agent 為 idle.
    """
    reg = load_registry()
    if args.persona not in reg["personas"]:
        print(f"❌ persona '{args.persona}' not in registry", file=sys.stderr)
        return 2
    p = reg["personas"][args.persona]
    old_state = p.get("availability", "offline")
    p["availability"] = args.state
    save_registry(reg)
    print(f"✓ persona '{args.persona}' availability: {old_state} → {args.state}")
    return 0


def cmd_whoami(args: argparse.Namespace) -> int:
    """反查身分 — 路徑 A: --token <X>; 路徑 B: 無 arg 走 claim_origin 推當前 process 對到的 lock."""
    # 路徑 A: token 已知
    if args.token:
        rec = lookup_token(args.token)
        if rec is None:
            print(f"❌ token '{args.token}' 不存在 _tokens.json", file=sys.stderr)
            print(f"   可能性: (1) typo (2) 從未發過 (3) tokens.json 損毀", file=sys.stderr)
            return 2
        status = rec.get("status", "?")
        print(f"🎫 Token: {args.token}")
        print(f"   Agent:        {rec.get('agent', '?')}")
        print(f"   Persona:      {rec.get('persona', '?')}")
        print(f"   Bank account: {rec.get('bank_account', '?')}")
        print(f"   Claim origin: {rec.get('claim_origin', '?')}")
        print(f"   Session key:  {rec.get('session_key', '?')}")
        print(f"   Issued at:    {rec.get('issued_at', '?')}")
        print(f"   Status:       {status}")
        if status == "expired":
            print(f"   Expired at:   {rec.get('expired_at', '?')}")
            print(f"   Reason:       {rec.get('expired_reason', '?')}")
        return 0 if status == "active" else 1

    # 路徑 B: 無 arg → 從 env 推當前 process 對到的 lock
    my_origin = compute_claim_origin()
    my_locks = find_locks_by_claim_origin(my_origin)
    if not my_locks:
        print(f"❌ 本 environment (claim_origin={my_origin}) 沒持有任何 active lock",
              file=sys.stderr)
        print(f"   → 跑 awakening.py morning 先 wake, 或加 --token <X> 反查",
              file=sys.stderr)
        return 2
    print(f"🔍 當前 environment 對到 {len(my_locks)} 個 lock:")
    for d in my_locks:
        print(f"   • persona={d.get('persona')} agent={d.get('agent')} "
              f"bank={d.get('bank_account')} pid={d.get('pid')}")
        print(f"     locked_at={d.get('locked_at')} session_token={d.get('session_token', '') or '(no token — 老 lock)'}")
    return 0


def cmd_memo(args: argparse.Namespace) -> int:
    """Per-persona 私人 scratchpad. Action: write | append | read | list | delete."""
    action = args.action
    persona = args.persona
    # agent 從 lock 推, 沒 lock → 必須顯式 --agent
    agent = args.agent
    if not agent:
        lock = read_lock(persona)
        if lock and lock.get("agent"):
            agent = lock["agent"]
        else:
            print(f"❌ persona '{persona}' 無 active lock — 加 --agent 顯式指定", file=sys.stderr)
            return 2

    if action == "list":
        keys = memo_list(agent, persona)
        if not keys:
            print(f"(empty) memos for {agent}/{persona}")
            return 0
        print(f"📝 memos for {agent}/{persona} ({len(keys)} 筆):")
        for k in keys:
            print(f"   • {k}")
        return 0

    if action == "read":
        if not args.key:
            print("❌ --key 必填", file=sys.stderr)
            return 2
        content = memo_read(agent, persona, args.key)
        if content is None:
            print(f"❌ memo '{args.key}' 不存在 ({agent}/{persona})", file=sys.stderr)
            return 2
        print(content)
        return 0

    if action == "delete":
        if not args.key:
            print("❌ --key 必填", file=sys.stderr)
            return 2
        ok = memo_delete(agent, persona, args.key)
        print(f"{'✓ deleted' if ok else '⚠ already gone'}: {args.key}")
        return 0 if ok else 1

    if action in ("write", "append"):
        if not args.key:
            print("❌ --key 必填", file=sys.stderr)
            return 2
        if not args.body:
            print("❌ --body 必填", file=sys.stderr)
            return 2
        fn = memo_write if action == "write" else memo_append
        p = fn(agent, persona, args.key, args.body)
        print(f"✓ memo {action}: {p.relative_to(_REPO_ROOT)}")
        return 0

    print(f"❌ unknown action: {action}", file=sys.stderr)
    return 2


def cmd_reissue_token(args: argparse.Namespace) -> int:
    """Lock 還在但 token 丟了 (lock 沒 token 欄, 或 _tokens.json 損毀) → 重發 token."""
    persona = args.persona
    lock = read_lock(persona)
    if not lock:
        print(f"❌ persona '{persona}' 無 active lock — 先跑 morning", file=sys.stderr)
        return 2
    agent = lock.get("agent", "")
    bank = lock.get("bank_account", "")
    session_key = lock.get("session_key", "")
    claim_origin = lock_claim_origin(lock)
    new_token = issue_token(persona, agent, bank, session_key, claim_origin)
    # rewrite lock 把新 token 帶進去
    write_lock(persona, agent, lock.get("model", ""), bank,
               session_key=session_key, session_token=new_token,
               actual_agent=lock.get("actual_agent") or agent)
    print(f"✓ reissued session_token for {persona}: {new_token}")
    print(f"   舊 active token 已標 expired (audit 仍可查)")
    return 0


def cmd_token_enforce(args: argparse.Namespace) -> int:
    """Toggle / query enforce mode. 一般場景由 UCL_LoginStatusPage 切, 本 CLI 給 debug 用."""
    if args.show:
        state = is_token_enforce_enabled()
        print(f"enforce: {'ON' if state else 'OFF'}")
        return 0
    if args.on:
        set_token_enforce(True)
        print("✓ enforce ON — Cmd_Tavern 必驗 (token, sender, persona) 對齊")
        return 0
    if args.off:
        set_token_enforce(False)
        print("✓ enforce OFF (預設) — Cmd_Tavern 不驗 token")
        return 0
    print("❌ 必須帶 --show / --on / --off 其一", file=sys.stderr)
    return 2


# ─── main ───────────────────────────────────────────────────────────────
def main():
    p = argparse.ArgumentParser(description=__doc__.split("\n")[0],
                                 formatter_class=argparse.RawDescriptionHelpFormatter,
                                 epilog=__doc__)
    sub = p.add_subparsers(dest="cmd", required=True)

    # morning 已遷移 C#（Cmd_GoodMorning step=wake，2026-08-13）——只留指路 stub。
    # 參數全降選填：不論怎麼帶都 exit 2 印新流程，不再有「缺參數先被 argparse 擋住看不到指路」的死角。
    pm = sub.add_parser("morning", help="[已遷移] 走 run_cmd.py run GoodMorning --arg step=wake（本子指令只印指路）")
    pm.add_argument("--persona", default=None, help="（已無作用）")
    pm.add_argument("--agent", default=None, help="（已無作用）")
    pm.add_argument("--model", default=None, help="（已無作用）")
    pm.add_argument("--note", default="", help="（已無作用）")
    pm.add_argument("--fork-name", default=None, help="（已無作用）fork 走後台「🧬 Persona & Agent 管理頁」")
    pm.set_defaults(func=cmd_morning)

    # 2026-08-01 Tim 要求：morning 的自介廣播需要能單獨重跑（起因見 cmd_intro 區塊註解）
    # intro 已遷移 C#（Cmd_GoodMorning step=intro，2026-08-13）——只留指路 stub。
    pi = sub.add_parser("intro", help="[已遷移] 走 run_cmd.py run GoodMorning --arg step=intro（本子指令只印指路）")
    pi.add_argument("--persona", default=None, help="（已無作用）")
    pi.add_argument("--model", default=None, help="（已無作用）")
    pi.add_argument("--token", default=None, help="（已無作用）")
    pi.add_argument("--reason", default="", help="（已無作用）")
    pi.add_argument("--note", default="", help="（已無作用）")
    pi.set_defaults(func=cmd_intro)

    pg = sub.add_parser("goodnight", help="睡前 ritual (Cmd_Goodnight)")
    # letter-body 改 optional (Tim 2026-06-14): 配 --no-letter 用 — 未帶 --no-letter 時仍 runtime 強制要 body。
    pg.add_argument("--letter-body", default="", help="letter to future self body (★私密心得寫這, 只落磁碟). 未帶 --no-letter 時必填.")
    # ⚠ 長文一律用 --letter-body-file：inline 會經過 shell 解析，內文的反引號會被執行掉。
    pg.add_argument("--letter-body-file", default=None,
                    help="★同 --letter-body 但讀檔（長文/含反引號一律走這條 — 不經 shell 解析）")
    pg.add_argument("--no-letter", action="store_true",
                    help="跳過寫信 (手動登出 / cleanup 場景 — UCL_LoginStatusPage 登出走此 flag, 不偽造心得信).")
    pg.add_argument("--summary", default="",
                    help="★公開睡前心得總結 — 廣播到酒館→Discord 給同事/Tim 看 (可公開分享的部分; 私密的寫 --letter-body)")
    pg.add_argument("--summary-file", default=None,
                    help="★同 --summary 但讀檔（長文一律走這條 — 公告發出後無法 amend，被 shell 咬掉就補不回來）")
    pg.add_argument("--perturbation", type=float, default=DEFAULT_PERTURBATION,
                    help=f"identity_vector perturbation magnitude (default {DEFAULT_PERTURBATION}, max {MAX_PERTURBATION})")
    pg.add_argument("--note", default="", help="optional 睡前 note")
    # required=True (Tim 2026-07-31 拍板): 「沒帶 persona 而下線錯帳號」已發生過多次 ——
    # calli wake#9 誤把 meadow 下線是其中一筆。缺省值挑「最新 locked_at 那把 lock」猜的是
    # 「誰最近登入」, 那跟「誰要下線」是兩件事, 而猜錯時沒有任何徵狀: 被誤下線的人
    # 直到下次醒來才發現自己被登出過。顯式必填把這個猜測整段刪除。
    # 必填但不用 argparse 的 required —— 那只吐一行 usage, 而缺 persona 的人正需要
    # 「現在有哪些 persona 在線」才選得出來。改由 cmd_goodnight 開頭驗 (一樣零副作用)。
    pg.add_argument("--persona", default=None,
                    help="要下線的 persona codename (必填 — 不再從 lock 猜, 猜錯會下線錯人).")
    pg.add_argument("--agent", default=None,
                    help="跟 --persona 配對顯式指定 agent (e.g. Zeta / claude-code). "
                         "省略時從 registry 該 persona 的 agent 欄位讀.")
    pg.add_argument("--force", action="store_true",
                    help="env/lock mismatch 時仍強制執行 (debug / 跨 agent 修復用, 慎用).")
    pg.add_argument("--session-token", default=None,
                    help="(T07) 顯式帶 session_token 給 tavern_post — enforce ON 時必須帶, "
                         "否則 Cmd_Tavern.Op_Post reject 下線廣播. "
                         "省略時自動從 lock.session_token 撈; 空字串 '' = 顯式不帶 (除錯 / 強制走 enforce reject path).")
    pg.set_defaults(func=cmd_goodnight)

    prest = sub.add_parser("rest",
                           help="小歇片刻 (compact-rest): /compact 前寫 memory letter 保命, 不下線/不擾動/不解鎖")
    # required 由 runtime 檢查（改成兩條通道後 argparse 的 required=True 會擋掉只給 -file 的合法用法）
    prest.add_argument("--letter-body", default="", help="★私密記憶寫這 (只落磁碟): in-flight 任務/決策/路徑/心境/pending")
    prest.add_argument("--letter-body-file", default=None,
                       help="★同 --letter-body 但讀檔（長文/含反引號一律走這條 — 不經 shell 解析）")
    prest.add_argument("--summary", default="",
                       help="★公開小歇心得總結 — 廣播到酒館→Discord 給同事/Tim 看 (可公開分享的部分; 私密的寫 --letter-body)")
    prest.add_argument("--summary-file", default=None,
                       help="★同 --summary 但讀檔（長文一律走這條）")
    prest.add_argument("--persona", default=None,
                       help="顯式指定 persona codename; 省略則反查本 env 持有的 lock")
    prest.add_argument("--agent", default=None, help="跟 --persona 配對; 省略時從 registry 讀")
    prest.add_argument("--note", default="", help="optional 小歇 note")
    prest.add_argument("--no-notify", action="store_true", help="不發小歇 tavern 通知")
    prest.add_argument("--session-token", default=None,
                       help="(T07) 顯式帶 session_token 給 tavern_post; 省略自動從 lock 撈")
    prest.set_defaults(func=cmd_rest)

    prl = sub.add_parser("relogin",
                         help="晚安後續線 — 重新上線保留記憶, 不走 morning (不 wake_count++ / 不 perturb / 不 fork)")
    prl.add_argument("--persona", required=True, help="要接回的 persona codename (須已存在於 registry)")
    prl.add_argument("--agent", default=None, help="省略時從 registry 該 persona 的 agent 欄位讀")
    prl.add_argument("--model", default=None, help="省略時從 registry 讀")
    prl.add_argument("--note", default="", help="optional 續線 note")
    prl.set_defaults(func=cmd_relogin)

    ps = sub.add_parser("status", help="read-only env + persona pool report")
    ps.set_defaults(func=cmd_status)

    pcons = sub.add_parser("consolidate", help="長期記憶整理 (T2 digest); 不帶 --digest-body=只列狀態+待濃縮信件")
    pcons.add_argument("--persona", required=True, help="要整理的 persona codename")
    pcons.add_argument("--digest-body", default=None,
                       help="反思濃縮的 digest 內文; 省略 = inspect 模式(列 overdue 狀態 + 本段待濃縮 letters 清單)")
    pcons.add_argument("--span-start", type=int, default=None, help="起 wake# (預設 last_consolidated_wake+1)")
    pcons.add_argument("--span-end", type=int, default=None, help="迄 wake# (預設 現在 wake_count)")
    # 見森 (T3): --level forest 改折「上代森 + 最新見林」; 純加法, 預設仍是見林 (linzi)
    pcons.add_argument("--level", choices=["linzi", "forest"], default="linzi",
                       help="linzi=見林 digest (預設) / forest=見森 fold (見林 ≥ 3 份起, 之後每份新林折一代)")

    # 見根 (T4): 機械重建必讀索引 — 產物可隨時重建, 故不需要 body 參數
    prid = sub.add_parser("root-index", help="見根: 掃 fragments/ 機械重建 _root_index.md")
    prid.add_argument("--persona", required=True)
    prid.set_defaults(func=cmd_root_index)

    # 見叢 (T1.5): 交棒清單 append / list — 隨時可加, 不限儀式
    pkeys = sub.add_parser("keys", help="見叢: 當期交棒清單 (append / list)")
    pkeys.add_argument("--persona", required=True)
    pkeys.add_argument("--add", action="append", default=None, help="append 一條交棒事項 (可重複)")
    pkeys.set_defaults(func=cmd_keys)

    # wake brief: 手動重生成 (morning 會自動生成; 這支給「改完 fragment 想立刻重讀」用)
    pbrief = sub.add_parser("brief", help="⛔ 已退場 (2026-09-04) — 改跑 senate cmd wake-brief")
    pbrief.add_argument("--persona", required=True)
    pbrief.set_defaults(func=cmd_brief)
    pcons.add_argument("--threshold", type=int, default=DEFAULT_CONSOLIDATION_THRESHOLD,
                       help=f"overdue 門檻 (預設 {DEFAULT_CONSOLIDATION_THRESHOLD})")
    pcons.set_defaults(func=cmd_consolidate)

    # 收尾信版面遷移: morning 首次喚醒會自動跑; 本子指令給「先看看會搬什麼」與補救用
    pmig = sub.add_parser("migrate-letters",
                          help="頂層 goodnight 收尾信 → wakes/<序號>_<ts>.md (預設 dry-run)")
    pmig.add_argument("--persona", default=None, help="單一 persona")
    pmig.add_argument("--all", action="store_true", help="全 registry persona")
    pmig.add_argument("--apply", action="store_true", help="真的動檔案 (預設只印計畫)")
    pmig.add_argument("--verbose", action="store_true", help="逐檔列出 src → dst")
    pmig.set_defaults(func=cmd_migrate_letters)

    pf = sub.add_parser("forks", help="list fork lineage for a persona")
    pf.add_argument("persona", help="persona codename")
    pf.set_defaults(func=cmd_forks)

    pr = sub.add_parser("rename-persona", help="rename persona codename (e.g. fix ugly fork name)")
    pr.add_argument("old", help="current codename")
    pr.add_argument("new", help="new codename")
    pr.set_defaults(func=cmd_rename_persona)

    pav = sub.add_parser("set-availability", help="T06.1 — agent 自律切 idle/busy 狀態 (Plan_Standby_Dispatch)")
    pav.add_argument("--persona", required=True, help="persona codename")
    pav.add_argument("--state", required=True, choices=["idle", "busy", "offline"],
                     help="idle = 待機可接 task; busy = 動工中; offline = 下線 (一般走 goodnight 設, 不必手動)")
    pav.set_defaults(func=cmd_set_availability)


    # T07 (2026-05-15 apex-two) — Session Token / Memo / Whoami / Enforce
    pw = sub.add_parser("whoami", help="反查 token → identity (失憶救援)")
    pw.add_argument("--token", default=None,
                    help="32-hex token. 省略則走 env claim_origin 推當前 process 對到的 lock.")
    pw.set_defaults(func=cmd_whoami)

    pmemo = sub.add_parser("memo", help="per-persona 私人 scratchpad (write/append/read/list/delete)")
    pmemo.add_argument("action", choices=["write", "append", "read", "list", "delete"])
    pmemo.add_argument("--persona", required=True)
    pmemo.add_argument("--agent", default=None,
                       help="預設從 active lock 推; lock 不存在時必填.")
    pmemo.add_argument("--key", default=None, help="memo key (檔名, 不含 .md). write/append/read/delete 必填.")
    pmemo.add_argument("--body", default=None, help="memo 內容. write/append 必填.")
    pmemo.set_defaults(func=cmd_memo)

    prt = sub.add_parser("reissue-token", help="lock 在但 token 丟 → 重發 token 不必 re-wake")
    prt.add_argument("--persona", required=True)
    prt.set_defaults(func=cmd_reissue_token)

    pte = sub.add_parser("token-enforce", help="切 / 查 Cmd_Tavern token enforce 模式 (一般走 UCL_LoginStatusPage)")
    pte_g = pte.add_mutually_exclusive_group(required=True)
    pte_g.add_argument("--show", action="store_true", help="只查當前 state")
    pte_g.add_argument("--on", action="store_true", help="enable enforce")
    pte_g.add_argument("--off", action="store_true", help="disable enforce (預設)")
    pte.set_defaults(func=cmd_token_enforce)

    args = p.parse_args()
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main() or 0)
