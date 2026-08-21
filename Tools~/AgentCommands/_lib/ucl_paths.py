#!/usr/bin/env python3
# 區塊職責：UCL_Core Python 端「跨專案路徑解析」的唯一 canonical 來源 (T-PATH-RESOLVE / T01)。
# 物理意義：
#   UCL_Core 是 git submodule，不同 host 專案把它掛在不同深度
#   （EOV：CardGame/Assets/UCL/UCL_Core；扁平專案如 TEVI：直接掛 repo 根）。
#   工具腳本要解析三個「其實不同」的根：
#     (1) host repo root  — 含 .git 的那層，AgentCommands/ 狀態夾掛其下（RPC 錨點）
#     (2) UCL_Core 本體目錄 — 深度不定，但本檔就住在其內，用 __file__ 反推即可、免 walk
#     (3) AgentCommands 資料根 — 預設 repo_root/AgentCommands，可經 pointer 檔搬遷
#   歷史上這三者由 ≥5 份漂移的 find_repo_root 各自解析（.git-walk / AgentCommands-walk /
#   baton-walk / git rev-parse〔吃 cwd 有 bug〕 / EOV 專屬的 CardGame 錨），漂移正是
#   2026-06-16 cwd 路徑詐欺 bug 家族的病灶。本檔把解析收斂成一處。
# 數值影響：路徑解析純唯讀檔案系統探測（os.path/Path 判斷），不寫任何 asset / token / 狀態檔。
#          **唯一例外是 `ensure_letters_cmd_dir()`**：建 `cmd/` 目錄並補一份 `.gitignore`
#          （對側 = C# `UCL_LettersPath.EnsureCmdDir`）。那份 ignore 屬於版面語意，
#          而版面的擁有者是本檔 —— 分散到各寫入端就是下一次靜默漂移。
#
# 契約對齊：本檔的 repo_root() 與 C# 端 UCL.Core.EditorLib.UCL_RepoPath.RepoRoot 等價 ——
#   兩者都「從固定位置往上 walk，找第一個含 .git〖資料夾〗的 ancestor（submodule 的 .git
#   是 gitlink〖檔案〗redirect，必須跳過）」。C# 從 Application.dataPath 起 walk；Python 從
#   本檔 __file__ 起 walk（同為與 cwd 解耦的固定錨）。兩端對齊，pending.trigger 不會落單。
#
# 主管裁決 (summit, 2026-07-04)：
#   - 錨鏡像 UCL_RepoPath 的「.git 資料夾才停、gitlink 檔跳過」契約，不發明第三套 heuristic。
#   - CLAUDE_PROJECT_DIR 保留為 tier-1 顯式 override。
#   - data root 搬遷走 .agentcommands_root.local pointer 檔。

from __future__ import annotations

import json
import os
from functools import lru_cache
from pathlib import Path

# ─────────────────────────────────────────────────────────────────────────
# 區塊職責：本檔自身位置錨 + UCL_Core 自我定位。
# 物理意義：canonical 檔在 <UCL_Core>/Tools~/AgentCommands/_lib/ucl_paths.py。
#          但本檔會被「位元組原樣」同步鏡像到 host 專案的 <repo>/AgentCommands/_lib/ucl_paths.py
#          (T02, install_skills.py 模式)。在鏡像位置 UCL_Core 不是本檔的 ancestor（它在
#          CardGame/Assets/UCL/… 這條 sibling 子樹、且掛載深度跨專案不定），故不能用固定
#          parents[N] 反推 —— 那在鏡像位置會指到 repo 上一層，回垃圾（外觀 OK ≠ 真的 OK）。
# 數值影響：改採「往上找名為 UCL_Core 的 ancestor」自我定位 —— depth-tolerant（不綁死層數），
#          canonical 位置一定找得到；鏡像位置找不到 → 回 None，由 ucl_core_dir() 誠實 raise。
#          repo_root() / data_root() 走 .git walk，兩個位置都正確，不受本區塊影響。
# ─────────────────────────────────────────────────────────────────────────
_THIS_FILE = Path(__file__).resolve()          # 本檔絕對路徑（已解 symlink）


def _find_ucl_core_dir(start: Path) -> Path | None:
    # 從 start 起（含自身）往上找第一個「目錄名為 UCL_Core」的 ancestor。
    # UCL_Core 是 submodule 的固定名稱（非 host 專案特徵），故此自我定位跨專案安全。
    for anc in (start, *start.parents):        # 含起點本身，再逐層往上
        if anc.name == "UCL_Core":             # 命中名為 UCL_Core 的那層
            return anc
    return None                                 # 鏡像位置 UCL_Core 非 ancestor → 找不到


