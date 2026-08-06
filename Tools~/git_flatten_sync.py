#!/usr/bin/env python
# 區塊職責：把含 submodule 的 repo「攤平」成純檔案，同步到另一個 repo 的工作目錄
# 物理意義：src 端**只讀 git 物件**（ls-tree / cat-file），dst 端**只寫檔案** ——
#          兩邊的 git 一個字都不動：不 fetch、不 commit、不改 index、不動 ref、不刪任何 .git。
#          輸出是「攤平後的檔案樹」：submodule 內容落在它原本的路徑上，
#          `.gitmodules` 與 gitlink 條目完全不存在。
# 數值影響：dry-run 完全唯讀（連 dst 都不寫）。apply 只寫 dst 工作目錄內的檔案 +
#          一份 manifest（預設 <dst>/.ucl_flatten/manifest.json，可用 --manifest 移走）。
#
# 設計決策與出處（2026-08-05，Tim 拍板 / gura・Sirius 砸磚後定案）：
#   ① drift 即 fail closed（gura + Sirius）：父記錄的 gitlink SHA 與 submodule 磁碟 HEAD 不同時，
#      **拒絕執行**，不猜。要跑就顯式 --mode recorded|head；選 head 而該 SHA 不可回溯 → 一律拒絕。
#      理由：兩種靜默選擇都「外觀成功」——「父記錄」靜默少東西、「head」靜默多一份無法回溯的東西。
#      而警示可以被忽略，拒絕不能。
#   ② 不在 dst 塞任何來源沒有的檔案（gura + Sirius，推翻我原本的 placeholder 方案）：
#      排除資訊放同步報告，不放檔案樹 —— 否則破壞「dst 檔案 ≡ 來源 tracked 內容」這個等式，
#      diff 會炸出來源根本沒有的東西。唯一例外是 manifest（已揭露、可移走、不列入驗證集合）。
#   ③ 只同步 tracked 內容。gitignore 掉的不會過去（要磁碟快照請用別的工具）。
#   ④ 跨次回歸靠「由來源圖獨立產生的輸入清單」，不是拿本次輸出比上次輸出（Sirius）——
#      後者是自己的輸出替自己背書。
#   ⑤ 破壞性動作預設不做：stale 刪除要 --prune，且第一次執行（無 manifest）只報告不刪。
#   ⑥ 刪除有**兩個**旗標，因為候選集合與風險級別不同（2026-08-06 Tim：「D 刪除了 dst 還留著」）：
#      · --prune        候選＝manifest（本工具上次寫過什麼）→ 只認得**自己造成的**遺留。
#      · --delete-extra 候選＝**dst 自己的 git 追蹤清單** 減去 src 攤平清單 → 兩邊檔案集合一致。
#        後者刪的是「本工具從沒寫過的檔」，所以候選集合必須是 tracked：**tracked 才有還原點**
#        （本工具不動 dst 的 git ⇒ 刪掉 untracked 就真的沒了）。也因此它需要
#        **兩端都是 git repo**；dst 不是 → exit 7 fail closed，不靜默降級成「那就不刪」
#        （勾了選項卻沒有作用，是最難發現的一種壞）。
#        排除的 submodule 底下一律不列入候選 —— 排除的語意是「這塊我不同步」，
#        不是「這塊在 src 不存在」，算進差集會把使用者刻意保留的整個目錄刪光。
#
# 📒 manifest（`<dst>/.ucl_flatten/manifest.json`）是什麼、能不能刪（2026-08-06 Tim 問，實測後寫下）：
#   **它不是快取，是帳本 —— 「這個差異是誰造成的」的唯一證人。**
#   dst 上某個檔跟 src 不一樣時只有兩種可能：src 後來更新了，或有人在 dst 改過。
#   兩者在磁碟上長得**完全一樣**；能分辨，只因為 manifest 記著「本工具上次寫下的內容雜湊」。
#
#   消費端只有兩個（別再去找第三個）：
#     ① `prev_files[path] == 磁碟現況` → 這是我上次寫的 ⇒ 安全覆蓋；否則 ⇒ 衝突
#     ② `stale = prev_files - files`   → `--prune` 的刪除對象
#
#   刪掉它會怎樣（實測，非推論）：src 把 A.txt 由 v1 改成 v2 之後同一份資料 ——
#     有 manifest：要寫入 1 / 衝突 0        （正常更新）
#     無 manifest：要寫入 0 / **衝突 1**    （整支 exit 5 中止）
#   於是每次同步都被當成「首次」，凡 src 更新過的檔全變衝突 → 只能靠 `--force` 才跑得動。
#   **而那等於永久失去分辨能力**：真的有人在 dst 改了東西，你也再看不見。
#   這不是麻煩，是判準消失。`--prune` 同時退化成空操作（stale 恆為 0）。
#   ⚠ 例外：`--delete-extra` **不受影響** —— 它的候選來自 dst 自己的 git 追蹤清單，
#   與 manifest 是兩個獨立來源（實測無 manifest 時照樣算得出來）。
#
#   **不要進版控。** 理由不是「它是產物」那麼軟：`src` 欄位存的是**絕對路徑**
#   （例：`D:\Unity\LY`），跨機器必錯；且它隨每次同步整份重寫（LY→osawari01 是 9127 筆 / 1.1MB），
#   進 git 只會製造無意義的巨大 diff。建議在 dst 的 .gitignore 加 `.ucl_flatten/`
#   —— 否則它會一直杵在 untracked 裡當噪音。
#   要換位置用 `--manifest`；它本來就是「已揭露、可移走、不列入驗證集合」的那一份。
#
# ⚠ exit code 有第二個消費端（2026-08-06，Tim 指出面板走死路後加）：
#   `UCL_GitFlattenSyncPage` 用 **4 = drift / 5 = dst 有本地修改** 把拒絕原因翻成
#   「在面板上該動哪一個控制項」。原因：本檔的拒絕訊息寫的是 CLI 旗標（`--force` / `--mode`），
#   那對 CI / agent 是對的，但**按按鈕的人手上沒有命令列** —— 照字面讀會得到一個做不到的指示。
#   於是「講 CLI」與「講按鈕」各自留在擁有那一層的檔案裡，這裡不去猜 UI 長什麼樣。
#   代價是這兩個數字成了跨語言契約：**改動 4 / 5 的語意，要同步改那支頁面的 EXIT_DRIFT /
#   EXIT_CONFLICT**，否則面板會安靜地指向錯的控制項（訊息還是對的，指路變成錯的）。
#   （7 = --delete-extra 但 dst 不是 git repo，同屬這份契約。）
# @doc-sync: ../UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_GitFlattenSyncPage.cs
#
# 為什麼不用 git archive（實測後否決，別再繞回去）：
#   · archive **會帶 .gitmodules**，且對 gitlink 產生空目錄條目
#   · archive **會套用 .gitattributes 的 eol 屬性** —— 實測同一 blob：
#     blob dd8c6a39… vs archive 落檔 59b0d777…，**位元組不同**。
#     那會讓「逐檔內容比對」這個驗證判準誤報（內容其實是對的）。
#   自己讀 blob 寫檔就沒有這層轉換，位元組級精確，判準也乾淨。
#
# 為什麼不用 read-tree --prefix / write-tree（原型走過，因約束變更而廢）：
#   那產出 tree 物件，要變成檔案得 checkout，而 checkout 必須動 dst 的 index。
import argparse
import hashlib
import json
import os
import subprocess
import sys
from pathlib import Path

