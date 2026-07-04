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
# 數值影響：純唯讀檔案系統探測（os.path/Path 判斷），不寫任何 asset / token / 狀態檔。
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
@lru_cache(maxsize=1)
def repo_root() -> Path:
    # tier-1：顯式 env override（須通過 is_dir 驗證才採用，避免指到不存在的路徑靜默誤用）
    env_root = os.environ.get("CLAUDE_PROJECT_DIR")            # 讀 hook 注入的專案根
    if env_root and Path(env_root).is_dir():                   # 有值且確實是資料夾才採信
        return Path(env_root).resolve()                        # 正規化後回傳

    # tier-2：從本檔位置往上 walk（結構錨，最穩，不受呼叫端 cwd 影響）
    walked = _find_git_root_by_walk(_THIS_FILE)                # 從 ucl_paths.py 起 walk
    if walked:                                                 # 命中真實 .git 資料夾
        return walked

    # tier-3：從 cwd 往上 walk（本檔在奇特打包／複製情境下失效時的補救）
    walked_cwd = _find_git_root_by_walk(Path.cwd())            # 從當前工作目錄起 walk
    if walked_cwd:
        return walked_cwd

    # fallback：完全找不到 .git（罕見；e.g. tarball 解壓無 git 環境）→ 退回 UCL_Core 根
    #          刻意不退 cwd／不亂猜，維持可預期行為，caller 可自行帶 CLAUDE_PROJECT_DIR 校正。
    return _UCL_CORE_DIR


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
    root = repo_root()                                         # 先取 host repo root
    pointer = root / ".agentcommands_root.local"              # pointer 檔固定放 repo root 下
    try:
        if pointer.exists():                                  # 有 pointer 檔才嘗試讀
            content = pointer.read_text(encoding="utf-8").strip()  # 讀內容並去頭尾空白
            if content:                                       # 非空
                p = Path(content)
                if p.is_absolute():                           # 僅接受絕對路徑（相對值歧義故忽略）
                    return p.resolve()                        # 採用 pointer 指定的搬遷後資料根
    except Exception:
        # 讀檔失敗（權限／編碼／IO）→ 靜默退回預設，路徑解析不因 pointer 壞掉而中斷
        pass
    return (root / "AgentCommands").resolve()                 # 預設：repo_root/AgentCommands


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