_UCL_CORE_DIR = _find_ucl_core_dir(_THIS_FILE)  # canonical: 找得到；鏡像: None


# ─────────────────────────────────────────────────────────────────────────
# 區塊職責：從某起點往上 walk，找第一個含 .git〖資料夾〗的 ancestor。
# 物理意義：與 C# UCL_RepoPath.ResolveRepoRoot / Python run_cmd._find_git_root_by_walk 等價。
#          只接受 .git 為「資料夾」——submodule 的 .git 是 gitlink 檔（gitdir: redirect），
#          遇到要跳過繼續往上，才能找到「真實」host repo 根而非 submodule 根。
# 數值影響：至多 walk 幾層 + 每層一次 is_dir 判斷；找不到回 None（交由呼叫端決定 fallback）。
# ─────────────────────────────────────────────────────────────────────────
def _find_git_root_by_walk(start: Path) -> Path | None:
    p = start.resolve()                        # 起點正規化為絕對路徑
    while p != p.parent:                        # 走到檔案系統根（p == p.parent）為止
        if (p / ".git").is_dir():              # .git 為「資料夾」才算真實 repo 根（跳過 gitlink 檔）
            return p                            # 命中 → 回這層
        p = p.parent                            # 否則再往上一層
    return None                                 # 一路到頂都沒 .git 資料夾 → 交還 None


# ─────────────────────────────────────────────────────────────────────────
# API 1 — repo_root()
# 區塊職責：解析 host repo root（含 .git 的那層），AgentCommands/ RPC 錨點掛其下。
# 物理意義：解析優先序（tier）：
#   tier-1  環境變數 CLAUDE_PROJECT_DIR（Claude Code hook 注入，最權威的顯式 override）
#   tier-2  從本檔 __file__ 往上找 .git 資料夾（結構錨，與 cwd 解耦，最穩定）
#   tier-3  從 cwd 往上找 .git 資料夾（次要；caller 從別處 cwd 跑時的補救）
#   fallback 退回 UCL_Core 根（極端無 .git 環境；至少不炸，不亂猜到別的磁碟位置）
# 數值影響：lru_cache 後同 process 內只算一次，之後 O(1)。
# ─────────────────────────────────────────────────────────────────────────
# ─────────────────────────────────────────────────────────────────────────
# 路徑快照（pointer 檔）—— Editor 端量到的值，兩端唯一的共同來源
# 區塊職責：讀 <UCL_Core>/.agentcommands_root.local，取得 C# 端解析出的 repo_root / data_root。
# 物理意義：C# **只寫不讀**（每次 domain reload 重算後覆寫）；Python **只讀不寫**（外加自癒刪檔）。
#   ⇒ 「寫錯會被固化」這個風險不存在：下一次 recompile 就會被正確值覆蓋。
# 🩸 為什麼檔案放在 <UCL_Core>/ 而不是 repo root：
#   放 repo root 的話，**要讀到它必須先知道 repo root** —— 於是它只能同步
#   「data_root ≠ repo_root/AgentCommands」的情形，碰不到「兩端 repo_root 推導不一致」那格，
#   而後者才是會咬人的（C# 與 Python 的 tier 順序至今不同）。
#   UCL_Core 兩端都能在**不知道 repo root** 的情況下定位（C# 從 Application.dataPath 搜資料夾名、
#   Python 從 __file__ 往上找目錄名）⇒ 放這裡才真的同步得到 repo_root。
# 數值影響：純讀 + 存在性驗證；驗不過就刪檔（下次 Editor reload 會重寫）。
# ⚠ tier 順序變更（覆寫 summit 2026-07-04 的「CLAUDE_PROJECT_DIR 為 tier-1」裁決，
#   Tim 2026-08-17 重新拍板）：pointer 是**唯一被實際量到**的值（Editor 知道自己在哪），
#   其餘都是推導或注入。所以 pointer 排在最前面。這條是明改，不是忘了舊裁決。
# ─────────────────────────────────────────────────────────────────────────
POINTER_FILENAME = ".agentcommands_root.local"
_pointer_cache: dict | None = None


def pointer_file() -> Path | None:
    return (_UCL_CORE_DIR / POINTER_FILENAME) if _UCL_CORE_DIR else None