MANIFEST_REL = ".ucl_flatten/manifest.json"
SCHEMA = 1


# ===========================================================
# git 呼叫（全部唯讀指令；本檔任何地方都不呼叫寫入型 git 指令）
# ===========================================================
def git(repo, *args, check=True):
    r = subprocess.run(["git", "-C", str(repo), *args],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    if check and r.returncode != 0:
        raise RuntimeError(f"git {' '.join(args)} @ {repo} failed:\n{r.stderr.strip()}")
    return r.stdout


def git_bytes(repo, *args):
    """需要原始位元組的場合（cat-file --batch）—— 不可經 text 模式，會毀掉二進位檔。"""
    return subprocess.run(["git", "-C", str(repo), *args], capture_output=True).stdout


def blob_sha(data: bytes) -> str:
    """
    區塊職責：獨立重算 git 的 blob 物件雜湊
    物理意義：git 的 blob SHA = sha1(b"blob <len>\\0" + content)。
             這裡刻意**自己算**而不呼叫 `git hash-object` ——
             驗證要用獨立實作，拿被測工具自己的雜湊來驗它自己是循環論證。
             （附帶好處：不必為每個檔開一個 process。）
    """
    h = hashlib.sha1()
    h.update(b"blob %d\0" % len(data))
    h.update(data)
    return h.hexdigest()


# ===========================================================
# 來源圖：submodule 發現 + drift 判定
# ===========================================================
def discover(src: Path):
    """
    回傳每個 submodule 的 path / recorded_sha（其 owner 樹裡記的）/ head_sha（磁碟 checked out）/ owner。
    ⚠ `git submodule status` 第一欄是**磁碟 checked-out SHA**，不是父記錄 —— 標錯會讓
      「攤誰的 commit」這個選擇整個失去意義（實摔過一次）。父記錄要去 owner 的樹裡撈。
    """
    raw = []
    for line in git(src, "submodule", "status", "--recursive").splitlines():
        if not line.strip():
            continue
        flag = line[0] if line[0] in "+-U " else " "
        parts = (line[1:] if flag != " " else line).split()
        if len(parts) < 2:
            continue
        raw.append({"path": parts[1].replace("\\", "/"), "head_sha": parts[0],
                    "drift_flag": flag == "+", "uninitialized": flag == "-"})
    paths = {r["path"] for r in raw}
    subs = []
    for r in raw:
        p = r["path"]
        owners = [o for o in paths if p.startswith(o + "/")]
        owner_rel = max(owners, key=len) if owners else ""
        owner_repo = src / owner_rel if owner_rel else src
        rel = p[len(owner_rel) + 1:] if owner_rel else p
        # ⚠ owner 自己也可能未 init（巢狀情況）。在**未 init 的空目錄**裡跑 git 不會報錯 ——
        #   git 會靜默往上走到最近的父 repo，於是讀回來的是**別人的 HEAD**。
        #   實摔：巢狀 leaf 未 init 時，`rev-parse HEAD` 回的是 mid 的 HEAD，
        #   後面 ls-tree 才用「not a tree object」死掉，而那個錯誤訊息完全指不到真正的原因。
        if (owner_repo / ".git").exists():
            entry = git(owner_repo, "ls-tree", "HEAD", "--", rel, check=False).split()
            recorded = entry[2] if len(entry) >= 3 else ""
        else:
            recorded = ""     # owner 未 init → 讀不到，不猜
        initialized = (src / p / ".git").exists()
        subs.append({**r, "recorded_sha": recorded, "owner": owner_rel or "",
                     "uninitialized": r["uninitialized"] or not initialized,
                     # 該 submodule 自己也是個 superproject（有 .gitmodules）—— 只有資訊性用途，
                     # 攤平時 .gitmodules 是按**路徑**濾掉的，不依賴這個欄位。
                     "has_gitmodules": (src / p / ".gitmodules").is_file()})
    subs.sort(key=lambda s: s["path"].count("/"))
    return subs


def reachable(src: Path, sub: dict, sha: str) -> bool:
    """
    區塊職責：該 SHA 是否可由**已推送的 remote 分支**回溯（Sirius 的要求）
    物理意義：只存在本機、還沒 push 的 commit 攤進 dst = 目標端有一份無法回溯來源的內容。
             判準用 `branch -r --contains`：有任何 remote 分支含它才算可回溯。
    """
    out = git(src / sub["path"], "branch", "-r", "--contains", sha, check=False)
    return bool(out.strip())


# ===========================================================
# 來源檔案集合（path → blob sha），這是「應該同步什麼」的唯一事實來源
# ===========================================================
def source_files(src: Path, src_sha: str, subs, excluded, mode: str):
    """
    區塊職責：由來源圖獨立產生「預期落地的 path → blob sha」全集
    物理意義：src 自身的樹 + 每個納入的 submodule 的樹（加路徑前綴）。
             兩者都要濾掉 **gitlink(160000)** 與 **.gitmodules** —— 那是「攤平」的定義。
    數值影響：純讀。這份集合同時是寫入清單、驗證判準、與跨次回歸的輸入清單。
    """
    files = {}
    skipped = {"gitlinks": 0, "gitmodules": 0}

    def harvest(repo: Path, sha: str, prefix: str):
        # ⚠ 必須用 `-z`（NUL 分隔、路徑原始輸出）。
        #   不帶 -z 時 git 會把**非 ASCII 路徑**加引號並轉成八進位轉義（`"Assets - \346..."`），
        #   只剝引號不解轉義 → 落地路徑變成字面的 `Assets - \346`，
        #   在 Windows 上直接 FileNotFoundError（實摔：LY 有中文檔名）。
        #   自己寫解轉義是重造 git 已經給你的東西，而且一定漏 case。
        raw = git(repo, "ls-tree", "-r", "-z", sha)
        for rec in raw.split("\0"):
            if not rec.strip():
                continue
            meta, path = rec.split("\t", 1)
            m, typ, blob = meta.split()
            path = path.replace("\\", "/")
            full = f"{prefix}{path}"
            if m == "160000" or typ == "commit":
                skipped["gitlinks"] += 1
                continue
            if path == ".gitmodules" or path.endswith("/.gitmodules"):
                skipped["gitmodules"] += 1
                continue
            # rel = 該檔在**自己那個 repo 內**的路徑。
            # smudge 與 lfs 索引都以 repo 為座標系，用 full（含 submodule 前綴）會查不到。
            files[full] = {"sha": blob, "mode": m, "repo": str(repo), "rel": path}

    harvest(src, src_sha, "")
    for s in subs:
        if s["path"] in excluded:
            continue
        harvest(src / s["path"], s["head_sha"] if mode == "head" else s["recorded_sha"],
                s["path"] + "/")
    return files, skipped


def cascade_exclude(subs, asked):
    """排除父 submodule → 連帶排除其下巢狀。否則會落下「有內容但少了外面一層」的怪結構。"""
    ex = set(asked)
    for s in subs:
        for a in asked:
            if s["path"] == a or s["path"].startswith(a + "/"):
                ex.add(s["path"])
    return ex


# ===========================================================
# 防呆（Tim 2026-08-05：dst 若是 Unity 專案要明確提醒避免覆蓋本地）
# ===========================================================
def guard(src: Path, dst: Path, force: bool):
    """
    回傳 (fatal: list[str], warn: list[str])。fatal 非空 → 一律拒絕，--force 也不放行。
    物理意義：
      · dst == src 或互相嵌套 → 會邊讀邊蓋自己，沒有任何正當用途
      · dst/Temp/UnityLockfile 存在 → **Unity 正開著這個專案**（實測可靠訊號）：
        往它身上寫檔會直接覆蓋人家正在編輯的專案，這正是 Tim 要防的那件事
      · dst 是 Unity 專案（有 ProjectSettings + Assets）→ 提醒，不擋（往 Unity 專案匯出可能正是目的）
    """
    fatal, warn = [], []
    s, d = src.resolve(), dst.resolve()
    if s == d:
        fatal.append(f"dst 與 src 是同一個路徑（{d}）—— 會邊讀邊覆蓋自己。")
    elif str(d).startswith(str(s) + os.sep):
        fatal.append(f"dst 在 src 內部（{d}）—— 同步過程會覆蓋來源。")
    elif str(s).startswith(str(d) + os.sep):
        fatal.append(f"src 在 dst 內部（{s}）—— 同步會覆蓋來源本身。")
    if (d / "Temp" / "UnityLockfile").is_file():
        fatal.append(f"dst 有 Temp/UnityLockfile —— **Unity 正開著這個專案**。"
                     f"往它寫檔會覆蓋正在編輯的本地內容。關掉那個 Unity 再跑。")
    if (d / "ProjectSettings").is_dir() and (d / "Assets").is_dir():
        warn.append("dst 是一個 Unity 專案 —— 同步會直接覆蓋它 Assets/ 等路徑下的檔案。"
                    "確認這正是你要的。")
    if not (d / ".git").exists():
        warn.append("dst 看起來不是 git repo（沒有 .git）—— 仍可同步（本工具只寫檔案），"
                    "但你會失去用 git diff 檢視這次同步結果的能力。")
    return fatal, warn


# ===========================================================
# manifest（跨次同步的唯一狀態：這支工具上次寫過哪些檔）
# ===========================================================
LFS_MAGIC = b"version https://git-lfs.github.com/spec/v1"


def lfs_pointer(data: bytes):
    """是 Git LFS 指標就回 (oid_sha256, size)，否則回 None。

    區塊職責：辨認「blob 內容不是檔案內容」的那一類檔。
    物理意義：LFS 追蹤的檔，**存進 git 的 blob 是一張約 130 bytes 的指標**，
             真內容由 checkout 時的 smudge filter 從 LFS 物件庫還原。
             所以 `cat-file -p` 拿到的是指標 —— 對 LFS 檔而言，
             **「讀 blob」與「讀檔案」是兩個不同的座標系**。
    數值影響：純判讀，不改資料。指標很小且格式固定，先擋 size 再比對，成本可忽略。
    """
    if len(data) > 400 or not data.startswith(LFS_MAGIC):
        return None
    oid = size = None
    for line in data.decode("utf-8", "replace").splitlines():
        if line.startswith("oid sha256:"):
            oid = line.split(":", 1)[1].strip()
        elif line.startswith("size "):
            try:
                size = int(line.split(" ", 1)[1].strip())
            except ValueError:
                return None
    return (oid, size) if oid and size is not None else None


def smudge(repo: str, path: str, sha: str) -> bytes:
    """套用 clean/smudge filter 取檔案的**工作目錄內容**（LFS 檔會還原成真檔）。

    物理意義：`cat-file --filters` 跟 `cat-file -p` 的差別就是「檔案」與「blob」的差別。
             一般檔兩者相同，LFS 檔差了整個檔案本體。
    設計取捨：**只對偵測到是 LFS 指標的檔呼叫**，不是全部走 --filters ——
             它一檔一個 process，9000 檔全走會慢到不能用；而 798 檔可以接受。
    邊界：LFS 物件不在本機（未 pull）時 git 會回指標本身或報錯 —— 呼叫端必須驗，不可信這裡的回傳。
    """
    r = subprocess.run(["git", "-C", repo, "cat-file", "--filters",
                        "--path=" + path, sha], capture_output=True)
    if r.returncode != 0:
        raise RuntimeError(f"cat-file --filters 失敗（path={path}）：{r.stderr.decode('utf-8', 'replace').strip()}")
    return r.stdout


def lfs_index(files: dict) -> dict:
    """{ 落地路徑 → LFS oid(sha256) }，只含 LFS 追蹤的檔。

    區塊職責：告訴其餘三段（比對 / 寫入 / 驗證）「這個檔的事實源是 sha256 不是 blob sha」。
    物理意義：LFS 檔有兩個雜湊 —— git blob sha（指標的）與 oid sha256（真內容的）。
             一旦 dst 落地的是**真內容**，拿 blob sha 去比就永遠不相等，
             於是每次同步都會把全部 LFS 檔判成「本地被改過」（798 筆假衝突）。
    設計取捨：一個 repo 一次 `git lfs ls-files`，不逐檔 check-attr。
             LFS 未安裝 / 該 repo 沒用 LFS → 回空，全流程退回純 blob 語意（本來就正確）。
    """
    by_repo = {}
    for full, info in files.items():
        by_repo.setdefault(info["repo"], []).append((full, info["rel"]))
    out = {}
    for repo, items in by_repo.items():
        try:
            raw = git(repo, "lfs", "ls-files", "-l")
        except Exception:
            continue        # 沒裝 lfs / 這個 repo 不用 lfs —— 不是錯誤
        oid_by_rel = {}
        for line in raw.splitlines():
            # 格式：`<oid> <*|-> <path>`（* = 本機有物件，- = 只有指標）
            parts = line.split(" ", 2)
            if len(parts) == 3:
                oid_by_rel[parts[2].strip().replace("\\", "/")] = parts[0].strip()
        for full, rel in items:
            oid = oid_by_rel.get(rel)
            if oid:
                out[full] = oid
    return out


def dst_tracked_files(dst: Path):
    """dst 端**被自己的 git 追蹤**的檔案清單（posix 相對路徑集合）。gitlink 條目排除。

    區塊職責：`--delete-extra` 的刪除候選集合來源。
    物理意義：這是本工具第一次**讀 dst 的 git**（先前只寫 dst 的檔）。仍然唯讀 ——
             不 fetch、不 commit、不改 index。檔頭那句「dst 只寫檔案」因此擴充為
             「dst 只寫檔案、必要時唯讀查詢它的 index」，不是被推翻。
    為什麼候選集合是「dst 的 tracked 檔」而不是「dst 磁碟上的所有檔」：
      ① **可回復。** 凡是 tracked 的，刪掉都能用 dst 自己的 git 還原；
         untracked 的刪掉就沒了，而本工具不動 dst 的 git ⇒ 沒有還原點。
         破壞性動作只准落在「有還原點」的集合上。
      ② **對稱。** src 端本來就只攤平 tracked 內容（設計決策③）。
         拿「src 的 tracked」去減「dst 的**全部**」不是同一個座標系，
         算出來的差集會包含 Library/ Temp/ 與 dst 自己的 ignored 產物 —— 那是刪掉整個專案。
      ③ gitlink（mode 160000）排除：那是 submodule 指標不是檔案，
         用刪檔的方式處理它會刪掉別人的整個工作目錄。
    """
    out = git(dst, "ls-files", "-s", "-z")
    files = set()
    for rec in out.split("\0"):
        if not rec:
            continue
        meta, _, path = rec.partition("\t")
        if not path:
            continue
        if meta.split(" ", 1)[0] == "160000":   # gitlink — 不是檔案
            continue
        files.add(path)
    return files


def load_manifest(path: Path):
    if not path.is_file():
        return None
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except Exception as e:
        # 壞掉的 manifest 不可當成「沒有 manifest」—— 那會讓 prune 誤判成第一次執行、
        # 把上次寫的檔全部當成「不是我們寫的」而永遠不清。要吵出來。
        raise RuntimeError(f"manifest 解析失敗（不會當作首次執行，請人工處理）：{path}\n{e}")


def main():
    ap = argparse.ArgumentParser(
        description="Flatten a repo's submodules into plain files and sync to another repo's working dir.")
    ap.add_argument("--src", required=True)
    ap.add_argument("--dst", default="", help="目標 repo（--list-submodules 時不需要）")
    ap.add_argument("--exclude", default="", help="不同步的 submodule 路徑（逗號分隔），會連帶排除其巢狀")
    ap.add_argument("--mode", choices=["recorded", "head"], default=None,
                    help="攤父記錄的 gitlink SHA 還是 submodule 磁碟 HEAD。"
                         "drift 時**必填**（不猜）；一致時可省略")
    ap.add_argument("--manifest", default=None, help=f"manifest 路徑（預設 <dst>/{MANIFEST_REL}）")
    ap.add_argument("--prune", action="store_true", help="刪除「上次寫過、這次不在清單」的檔（預設只報告）")
    ap.add_argument("--delete-extra", action="store_true",
                    help="讓 dst 與 src 檔案集合一致：刪掉「dst 有追蹤、但 src 沒有」的檔"
                         "（候選集合＝dst 的 tracked 檔，可用 dst 的 git 還原；排除的 submodule 底下不動）")
    ap.add_argument("--apply", action="store_true", help="真的寫入（預設 dry-run，完全唯讀）")
    ap.add_argument("--force", action="store_true", help="放行 warn 等級的防呆（fatal 永不放行）")
    ap.add_argument("--format", choices=["md", "json"], default="md")
    ap.add_argument("--list-submodules", action="store_true",
                    help="只列出 src 的所有 submodule（JSON）後結束。**含被排除的** —— "
                         "給 UI 畫勾選清單用：清單只列納入的話，取消勾選之後那一列就消失、無法還原")
    args = ap.parse_args()

    src = Path(args.src).resolve()
    if not (src / ".git").exists():
        print(f"✗ src 不是 git repo：{src}", file=sys.stderr)
        return 2

    # 區塊職責：只列 submodule 就結束（給 UI 畫勾選清單）
    # 物理意義：**列出全部，含被排除的** —— UI 的清單若只含納入項，取消勾選之後那一列就消失、
    #          使用者無法還原（我第一版頁面吃 dry-run 的 inputs，正好踩到）。
    #          也不做 drift / 未 init 的 fail closed —— 那是「要不要執行同步」的判準，
    #          不是「這個專案有哪些 submodule」的判準。狀態照實回報，讓 UI 標記出來。
    if args.list_submodules:
        subs = discover(src)
        print(json.dumps({
            "src": str(src),
            "src_sha": git(src, "rev-parse", "HEAD").strip(),
            "submodules": [{
                "path": s["path"], "owner": s["owner"],
                "recorded_sha": s["recorded_sha"], "head_sha": s["head_sha"],
                "drift": bool(s["recorded_sha"] and s["head_sha"]
                              and s["recorded_sha"] != s["head_sha"]),
                "uninitialized": bool(s["uninitialized"]),
                "has_gitmodules": bool(s["has_gitmodules"]),
            } for s in subs],
        }, ensure_ascii=False, indent=1))
        return 0

    if not args.dst:
        print("✗ 需要 --dst（只有 --list-submodules 可以省略）", file=sys.stderr)
        return 2
    dst = Path(args.dst).resolve()
    manifest_path = Path(args.manifest) if args.manifest else dst / MANIFEST_REL

    rep = {"src": str(src), "dst": str(dst), "schema": SCHEMA}
    out = []

    # ---- 防呆 ----
    fatal, warn = guard(src, dst, args.force)
    rep["fatal"], rep["warn"] = fatal, warn
    if fatal:
        out.append("# 🚫 拒絕執行\n")
        for f in fatal:
            out.append(f"- **{f}**")
        out.append("\n（fatal 等級的防呆 `--force` 也不放行 —— 這幾種情況沒有正當用途。）")
        print("\n".join(out))
        return 3

    # ---- 來源圖 + drift 判定（fail closed）----
    src_sha = git(src, "rev-parse", "HEAD").strip()
    subs = discover(src)
    asked = {e.strip().replace("\\", "/").rstrip("/") for e in args.exclude.split(",") if e.strip()}
    unknown = asked - {s["path"] for s in subs}
    if unknown:
        print(f"✗ --exclude 有不存在的 submodule 路徑：{sorted(unknown)}\n"
              f"  （靜默忽略會讓人以為排除生效了 —— 那是最難查的一種）", file=sys.stderr)
        return 2
    excluded = cascade_exclude(subs, asked)
    included = [s for s in subs if s["path"] not in excluded]

    # 區塊職責：未 init 的 submodule 一律 fail closed
    # 物理意義：未 init = 內容不在本機，**攤不出來**。這種情況只有三種正當處理：
    #          init 它、排除它、或不要跑。**絕不可靜默跳過** ——
    #          那正是「dst 少了東西不會被發現」那一族（Tim/gura/Sirius 三方都要求擋的失效）。
    uninit = [s for s in included if s["uninitialized"]]
    if uninit:
        out.append("# 🚫 拒絕執行 — 有 submodule 未 init（內容不在本機）\n")
        for s in uninit:
            out.append(f"- `{s['path']}`")
        out.append("\n未 init 的 submodule 攤不出內容。三條路選一條："
                   "\n1. `git submodule update --init --recursive`（把內容拉下來）"
                   "\n2. 用 `--exclude` 明確排除它（排除是顯式決定，會列在報告裡）"
                   "\n3. 不要跑"
                   "\n\n**本工具不會靜默跳過** —— 少了東西的 dst 看起來跟同步成功一模一樣。")
        print("\n".join(out))
        return 4

    drift = [s for s in included if s["recorded_sha"] and s["head_sha"]
             and s["recorded_sha"] != s["head_sha"]]
    mode = args.mode
    if drift and mode is None:
        out.append("# 🚫 拒絕執行 — 父記錄與磁碟 HEAD 不一致（drift）\n")
        out.append("沒有預設值：兩種選擇各有一種**外觀成功**的失效 ——")
        out.append("「父記錄」會靜默少掉尚未 bump 的內容；「磁碟 HEAD」會靜默多一份無法回溯的內容。\n")
        out.append("| submodule | 父記錄 | 磁碟 HEAD | 差幾筆 | head 可回溯 |")
        out.append("|---|---|---|---|---|")
        for s in drift:
            n = len(git(src / s["path"], "rev-list", "--count",
                        f"{s['recorded_sha']}..{s['head_sha']}", check=False).strip() or "?")
            cnt = git(src / s["path"], "rev-list", "--count",
                      f"{s['recorded_sha']}..{s['head_sha']}", check=False).strip() or "?"
            out.append(f"| `{s['path']}` | `{s['recorded_sha'][:8]}` | `{s['head_sha'][:8]}` | "
                       f"{cnt} | {'是' if reachable(src, s, s['head_sha']) else '**否（未 push）**'} |")
        out.append("\n要跑請顯式帶 `--mode recorded` 或 `--mode head`（或先把 src 的 parent bump 做完）。")
        rep["drift"] = [s["path"] for s in drift]
        print("\n".join(out))
        return 4
    mode = mode or "recorded"

    if mode == "head":
        bad = [s for s in included if s["head_sha"] and not reachable(src, s, s["head_sha"])]
        if bad:
            out.append("# 🚫 拒絕執行 — `--mode head` 但有 SHA 無法回溯\n")
            for s in bad:
                out.append(f"- `{s['path']}` @ `{s['head_sha'][:8]}` 不在任何 remote 分支上（未 push）")
            out.append("\n不可追溯的內容不准進 dst —— 先 push，或改用 `--mode recorded`。")
            print("\n".join(out))
            return 4

    # ---- 預期檔案集合 ----
    files, skipped = source_files(src, src_sha, subs, excluded, mode)
    prev = load_manifest(manifest_path)
    prev_files = (prev or {}).get("files", {})

    # ---- LFS 索引：哪些檔的「內容雜湊」該用 sha256 而不是 git blob sha ----
    lfs = lfs_index(files)

    def expect_hash(path, info):
        """該檔落地後應有的內容雜湊（LFS 走 oid sha256，其餘走 git blob sha）。"""
        return lfs.get(path) or info["sha"]

    def actual_hash(path, data):
        return hashlib.sha256(data).hexdigest() if path in lfs else blob_sha(data)

    # ---- 與 dst 現況比對：要寫的 / 已相同的 / 本地被改過的 / 該清的 ----
    to_write, identical, conflicts = [], [], []
    for path, info in sorted(files.items()):
        target = dst / path
        if target.is_file():
            cur = actual_hash(path, target.read_bytes())
            if cur == expect_hash(path, info):
                identical.append(path)
                continue
            # 差異來源要分清：上次是我們寫的（安全覆蓋）vs 有人在 dst 改過（衝突）
            if path in prev_files and prev_files[path] == cur:
                to_write.append(path)
            else:
                conflicts.append(path)
        else:
            to_write.append(path)
    stale = sorted(set(prev_files) - set(files))

    # ---- extra：dst 有、src 沒有（--delete-extra 的對象）----
    # 區塊職責：補上 stale 結構上碰不到的那一塊。
    # 物理意義：stale 的候選集合是 **manifest**（＝「這支工具上次寫過什麼」），
    #          所以它只認得**自己造成的**遺留。使用者要的「兩邊檔案一致」是另一個問題：
    #          dst 上那些「本工具從沒寫過、但 src 也沒有」的檔（首次同步、或別人放的）
    #          永遠不會出現在 stale 裡 —— 而那正是 A,B,C,D 場景裡的 D。
    #          兩者刻意分開兩個旗標，因為**風險級別不同**：
    #          prune 刪的是自己寫過的（來源已知），delete-extra 刪的是別人的檔。
    extra = []
    extra_note = ""
    if args.delete_extra:
        try:
            tracked = dst_tracked_files(dst)
        except RuntimeError as e:
            # dst 不是 git repo（或 git 壞了）→ 沒有候選集合、也沒有還原點。
            # **fail closed**：不退回「那就不刪」——靜默降級會讓使用者以為同步過了，
            # 而 D 還躺在那裡（勾了選項卻沒有作用，是最難發現的一種壞）。
            print(f"# 🚫 中止 — --delete-extra 需要 dst 是一個 git repo（要靠它的追蹤清單決定刪誰、"
                  f"並且靠它才還原得回來）\n{e}")
            return 7
        # 排除的 submodule 底下一律不動：排除的語意是「這塊我不同步」，
        # 不是「這塊在 src 不存在」。把它算進差集 = 使用者勾了排除、結果那整個目錄被刪光。
        skip_prefixes = tuple(p.rstrip("/") + "/" for p in excluded)
        manifest_rel = None
        try:
            manifest_rel = manifest_path.relative_to(dst).as_posix()
        except ValueError:
            pass    # manifest 被 --manifest 移到 dst 之外 → 本來就不在候選集合裡
        for p in sorted(tracked - set(files)):
            if skip_prefixes and p.startswith(skip_prefixes):
                continue
            if manifest_rel and p == manifest_rel:
                continue    # 已揭露的工具產物，不列入驗證集合也不刪
            extra.append(p)
        if excluded:
            extra_note = f"（已跳過 {len(excluded)} 個排除 submodule 底下的路徑）"

    # ---- 報告 ----
    out.append(f"# 攤平同步計畫\n")
    out.append(f"- src: `{src}` @ `{src_sha[:8]}`")
    out.append(f"- dst: `{dst}`")
    out.append(f"- 模式: **{mode}**（{'父記錄的 gitlink SHA' if mode == 'recorded' else 'submodule 磁碟 HEAD'}）")
    out.append(f"- manifest: `{manifest_path}`{'（尚不存在＝首次同步）' if prev is None else ''}")
    for w in warn:
        out.append(f"- ⚠ {w}")
    out.append("")
    out.append(f"| submodule | 同步 | 使用 SHA | 檔數 |")
    out.append(f"|---|---|---|---|")
    for s in subs:
        inc = s["path"] not in excluded
        sha = (s["head_sha"] if mode == "head" else s["recorded_sha"]) if inc else ""
        n = sum(1 for p in files if p.startswith(s["path"] + "/")) if inc else 0
        out.append(f"| `{s['path']}` | {'✅' if inc else '⛔'} | `{sha[:8] if sha else '—'}` | "
                   f"{n if inc else '—'} |")
    out.append("")
    out.append(f"- 預期落地檔數: **{len(files)}**（已濾掉 gitlink {skipped['gitlinks']} 筆 / "
               f".gitmodules {skipped['gitmodules']} 筆）")
    out.append(f"- 內容已相同（不動）: {len(identical)}")
    # --force 會把衝突檔併入寫入清單 —— 那件事必須反映在**計畫階段**的數字裡。
    # 第一版只印 to_write，於是 --force 時計畫說「要寫入 0」而執行段說「寫入 1 檔」，
    # 兩行自相矛盾且計畫那行**比事實小**（名字比事實大的反向，一樣是說謊）。
    forced = len(conflicts) if args.force else 0
    out.append(f"- 要寫入: **{len(to_write) + forced}**"
               + (f"（含 --force 強制覆蓋的 {forced} 個衝突檔）" if forced else ""))
    out.append(f"- 本地被改過（衝突）: **{len(conflicts)}**"
               + ("　→ `--force` 已指定，**會被覆蓋**" if forced else ""))
    out.append(f"- 上次寫過但已不在來源（stale）: **{len(stale)}**"
               f"{'　→ 加 --prune 才會刪' if stale and not args.prune else ''}")
    if excluded:
        out.append(f"\n⛔ 排除 {len(excluded)} 個（**排除資訊只在本報告，不會在 dst 留任何檔案**）：")
        for p in sorted(excluded):
            out.append(f"  - `{p}`")
    out.append(f"\n> ℹ 只同步 **tracked** 內容 —— `.gitignore` 掉的檔不會過去（要磁碟快照請用別的工具）。")
    if conflicts:
        out.append(f"\n## ⚠ 衝突（dst 上這些檔被改過，不是上次同步寫下的內容）")
        for p in conflicts[:20]:
            out.append(f"  - `{p}`")
        if len(conflicts) > 20:
            out.append(f"  - …另 {len(conflicts) - 20} 筆")
        out.append(f"\n覆蓋它們會**弄掉 dst 上的本地修改**。確認要蓋才加 `--force`。")
    if stale:
        out.append(f"\n## 🧹 stale（上次同步寫過、這次來源已沒有）")
        for p in stale[:20]:
            out.append(f"  - `{p}`")
        if len(stale) > 20:
            out.append(f"  - …另 {len(stale) - 20} 筆")
        if prev is None:
            out.append("\n（首次同步無 manifest —— 本工具**不會**去猜 dst 上既存檔案是誰寫的，一律不刪。）")
    if args.delete_extra:
        out.append(f"\n## 🗑 extra（dst 有追蹤、src 沒有 → 兩邊一致就該刪）{extra_note}")
        if extra:
            for p in extra[:20]:
                out.append(f"  - `{p}`")
            if len(extra) > 20:
                out.append(f"  - …另 {len(extra) - 20} 筆")
            out.append(f"\n共 **{len(extra)}** 筆。這些檔在 dst 的 git 裡有紀錄，"
                       f"刪錯了可以用 dst 自己的 git 還原（本工具不動它的 git）。")
        else:
            out.append("  （無 —— dst 的追蹤內容沒有多出 src 以外的檔）")

    rep.update({"mode": mode, "src_sha": src_sha, "expected": len(files),
                "identical": len(identical), "to_write": len(to_write),
                "conflicts": conflicts, "stale": stale, "extra": extra,
                "excluded": sorted(excluded), "skipped": skipped,
                "inputs": {s["path"]: (s["head_sha"] if mode == "head" else s["recorded_sha"])
                           for s in included}})

    if not args.apply:
        out.append("\n（dry-run — 完全唯讀：src 沒被讀寫以外的動作，dst 一個檔都沒動）")
        print("\n".join(out) if args.format == "md" else json.dumps(rep, ensure_ascii=False, indent=2))
        return 0

    if conflicts and not args.force:
        out.append(f"\n# 🚫 中止 — 有 {len(conflicts)} 個檔在 dst 被改過")
        out.append("覆蓋會弄掉本地修改。檢查上面清單，確認後加 `--force`。")
        print("\n".join(out))
        return 5
    if conflicts:
        # --force 的語意是「連本地改過的也蓋」—— 必須把衝突檔真的加進寫入清單。
        # 第一版只跳過中止而沒加進 to_write → 寫 0 檔、然後驗證失敗（exit 6），
        # 而使用者看到的是「我加了 --force 它卻說同步不完整」，指不到真正原因。
        to_write.extend(conflicts)

    # ---- 寫入 ----
    out.append("\n## 執行")
    by_repo = {}
    for path, info in files.items():
        by_repo.setdefault(info["repo"], []).append((path, info["sha"]))
    written = 0
    lfs_restored = []
    for repo, items in by_repo.items():
        # cat-file --batch：一個 repo 一個 process（32k 個檔開 32k 個 process 會慢到不能用）
        need = [(p, sha) for p, sha in items if p in set(to_write)]
        if not need:
            continue
        # ⚠ **逐筆 request/response，絕不可先把所有 sha 寫進 stdin 再讀。**
        #   第一版那樣寫，4400 個 sha（~180KB）還沒寫完，git 的 stdout 就被塞滿而阻塞，
        #   而我還在寫 stdin → 兩邊互等，**整支卡死、0 檔落地、沒有任何錯誤訊息**。
        #   （同一個坑 UCL_LoginStatusPage 的註解裡寫過，我讀過還是踩了。）
        #   每筆 write + flush + 讀完該物件再送下一筆，pipe 永遠不會積壓。
        proc = subprocess.Popen(["git", "-C", repo, "cat-file", "--batch"],
                                stdin=subprocess.PIPE, stdout=subprocess.PIPE, bufsize=0)
        try:
            for p, sha in need:
                proc.stdin.write((sha + "\n").encode())
                proc.stdin.flush()
                header = proc.stdout.readline().decode()
                parts = header.split()
                if len(parts) < 3 or parts[1] != "blob":
                    raise RuntimeError(f"cat-file 回了非 blob：{header.strip()}（path={p}）")
                size = int(parts[2])
                data = b""
                while len(data) < size:                 # readinto 語意：一次可能讀不滿
                    chunk = proc.stdout.read(size - len(data))
                    if not chunk:
                        raise RuntimeError(f"cat-file 輸出中斷（path={p}）")
                    data += chunk
                proc.stdout.read(1)                     # 物件尾端換行
                # ---- LFS：blob 是指標，要換座標系再讀一次 ----
                # 2026-08-06 血證：LY 有 62 條 LFS 規則、798 個 LFS 檔（*.dll *.png *.fbx *.wav …）。
                # 沒有這段時，dst 會收到 798 張 130 bytes 的指標檔，
                # 而 Unity 只會抱怨「namespace 找不到」—— 指不到真正原因。
                ptr = lfs_pointer(data)
                if ptr is not None:
                    oid, psize = ptr
                    real = smudge(repo, p, sha)
                    # 驗證用**指標自帶的 sha256**，不是 git blob sha ——
                    # blob sha 驗的是指標本身（那必然通過，而且什麼都沒證明）。
                    got = hashlib.sha256(real).hexdigest()
                    if got != oid or len(real) != psize:
                        raise RuntimeError(
                            f"LFS 還原失敗（path={p}）：期望 sha256={oid} size={psize}，"
                            f"實得 sha256={got} size={len(real)}。"
                            f"多半是本機沒有該 LFS 物件 —— 先在來源跑 `git lfs pull`。")
                    data = real
                    lfs_restored.append(p)
                tgt = dst / p
                tgt.parent.mkdir(parents=True, exist_ok=True)
                tgt.write_bytes(data)
                written += 1
        finally:
            proc.stdin.close()
            proc.wait()
    out.append(f"  寫入 {written} 檔"
               + (f"（其中 {len(lfs_restored)} 個 LFS 檔已還原真內容，不是 130 bytes 的指標）"
                  if lfs_restored else ""))

    pruned = []
    if args.prune and prev is not None:
        for p in stale:
            f = dst / p
            if f.is_file():
                f.unlink()
                pruned.append(p)
        out.append(f"  刪除 stale {len(pruned)} 檔")
    elif stale:
        out.append(f"  stale {len(stale)} 檔未動（{'首次同步不刪' if prev is None else '未帶 --prune'}）")

    deleted_extra = []
    if args.delete_extra:
        # 這裡刪的是**別人的檔**（本工具從沒寫過），所以：
        #   · 只刪 dst 追蹤清單算出來的差集（見 dst_tracked_files 的三條理由）
        #   · 刪完順手清空目錄 —— 留一堆空資料夾在 Unity 專案裡會生出空的 .meta 提示，
        #     而「兩邊一致」這個目標本來就不該留下 src 沒有的目錄。
        for p in extra:
            f = dst / p
            if f.is_file():
                f.unlink()
                deleted_extra.append(p)
        removed_dirs = 0
        for p in deleted_extra:
            d = (dst / p).parent
            # 往上收，但**絕不越過 dst**（rglob 的父鏈往上會走出專案外）
            while d != dst and dst in d.parents:
                try:
                    next(d.iterdir())
                    break                       # 還有東西，停手
                except StopIteration:
                    d.rmdir()
                    removed_dirs += 1
                    d = d.parent
                except OSError:
                    break                       # 權限 / 佔用 —— 留著比硬刪安全
        out.append(f"  刪除 extra {len(deleted_extra)} 檔"
                   + (f"（連帶清掉 {removed_dirs} 個變空的目錄）" if removed_dirs else ""))
    elif extra:
        out.append(f"  extra {len(extra)} 檔未動（未帶 --delete-extra）")

    # ---- 全量驗證（Tim 優先序②：確保內容完全同步）----
    # 判準：dst 上每個檔的內容，用**獨立重算的 blob SHA** 對上來源 blob SHA。
    #      這裡不呼叫 git hash-object —— 用被測工具自己的雜湊驗它自己是循環論證。
    # ⚠ **驗證必須跟落地內容同一個座標系**（2026-08-06 血證）。
    #   舊版一律 `blob_sha(檔案內容) == blob sha`。對 LFS 檔那是**指標對指標** ——
    #   dst 收到 130 bytes 的假檔，而這一段照樣印「✅ 內容完全同步（逐檔位元組級一致）」。
    #   那句話在 blob 座標系是真的，在「使用者拿到什麼檔」這個座標系是徹底的謊。
    #   實害：osawari01 收到 798 張指標，Unity 只說得出「namespace 找不到」，指不到真因。
    #   教訓不是「驗得不夠細」，是**驗錯了對象**：檢查再嚴，只要它跟目標不在同一個座標系，
    #   它在結構上就永遠看不見這一類錯。
    out.append("\n## 全量驗證（逐檔獨立重算雜湊；LFS 檔比 oid sha256）")
    missing, mismatch = [], []
    for path, info in files.items():
        f = dst / path
        if not f.is_file():
            missing.append(path)
            continue
        if actual_hash(path, f.read_bytes()) != expect_hash(path, info):
            mismatch.append(path)
    out.append(f"  應有 {len(files)} 檔 / 缺 **{len(missing)}** / 內容不符 **{len(mismatch)}**"
               + (f" / 其中 LFS 檔 {len(lfs)} 個（已還原真內容，非指標）" if lfs else ""))
    for p in (missing + mismatch)[:10]:
        out.append(f"    ✗ {p}")
    ok = not missing and not mismatch
    out.append(f"  {'✅ 內容完全同步（逐檔位元組級一致）' if ok else '🚨 同步不完整 —— 見上方清單'}")
    rep["verify"] = {"missing": missing, "mismatch": mismatch, "ok": ok}

    # ---- manifest（只有驗證通過才寫；失敗的狀態不可被記成「上次同步結果」）----
    if ok:
        # 區塊職責：manifest = 「這支工具在 dst 負責的檔案」，不是「本次來源有哪些檔」
        # 物理意義：**沒被 prune 掉的 stale 必須留在 manifest 裡**，否則下一次
        #          prev_files 看不到它 → stale 變 0 → 那個檔永久變成孤兒，
        #          之後任何一次執行都不會再發現它。
        #          （實摔：測 F 第一次執行報 stale=1，第二次帶 --prune 卻刪 0 檔。
        #           因為第一次收尾就把它從 manifest 抹掉了。）
        #          少東西會被發現，多東西不會 —— 所以責任清單只能在**實際刪除後**才縮小。
        # 存的是**落地內容的雜湊**（LFS 走 oid sha256），不是 blob sha ——
        # 因為下一次比對是拿它去對 actual_hash(磁碟內容)。存 blob sha 的話，
        # 全部 LFS 檔下次都會被判成「本地被改過」而變成假衝突。
        manifest_files = {p: expect_hash(p, i) for p, i in sorted(files.items())}
        orphan_kept = []
        for p in stale:
            if p in pruned:
                continue
            if (dst / p).is_file():
                manifest_files[p] = prev_files[p]     # 保留舊紀錄：它還在 dst，還是我們的責任
                orphan_kept.append(p)
        manifest_path.parent.mkdir(parents=True, exist_ok=True)
        manifest_path.write_text(json.dumps({
            "schema": SCHEMA, "src": str(src), "src_sha": src_sha, "mode": mode,
            "excluded": sorted(excluded),
            "inputs": rep["inputs"],
            "files": manifest_files,
        }, ensure_ascii=False, indent=1), encoding="utf-8")
        if orphan_kept:
            out.append(f"  ℹ 保留 {len(orphan_kept)} 筆 stale 於 manifest（它們還在 dst，"
                       f"下次仍可 --prune 清除；抹掉紀錄會讓它們變成永久孤兒）")
        out.append(f"\n  manifest 已更新: {manifest_path}")
        out.append("  （這是本工具在 dst 唯一新增的非來源檔案，可用 --manifest 移到 dst 之外；"
                   "不列入驗證集合）")
    else:
        out.append("\n  ⚠ manifest **未更新** —— 驗證沒過的狀態不可被記成「上次同步結果」，"
                   "否則下次 prune 會照著錯的清單刪。")

    print("\n".join(out) if args.format == "md" else json.dumps(rep, ensure_ascii=False, indent=2))
    return 0 if ok else 6


if __name__ == "__main__":
    sys.exit(main())
