#!/usr/bin/env python3
"""訊息檔名 migration：舊格式 -> 全域 seq 檔名（00000001.json）。

入口：`UCL_ChatTavernAdminPage` 的「🗄 歸檔 / 檔名 migration」區塊（也可 CLI 直接跑）。
     頁面只負責帶 `--rooms-dir`（由 `UCL_ChatTavernIO.GetRoomsRoot()` 解析，**跨專案不寫死**）
     與 `--apply`；判斷與對帳全在本檔，**唯一事實來源在這裡**，不在 UI。

⚠ 跑之前務必**關閉聊天酒館系統總開關**（`UCL_ControlPanelPage`）。
  改名會動到日期目錄 mtime → C# 端 `GetSortedMessageFiles` 的指紋失效 → 重新列舉，
  而改名進行中的那個窗口排序是半舊半新的，seq 對應會暫時錯亂：
  bartender 可能對**舊訊息**誤觸發 keyword trigger（會真的發文），
  或把 `room_last_seq` 推進到錯的位置。改完之後順序與 seq 完全不變，只有「進行中」有這個窗口。


區塊職責：把「seq 靠排序位置算出來」改成「seq 直接寫在檔名上」。
物理意義：C# 端 seq = GetSortedMessageFiles() 排序後的 index+1。本工具**照那個既有順序**
         依序改名，所以排序結果與 seq 對應關係一個都不動 —— 改的是「怎麼知道 seq」，不是 seq。
數值影響：只改檔名，不碰任何檔案內容。

⚠ 排序必須與 C# 完全一致（UCL_ChatTavernIO_PerMsgFile.GetSortedMessageFiles）：
    Directory.GetFiles(root, "*.json", AllDirectories)
    key = 路徑去掉 root 前綴、'\\'→'/'，StringComparer.Ordinal 排序
  檔名與日期目錄全為 ASCII，故 Python 的 str 比較 == Ordinal（非 ASCII 會不同，這裡沒有）。
"""
import argparse, json, os, re, subprocess, sys

NEW = re.compile(r'^\d{8}\.json$')
OLD = re.compile(r'^(\d{6})_(\d+)_([0-9a-f]{6})\.json$')


def sorted_files(root):
    """複製 C# GetSortedMessageFiles 的順序。回 [(relkey, abspath)]。"""
    out = []
    for dirpath, _dirs, files in os.walk(root):
        for fn in files:
            if fn.endswith(".json"):
                p = os.path.join(dirpath, fn)
                out.append((p[len(root):].replace("\\", "/"), p))
    out.sort(key=lambda t: t[0])
    return out


def read_uuid(path):
    """讀 uuid 當身分證 —— 對帳用。utf-8-sig：資料裡真的有帶 BOM 的檔（1 筆）。"""
    try:
        return json.loads(open(path, "rb").read().decode("utf-8-sig")).get("uuid")
    except Exception as e:
        return f"<unreadable:{e}>"


def plan_room(root):
    """回 (計畫, 問題清單)。計畫 = [(seq, src_abs, dst_abs)]，只含需要改名的。"""
    files = sorted_files(root)
    plan, problems = [], []
    for i, (rel, src) in enumerate(files):
        seq = i + 1
        base = os.path.basename(src)
        dst_name = f"{seq:08d}.json"
        if base == dst_name:
            continue                       # 已是正確的新格式
        if NEW.match(base):
            # 已是新格式但位置對不上 → 我的排序模型跟 C# 不一致，**停手**，不硬改
            problems.append(f"新格式檔位置不符: {rel} 期望 {dst_name}")
            continue
        if not OLD.match(base):
            problems.append(f"無法辨識的檔名: {rel}")
            continue
        plan.append((seq, src, os.path.join(os.path.dirname(src), dst_name)))
    return files, plan, problems


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--rooms-dir", required=True)
    ap.add_argument("--apply", action="store_true", help="真的改名（預設 dry-run）")
    ap.add_argument("--git", action="store_true", help="用 git mv（保 history）")
    ap.add_argument("--only-room", default=None)
    a = ap.parse_args()

    total_plan = total_files = 0
    all_problems = []
    before = {}          # room -> {seq: uuid}
    room_plans = {}

    rooms = sorted(os.listdir(a.rooms_dir))
    if a.only_room:
        rooms = [r for r in rooms if r == a.only_room]
    for room in rooms:
        root = os.path.join(a.rooms_dir, room, "messages")
        if not os.path.isdir(root):
            continue
        files, plan, problems = plan_room(root)
        total_files += len(files)
        total_plan += len(plan)
        room_plans[room] = (root, files, plan)
        before[room] = {i + 1: read_uuid(p) for i, (_r, p) in enumerate(files)}
        for p in problems:
            all_problems.append(f"[{room}] {p}")

    print(f"房間 {len(room_plans)} / 訊息檔 {total_files} / 需改名 {total_plan}")
    if all_problems:
        print(f"\n🚫 {len(all_problems)} 個問題 —— 不執行任何改名：")
        for p in all_problems[:20]:
            print("   ", p)
        return 2
    if not a.apply:
        print("\n（dry-run — 一個檔都沒動）")
        for room, (root, files, plan) in list(room_plans.items())[:2]:
            for seq, src, dst in plan[:3]:
                print(f"   {room}: {os.path.basename(src)} -> {os.path.basename(dst)}  (seq {seq})")
        return 0

    # ---- 執行 ----
    renamed = 0
    for room, (root, files, plan) in room_plans.items():
        for seq, src, dst in plan:
            if os.path.exists(dst):
                print(f"🚫 目標已存在，中止：{dst}")
                return 3
            if a.git:
                r = subprocess.run(["git", "mv", src, dst], capture_output=True, text=True)
                if r.returncode != 0:
                    print(f"🚫 git mv 失敗：{src}\n{r.stderr.strip()}")
                    return 4
            else:
                os.rename(src, dst)
            renamed += 1
    print(f"✅ 改名 {renamed} 檔")

    # ---- 對帳：seq -> uuid 必須逐筆不變 ----
    bad = 0
    for room, (root, files, _plan) in room_plans.items():
        after_files = sorted_files(root)
        if len(after_files) != len(files):
            print(f"🚫 [{room}] 檔數變了：{len(files)} -> {len(after_files)}")
            bad += 1
            continue
        for i, (_rel, p) in enumerate(after_files):
            seq = i + 1
            if read_uuid(p) != before[room][seq]:
                print(f"🚫 [{room}] seq {seq} 對到不同訊息：{before[room][seq]} -> {read_uuid(p)}")
                bad += 1
                if bad > 10:
                    return 5
        # 檔名是否等於 seq
        for i, (_rel, p) in enumerate(after_files):
            if os.path.basename(p) != f"{i+1:08d}.json":
                print(f"🚫 [{room}] 檔名不等於 seq：{p}")
                bad += 1
                break
    print("✅ 對帳通過：檔數相同、每個 seq 對到同一則訊息（uuid 逐筆一致）、檔名 == seq"
          if not bad else f"🚨 對帳失敗 {bad} 筆")
    return 0 if not bad else 6


if __name__ == "__main__":
    sys.exit(main())