def _parse_pointer(text: str) -> dict:
    """吃兩種格式：schema=2 的 key=value 多行，以及舊版「單行絕對路徑」。"""
    out: dict = {}
    lines = [ln.strip() for ln in text.splitlines() if ln.strip()]
    if not lines:
        return out
    if "=" not in lines[0]:
        out["data_root"] = lines[0]          # 舊格式：整檔就是一條 data_root
        out["_legacy"] = True
        return out
    for ln in lines:
        if "=" in ln:
            k, _, v = ln.partition("=")
            out[k.strip()] = v.strip()
    return out


def read_pointer() -> dict:
    """回 {'repo_root': Path, 'data_root': Path}（缺的 key 就不放）。

    驗存在性 —— 任一路徑不存在即視為過期：**刪檔**並回 {}。
    自癒的代價只是「下次 Editor reload 之前 Python 自己推導」，
    而留著一個指向不存在目錄的快照，會讓每一支工具都安靜地讀錯地方。
    """
    global _pointer_cache
    if _pointer_cache is not None:
        return _pointer_cache
    _pointer_cache = {}
    p = pointer_file()
    if p is None or not p.is_file():
        return _pointer_cache
    try:
        kv = _parse_pointer(p.read_text(encoding="utf-8"))
    except Exception:
        return _pointer_cache
    got, stale = {}, False
    for key in ("repo_root", "data_root"):
        raw = (kv.get(key) or "").strip()
        if not raw:
            continue
        cand = Path(raw)
        if not cand.is_absolute():
            stale = True
            break
        if not cand.exists():
            stale = True
            break
        got[key] = cand.resolve()
    if stale or not got:
        try:
            p.unlink()          # 自癒：過期快照就地移除，下次 Editor reload 重寫
        except Exception:
            pass
        return _pointer_cache
    _pointer_cache = got
    return _pointer_cache


@lru_cache(maxsize=1)
def repo_root() -> Path:
    # tier-0：Editor 寫下的路徑快照（唯一被量到的值，優先於任何推導）
    snap = read_pointer().get("repo_root")
    if snap is not None:
        return snap

    # tier-1：顯式 env override（須通過 is_dir 驗證才採用，避免指到不存在的路徑靜默誤用）
    env_root = os.environ.get("CLAUDE_PROJECT_DIR")            # 讀 hook 注入的專案根
    if env_root and Path(env_root).is_dir():                   # 有值且確實是資料夾才採信
        return Path(env_root).resolve()                        # 正規化後回傳

    # tier-2：從本檔位置往上 walk（結構錨，最穩，不受呼叫端 cwd 影響）
    walked = _find_git_root_by_walk(_THIS_FILE)                # 從 ucl_paths.py 起 walk
    if walked:                                                 # 命中真實 .git 資料夾
        return walked

    # tier-3：UCL_Core 的 submodule gitlink 精確上溯 —— 對齊 C# UCL_RepoPath 的同名 tier。
    #   `gitdir: ../../../.git/modules/<path>` 那串 `../` 是 git 自己寫下的**精確層數**，
    #   不是啟發式、也不吃 cwd；數幾個就上溯幾層。
    if _UCL_CORE_DIR is not None:
        up = _superproject_from_gitlink(_UCL_CORE_DIR)
        if up is not None:
            return up

    # tier-4：AgentCommands 直探（它依定義直掛 repo 根）—— 對齊 C# 的同名 tier。
    if _UCL_CORE_DIR is not None:
        for cand in _UCL_CORE_DIR.parents:
            if (cand / "AgentCommands").is_dir():
                return cand

    # 🩸 2026-08-17：拿掉兩件東西，兩件都是「看起來合理的錯答案」的來源
    #   ① **cwd 往上 walk**（原 tier-3）—— 本檔檔頭自己點名它是「2026-06-16 cwd 路徑詐欺
    #      bug 家族的病灶」，而它一直還留在這裡當一個 tier。實例：cwd 在 D:/Unity/persona/kiara
    #      （獨立 repo）時跑工具，會把登入態與信件寫進 kiara/AgentCommands。
    #   ② **fallback 回 `_UCL_CORE_DIR`** —— 那是一個格式正確、看起來完全正常、
    #      而且**一定不對**的 repo 根（UCL_Core 是 submodule，不是 host repo）。
    #      C# 端今天已把同族的 `dataPath/../..` 換成 throw，本檔對齊。
    #   ⇒ 猜一個看起來合理的根，會讓狀態檔安靜寫到別的地方；raise 才停得住。
    raise RuntimeError(
        "解析不到 host repo 根：ucl_paths.py 之上沒有 .git 資料夾、UCL_Core 不是 submodule、"
        "也找不到 AgentCommands 資料夾。\n"
        "  處置：設 CLAUDE_PROJECT_DIR 指向專案根，或確認專案結構。\n"
        "  ⚠ 刻意不 fallback 到 cwd／UCL_Core 根 —— 猜一個看起來合理的根，"
        "會讓狀態檔安靜地寫到別的地方。")


def _superproject_from_gitlink(sub_dir: Path) -> Path | None:
    """讀 submodule 的 `.git` gitlink，數 `../` 精確上溯到 superproject 根。

    失敗處置：`.git` 是資料夾（獨立 repo）／內容是絕對路徑（worktree）／格式不符 → 回 None，
    交由呼叫端走下一 tier。**不猜。**
    ⚠ 與 C# `UCL_RepoPath.ResolveSuperprojectFromGitlink` 逐條對齊 —— 改一端要同步改另一端。
    """
    try:
        gl = sub_dir / ".git"
        if not gl.is_file():
            return None                          # 資料夾 = 獨立 repo；不存在 = 非 git
        line = gl.read_text(encoding="utf-8").strip()
        if not line.startswith("gitdir:"):
            return None
        rel = line[len("gitdir:"):].strip().replace("\\", "/")
        if not rel.startswith("../"):
            return None                          # 絕對路徑（worktree）→ 不處理
        up = 0
        while rel.startswith("../"):
            up += 1
            rel = rel[3:]
        p = sub_dir
        for _ in range(up):
            p = p.parent
        return p.resolve()
    except Exception:
        return None


# ─────────────────────────────────────────────────────────────────────────
# API 2 — ucl_core_dir()
# 區塊職責：回 UCL_Core submodule 根目錄的絕對路徑。
# 物理意義：從本檔位置往上找名為 UCL_Core 的 ancestor（depth-tolerant，掛載深度不綁死）。
#          僅在「UCL_Core 樹內」的 canonical 有意義；在 AgentCommands 鏡像位置 UCL_Core 不是
#          ancestor（跨專案掛載點不定，無法從 repo_root 反推），故誠實 raise 而非回垃圾路徑。
# 數值影響：canonical 回正確 UCL_Core 根；鏡像呼叫直接 raise，逼呼叫端改從 UCL_Core 端工具呼叫。
# ─────────────────────────────────────────────────────────────────────────
def ucl_core_dir() -> Path:
    if _UCL_CORE_DIR is None:
        raise RuntimeError(
            "ucl_core_dir()/ucl_tool() 只能在 UCL_Core 樹內呼叫。"
            "此檔為 AgentCommands 端鏡像，無法自我定位 UCL_Core"
            "（跨專案掛載點不定、UCL_Core 非本檔 ancestor）。"
            "需要 UCL_Core 路徑時請從 UCL_Core/Tools~ 下的工具呼叫，或改用 repo_root()/data_root()。"
        )
    return _UCL_CORE_DIR


# ─────────────────────────────────────────────────────────────────────────
# API 3 — data_root()
# 區塊職責：回 AgentCommands 資料根（狀態檔／letters／session lock／registry 所在）。
# 物理意義：預設 repo_root()/AgentCommands；但 C# 控制台可把資料根搬到別處，並把新絕對路徑
#          寫進 <repo_root>/.agentcommands_root.local pointer 檔（per-machine, gitignored）。
#          C#／Python 共讀同一 pointer 檔，兩端資料根永遠一致 (T-PATH-01)。
# 數值影響：lru_cache 後只算一次。pointer 內容須為「絕對路徑」才採用（相對值忽略走預設，
#          避免相對於誰的歧義）。讀檔異常一律 graceful 退回預設，不讓路徑解析炸掉。
# ─────────────────────────────────────────────────────────────────────────
@lru_cache(maxsize=1)
def data_root() -> Path:
    # tier-0：Editor 寫下的路徑快照（存在性已在 read_pointer 驗過，過期會被刪掉不會走到這）
    snap = read_pointer().get("data_root")
    if snap is not None:
        return snap
    return (repo_root() / "AgentCommands").resolve()          # 預設：repo_root/AgentCommands


# ─────────────────────────────────────────────────────────────────────────
# API 5 — resolve_data_path(default_subpath, config_key)
# 區塊職責：legacy 細粒度 override（_config/tavern_paths.json）+ pointer-aware 資料根，
#          合成「某個狀態子路徑到底在哪」的**唯一**解析點。
# 物理意義：本函式原本有三份各自實作 —— awakening.py `_resolve_data_path`、
#          memory.py `_resolve_letters_root`、C# `UCL_AwakeningService.ResolveOverridablePath`。
#          三份都對，但**三份就是三個會各自漂移的真相源**；而漂移的症狀是
#          「兩邊各看各的目錄，且兩邊都不報錯」——沒有任何一格會紅。
#          （Tim 2026-08-17 拍板 A 案：override 感知搬進本檔，awakening/memory 改委派。）
# 數值影響：純唯讀；override 命中時印一次 deprecation warning（per-process，旗標在本檔）。
# ⚠ config 檔位置是 **repo root 錨**（<repo_root>/AgentCommands/_config/tavern_paths.json），
#   **不是** data_root 錨 —— 原本兩份實作都這樣寫。看起來像該跟著資料根搬，
#   但改了就會在設 pointer 的機器上讀不到既有 override（靜默失去覆寫）。照抄，不「順手改對」。
# ─────────────────────────────────────────────────────────────────────────
def _path_config_file() -> Path:
    """已廢除的 legacy 覆寫檔位置（僅用於偵測殘留並 raise）。

    ⚠ 位置是 **repo root 錨**，不是 data_root 錨 —— 原本三份實作都這樣寫。
      看起來像該跟著資料根搬，但改了就偵測不到設 pointer 的機器上的殘留檔。
    """
    return repo_root() / "AgentCommands" / "_config" / "tavern_paths.json"


def resolve_data_path(default_subpath: str, config_key: str = "") -> Path:
    """資料根底下的子路徑。`config_key` 僅為呼叫端相容保留，已無作用。

    🩸 legacy 細粒度覆寫（_config/tavern_paths.json）已廢除（Tim 2026-08-17 拍板）。
      查證：`git log --all -- _config/tavern_paths.json` 為空 —— 所有分支、整段歷史
      都沒提交過那個檔，版控裡只有 .example.json 範本。
      但它是 per-machine / gitignored ⇒ 證得到「從沒被提交」，證不到「沒有機器留著」。
      ⇒ 存在即 raise，不安靜移除支援：**用一個吵的失敗換掉一個安靜的漂移**。
    """
    cfg_path = _path_config_file()
    if cfg_path.exists():
        raise RuntimeError(
            f"偵測到已廢除的細粒度路徑覆寫檔：{cfg_path}\n"
            "  該機制已被 <repo-root>/.agentcommands_root.local pointer 檔取代"
            "（整個資料根一次搬遷）。\n"
            "  處置：把 letters_dir / session_dir 的意圖改成資料根 override"
            "（Unity 控制台「AgentCommands 路徑」→ 套用），然後刪除或改名該檔。\n"
            "  ⚠ 這裡刻意不 fallback —— 靜默改讀另一個目錄比停下來糟。")
    if default_subpath.startswith("AgentCommands/"):
        return (data_root() / default_subpath[len("AgentCommands/"):]).resolve()
    return (data_root() / default_subpath).resolve()


# ─────────────────────────────────────────────────────────────────────────
# API — secrets_dir_name() / secrets_dir()
# 區塊職責：secrets 資料夾**名稱**的唯一解析點（python 端）。
# 物理意義：名字 2026-08-21 起由 `<data_root>/secrets_config.json` 決定
#          （C# 對側 `UCL_SecretsPath`，同一個檔、同一個 key `SecretsDir`）。
#          原本 `"_secrets"` 這個字面值散在 7 處 code、兩種語言 —— 改名等於七處同步，
#          而漏一處的症狀是靜默的（Discord daemon 只會說「token 未就緒」，
#          那句話跟「還沒安裝」長得一模一樣）。
# ⚠ 跟 2026-08-17 廢除的 `_config/tavern_paths.json` **不是同一種東西**：
#   那套是 per-machine + gitignored 的細粒度覆寫，症狀正是「兩台機器各看各的目錄且都不報錯」。
#   本設定**入版控、全機器同值** —— 不是「這台機器把 secrets 放別處」，
#   而是「這個專案的 secrets 資料夾叫什麼」。前者是漂移的入口，後者是佈局事實。
# 數值影響：檔案缺席 ⇒ 回預設 `Secret`。壞檔／空值 ⇒ 回預設並印一行 warning（per-process 只印一次）。
#   刻意**不做「找不到就退回 _secrets」的 fallback** —— 自排 fallback 是
#   「跑起來了但讀的是另一個宇宙的檔」那族的入口，而它不會叫。
# ⚠ 對側契約：C# 等價入口 = `UCL_SecretsPath.DirName` / `.AbsoluteDir`。兩端要一起改。
# ─────────────────────────────────────────────────────────────────────────
SECRETS_CONFIG_FILE = "secrets_config.json"
SECRETS_DIR_DEFAULT = "Secret"
_SECRETS_WARNED = False


def secrets_dir_name() -> str:
    """secrets 資料夾名（相對 data_root）。讀 <data_root>/secrets_config.json，缺席回預設。"""
    global _SECRETS_WARNED
    cfg = data_root() / SECRETS_CONFIG_FILE
    if not cfg.exists():
        return SECRETS_DIR_DEFAULT
    try:
        with open(cfg, encoding="utf-8") as fh:
            data = json.load(fh)
        name = str(data.get("SecretsDir") or "").strip().replace(chr(92), "/").strip("/")
        if name:
            return name
        if not _SECRETS_WARNED:
            print("[ucl_paths] %s 的 SecretsDir 是空的，改用預設 %r" % (cfg, SECRETS_DIR_DEFAULT))
            _SECRETS_WARNED = True
    except Exception as exc:                                   # noqa: BLE001
        if not _SECRETS_WARNED:
            print("[ucl_paths] 讀 %s 失敗（%s），改用預設 %r" % (cfg, exc, SECRETS_DIR_DEFAULT))
            _SECRETS_WARNED = True
    return SECRETS_DIR_DEFAULT


def secrets_dir() -> Path:
    """secrets 資料夾的絕對路徑（已套 data_root override）。"""
    return (data_root() / secrets_dir_name()).resolve()


# ─────────────────────────────────────────────────────────────────────────
# API 6 — personas_dir() / persona_file(persona) / letters_root()
# 區塊職責：persona 檔（登入狀態 / wake_count / 見林書籤 / 身分向量…）與信件根的**唯一**解析點。
# 物理意義：persona 檔目前 = <registry_path 的目錄>/personas/<persona>.json。
#          在這裡出現之前，這條路徑被 19 處各自用字串拼出來（Python 9 / C# 10）。
#          **多一條路徑的代價不是重複，是遷移時改不完的那幾處會靜默讀到舊檔**——
#          舊檔還在、讀得到，兩邊各自成功、各自綠燈，沒有一格會紅。
#          ⇒ 存在的理由不是少打字，是讓第二條路徑**沒有地方存在**。
# 數值影響：純字串組合，不檢查存在性（caller 自負；與 ucl_tool 同慣例）。
# ⚠ personas_dir 刻意從 `registry_path` 的**父目錄**推導，而不是直接 data_root/AwakenInit ——
#   那是 awakening.py 既有的語意（`_REGISTRY_PATH.parent / "personas"`）。
#   改成前者會在設了 registry_path override 的機器上指到別處，**而且不會報錯**。
# ⚠ 對側契約：C# 等價入口 = UCL_AwakeningService.PersonasDir / ResolvePersonaFile(persona)
#   / LettersDir。兩端要一起改 —— 只改一端 = 兩邊各看各的目錄，兩邊都不報錯。
# ─────────────────────────────────────────────────────────────────────────
def registry_path() -> Path:
    return resolve_data_path("AgentCommands/AwakenInit/persona_registry.json", "registry_path")


def awaken_init_dir() -> Path:
    """AwakenInit/ —— persona 檔、_registry_meta、agent_emails / agent_models 的家。

    ⚠ 從 registry_path() 的**父目錄**推導，不是 data_root()/"AwakenInit" ——
      那是 awakening.py 的既有語意（`_REGISTRY_PATH.parent`）。改成後者會在設了
      registry_path override 的機器上指到別處，**而且不會報錯**。
    """
    return registry_path().parent


def registry_meta_path() -> Path:
    return awaken_init_dir() / "_registry_meta.json"


# ⛔ `personas_dir()` / `persona_file()` 已退場（2026-08-21，Tim 拍板）：
#    persona 資料整合到 `letters/<persona>/`（身分欄 profile/、帳號 bank/<區域>.md），
#    中央 `AwakenInit/personas/` 不再存在。
#    · 名單 → `awakening.list_persona_names()`（判準＝profile/ 目錄存在）
#    · 欄位 → `_lib/persona_profile`（接縫；對側 = C# UCL_PersonaProfile）
#    · 目錄 → `letters_root()`
#    ⚠ 刻意**不留**「回舊路徑」的相容函式：那種函式的失敗方式是 `File.Exists` 為 False 之後
#      fail-soft，而症狀會長成「查無此人」而不是「路徑過期」—— 那是本專案最貴的一族錯誤。
def personas_dir() -> Path:
    raise RuntimeError(
        "personas_dir() 已退場（2026-08-21）：persona 資料在 letters/<persona>/。"
        " 名單走 awakening.list_persona_names()，欄位走 _lib/persona_profile。")


def persona_file(persona: str) -> Path:
    raise RuntimeError(
        f"persona_file({persona!r}) 已退場（2026-08-21）：那個中央 json 不存在了。"
        " 身分欄在 letters/<persona>/profile/<欄>.md，帳號在 letters/<persona>/bank/<區域>.md。")


def letters_root() -> Path:
    return resolve_data_path("AgentCommands/ChatTavern/baton/letters", "letters_dir")


# 區塊職責: letters 目錄**底下的版面** — persona 目錄 / Cmd 回傳檔子目錄與檔名組法。
# 物理意義: letters 頂層原本同時住著**人寫的信**(時間戳命名)與**機器寫的 Cmd 回傳檔**(`_` 開頭)。
#          🩸 2026-08-18 實測: Cmd_DocEdit 要找「最新那封信」時抓到了 `_freetime_next.md` ——
#          機器產物每跑一次 Cmd 就更新, 所以「最新的 .md」幾乎永遠是機器的。
#          ⇒ Tim 拍板把 Cmd 回傳檔移進 `cmd/` 子目錄: 「是不是信」不再靠檔名前綴猜, 而是位置的問題。
# ⚠ **對側契約**: C# 等價入口是 `UCL_LettersPath`(CmdDirName / CmdDir / CmdPayload)。
#   兩端要一起改 —— 只改一端的後果是兩邊各看各的目錄, 而**兩邊都不會報錯**
#   (寫檔會自動建目錄, 於是新舊位置各有一份、各自看起來都正常)。
# 數值影響: 純路徑組合, 不建目錄(建目錄由寫入端負責)。
LETTERS_CMD_DIRNAME = "cmd"


def letters_persona_dir(persona: str) -> Path:
    """某 persona 的 letters 目錄 —— **人寫的信住這裡**。"""
    return letters_root() / persona


def letters_cmd_dir(persona: str) -> Path:
    """某 persona 的 Cmd 回傳檔目錄(`letters/<persona>/cmd/`)。"""
    return letters_persona_dir(persona) / LETTERS_CMD_DIRNAME


def letters_cmd_payload(persona: str, cmd: str, step: str) -> Path:
    """一份 Cmd 回傳檔(`letters/<persona>/cmd/<cmd>_<step>.md`) —— 檔名**不帶 `_` 前綴**(目錄已說明它是什麼)。"""
    return letters_cmd_dir(persona) / f"{cmd}_{step}.md"


# `cmd/` 目錄自帶的 ignore 內容 —— 與 C# `UCL_LettersPath.CmdDirGitignore` **逐位元相同**
# （驗法：兩端各建一次、比 sha256；不同就是有一端被改過而另一端沒跟上）。
# 物理意義：回傳檔是 transient，不入版控。原本這件事靠各 letters repo 根 `.gitignore` 逐檔列名，
#          而清單天生會落後：FreeTime 遷進 `cmd/` 之後那幾行全部失配，4 份回傳檔就被 commit 了；
#          更重的是 `_wake_brief.md`（含活 session_token 與信箱、remote 公開）照計畫也要搬進來。
# ⇒ 規則改成跟著「位置」走，新增任何 Cmd / step 都不必再維護清單。
CMD_DIR_GITIGNORE = (
    "# Cmd 回傳檔（transient）—— 每跑一次就重生、手改無效，一律不入版控。\n"
    "# 有些回傳檔含 session_token / 信箱等憑證，而 letters remote 可能是公開的；\n"
    "# 這份 ignore 是「目錄層」的，所以新增任何 Cmd / step 都不必再維護逐檔清單。\n"
    "# 本檔由 UCL_LettersPath.EnsureCmdDir() / ucl_paths.ensure_letters_cmd_dir() 自動建立（兩端同一份字面）。\n"
    "*\n"
    "!.gitignore\n"
)


def ensure_letters_cmd_dir(persona: str) -> Path:
    """建好 `letters/<persona>/cmd/` 並確保內有 `.gitignore`（缺才寫，不覆蓋既有）；回該目錄。

    ⚠ **本函式是本檔唯一會寫檔的一支**（見檔頭「數值影響」）。放在這裡是因為
      「`cmd/` 不入版控」屬於版面語意，而版面在本檔 —— 交給各寫入端各自記得就是下一次靜默漂移。
    失敗不丟例外：回傳檔本身比 ignore 重要，不該因為這步讓 Cmd／工具掛掉。
    """
    d = letters_cmd_dir(persona)
    try:
        d.mkdir(parents=True, exist_ok=True)
        gi = d / ".gitignore"
        if not gi.exists():                      # 缺才寫 —— 有人手改過（放行某一份）時不該被蓋回
            # newline="\n" 是硬需求：預設會在 Windows 把 \n 轉成 \r\n，而 C# 端寫的是 LF
            # ⇒ 兩端產出就不再逐位元相同（同一份檔在兩端交替寫入會製造無意義的 diff）。
            gi.write_text(CMD_DIR_GITIGNORE, encoding="utf-8", newline="\n")
    except Exception as e:
        print(f"[ucl_paths] cmd/ 目錄或 .gitignore 準備失敗（回傳檔仍會嘗試寫入）：{d} — {e}")
    return d


# ─────────────────────────────────────────────────────────────────────────
# 外部漫畫庫路徑（.comic_root.local 快照檔）
# 區塊職責：讀取 C# UCL_ReadingLibraryIO 寫出的外部漫畫庫本機快照。
# 物理意義：Python 端唯讀消費、絕不刪檔自癒（Tim 2026-08-17 拍板方案 B）。
# 數值影響：若目錄不存在或未掛載，回傳 None 並印警告，保留快照內容。
# ─────────────────────────────────────────────────────────────────────────
COMIC_ROOT_FILENAME = ".comic_root.local"


def comic_root() -> Path | None:
    candidates = []
    if _UCL_CORE_DIR:
        candidates.append(_UCL_CORE_DIR / COMIC_ROOT_FILENAME)
    root = repo_root()
    if root:
        candidates.append(root / COMIC_ROOT_FILENAME)

    for p in candidates:
        if p.is_file():
            try:
                for line in p.read_text(encoding="utf-8").splitlines():
                    line = line.strip()
                    if not line or line.startswith("#"):
                        continue
                    if "=" in line:
                        k, _, v = line.partition("=")
                        if k.strip() == "comic_root":
                            cand_path = Path(v.strip())
                            if cand_path.is_dir():
                                return cand_path.resolve()
                            else:
                                import sys
                                print(f"⚠ [ucl_paths] comic_root 目錄不存在或未掛載: {cand_path}", file=sys.stderr)
                                return None
                    else:
                        cand_path = Path(line)
                        if cand_path.is_dir():
                            return cand_path.resolve()
            except Exception:
                pass
    return None


# ─────────────────────────────────────────────────────────────────────────
# API 4 — ucl_tool(name)
# 區塊職責：組出 UCL_Core 內某支工具腳本的絕對路徑（e.g. run_cmd.py / awakening.py）。
# 物理意義：所有工具都在 <UCL_Core>/Tools~/AgentCommands/ 下；本函式把「認死那段相對路徑」
#          集中成一處，日後 UCL_Core 內部結構若調整只改這裡。
# 數值影響：純字串組合，不檢查檔案是否存在（caller 自行負責存在性；保持純路徑語意）。
# 參數 name：工具檔名（可含子路徑，如 "CommandResolver/normalize.py"）。
# ─────────────────────────────────────────────────────────────────────────
def ucl_tool(name: str) -> Path:
    return _UCL_CORE_DIR / "Tools~" / "AgentCommands" / name



# ─────────────────────────────────────────────────────────────────────────
# 區塊職責：CLI 自測入口 —— `python ucl_paths.py` 直接印四支 API 解析結果。
# 物理意義：給 T06 扁平專案實測 / 開發者快速核對「這台機器上四支 API 各回哪裡」用。
# 數值影響：純印字，不改任何狀態。
# ─────────────────────────────────────────────────────────────────────────
if __name__ == "__main__":
    import sys
    # Windows console cp950 → UTF-8，避免中文路徑印錯
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    print("# ucl_paths.py 解析結果")
    print(f"repo_root()    = {repo_root()}")
    print(f"ucl_core_dir() = {ucl_core_dir()}")
    print(f"data_root()    = {data_root()}")
    print(f"ucl_tool('run_cmd.py') = {ucl_tool('run_cmd.py')}")
