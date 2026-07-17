#!/usr/bin/env python3
"""
test_notify_discord_stable_cursor.py — T-STABLE-CURSOR 回歸測試（summit #0「能證偽的亂序重現」）

跑法：python CardGame/Assets/UCL/UCL_Core/Tools~/AgentCommands/PromptQueue/test_notify_discord_stable_cursor.py
（純 stdlib，不 POST Discord、不碰真實檔案；monkeypatch _read_room_messages 注入受控訊息）

為什麼需要這支測試（別刪）：
  Discord 漏訊息這 bug 三度騙過我們，共通點都是「state 到頂 / failures=0」這個指標本身在說謊——
  靠「跑起來沒漏」永遠驗不出 silent drop。這支測試「故意製造亂序落地」：注入一筆檔名排進浮水印
  以下、但在浮水印推進「之後」才落地的訊息，斷言 mirror 仍會把它撈出來送（fresh），而不是當
  「seq <= last_seen」永久跳過。外觀 OK ≠ 真的 OK — 這測試能過，修才算數。
"""
import importlib.util
import pathlib
import sys

# ---- 載入受測模組（notify_discord.py，本檔同目錄）----
_HERE = pathlib.Path(__file__).resolve().parent
_MOD_PATH = _HERE / "notify_discord.py"
_spec = importlib.util.spec_from_file_location("notify_discord_under_test", str(_MOD_PATH))
nd = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(nd)

_failures = []


def _check(cond, label):
    print(("  PASS " if cond else "  FAIL ") + label)
    if not cond:
        _failures.append(label)


# ISO UTC 時間戳（可字典序比較）
T1 = "2026-07-17T10:00:00.000Z"
T1_5 = "2026-07-17T10:00:00.500Z"   # 夾在 T1、T2 中間（亂序晚到的那筆）
T2 = "2026-07-17T10:00:01.000Z"
T_ANCIENT = "2026-07-17T09:00:00.000Z"   # 早於 (T2 - 600s) 下界 = 古老歷史
T_NEW = "2026-07-17T10:05:00.000Z"       # 晚於 T2 = 全新


def test_dedup_status_pure():
    print("[1] _msg_dedup_status 純函式")
    low = nd._iso_minus_seconds(T2, nd._STABLE_CURSOR_SKEW_WINDOW_SEC)  # T2 - 600s
    seen = {"a": T1, "b": T2}
    _check(nd._msg_dedup_status("a", T1, seen, low) == "seen", "已送過的 uuid → seen")
    _check(nd._msg_dedup_status("late", T1_5, seen, low) == "fresh",
           "窗內、未送過、亂序晚到 → fresh（就是這筆以前會被 silent drop）")
    _check(nd._msg_dedup_status("old", T_ANCIENT, seen, low) == "too_old",
           "未送過但早於下界 → too_old（不回放古老歷史）")
    _check(nd._msg_dedup_status("new", T_NEW, seen, low) == "fresh", "全新 ts → fresh")
    _check(nd._msg_dedup_status("x", T1, {}, "") == "fresh", "無下界 + 空 seen → fresh")


def _msg(uuid, ts, seq, body="x"):
    return {"uuid": uuid, "ts": ts, "seq": seq, "kind": "chat",
            "sender_id": "tester", "sender_name": "tester", "body": body, "meta": {}}


def test_out_of_order_not_dropped():
    print("[2] 亂序重現：晚落地、排進浮水印以下的訊息不被跳過（核心回歸）")
    room = "testroom"
    tm_config = {
        "rooms": [room], "kinds": ["chat"],
        "exclude_senders": [], "include_senders": [],
        "exclude_meta_source": [], "exclude_sender_prefix": [], "max_per_run": 20,
    }
    # 模擬「已送過 A、B」的 state（ts_high=T2，seen={a,b}）——等同前一輪跑完
    state = {"rooms": {room: {"last_seen_seq": 2, "ts_high": T2, "seen_uuids": {"a": T1, "b": T2}}}}

    # 亂序：L(uuid l, ts T1.5) 晚落地，檔名排在 A、B 之間 → derived 位置把 B 從 seq2 擠到 seq3、L 佔 seq2
    # 舊邏輯 (seq <= last_seen=2)：L(seq2) 會被跳過 = silent drop；B(seq3) 會被重收 = 重送
    reordered = [_msg("a", T1, 1), _msg("l", T1_5, 2, body="LATE"), _msg("b", T2, 3)]
    nd._read_room_messages = lambda r: reordered
    nd._read_room_meta = lambda r: {}

    out = nd._collect_new_tavern_messages(tm_config, state)
    got_uuids = [m.get("uuid") for _r, m in out]
    _check(got_uuids == ["l"],
           f"只撈出亂序晚到的 L、A/B 不重送（期望 ['l']，實得 {got_uuids}）")

    # 對照：證明舊 position-seq 邏輯會出錯（L 掉、B 重送）——非斷言，僅印出反差供人看
    last_seen = 2
    old_logic = [m.get("uuid") for m in reordered if m.get("seq", 0) > last_seen]
    print(f"    （對照）舊 seq<=last_seen 邏輯會撈：{old_logic} → L 被丟、B 被重送")


def test_ancient_not_replayed_new_sent():
    print("[3] 古老歷史不回放、全新訊息會送")
    room = "testroom"
    tm_config = {"rooms": [room], "kinds": ["chat"], "exclude_senders": [], "include_senders": [],
                 "exclude_meta_source": [], "exclude_sender_prefix": [], "max_per_run": 20}
    state = {"rooms": {room: {"last_seen_seq": 2, "ts_high": T2, "seen_uuids": {"a": T1, "b": T2}}}}
    msgs = [_msg("ancient", T_ANCIENT, 1), _msg("a", T1, 2), _msg("b", T2, 3), _msg("new", T_NEW, 4)]
    nd._read_room_messages = lambda r: msgs
    nd._read_room_meta = lambda r: {}
    out = nd._collect_new_tavern_messages(tm_config, state)
    got = [m.get("uuid") for _r, m in out]
    _check(got == ["new"], f"只送全新 new、不回放 ancient/不重送 a,b（期望 ['new']，實得 {got}）")


def test_record_and_prune():
    print("[4] _record_sent_message 記 uuid + 推進 ts_high + 剪枝")
    rs = {"last_seen_seq": 2, "ts_high": T2, "seen_uuids": {"a": T1, "b": T2}}
    nd._record_sent_message(rs, _msg("new", T_NEW, 4))
    _check("new" in rs["seen_uuids"], "新 uuid 記進 seen_uuids")
    _check(rs["ts_high"] == T_NEW, "ts_high 推進到最新 ts")
    _check(rs["last_seen_seq"] == 4, "last_seen_seq 更新（AdminPage 顯示用）")
    # T_NEW - 600s 下界會不會剪掉 T1(10:00:00)? T_NEW=10:05 → 下界=09:55 → T1(10:00)>09:55 保留；T_ANCIENT 會被剪
    rs2 = {"last_seen_seq": 2, "ts_high": T2, "seen_uuids": {"ancient": T_ANCIENT, "b": T2}}
    nd._record_sent_message(rs2, _msg("new", T_NEW, 5))
    _check("ancient" not in rs2["seen_uuids"], "早於下界的舊 uuid 被剪枝（防無界成長）")
    _check("b" in rs2["seen_uuids"] and "new" in rs2["seen_uuids"], "窗內 uuid 保留")


def test_migration_preserves_pending():
    print("[5] 舊 schema 遷移：保留 pending（不吞未送訊息）、不回放已送")
    # 舊 state：last_seen_seq=2（position 浮水印），無 ts_high/seen_uuids
    # msgs：4 筆，seq1-2 已送、seq3-4 是 pending（舊邏輯還沒送到）
    msgs = [_msg("a", T1, 1), _msg("b", T2, 2), _msg("c", T_NEW, 3), _msg("d", "2026-07-17T10:06:00.000Z", 4)]
    rs = nd._seed_room_baseline(msgs, sent_up_to_seq=2)
    _check("a" in rs["seen_uuids"] and "b" in rs["seen_uuids"], "seq<=2 的已送訊息標記為 seen")
    _check("c" not in rs["seen_uuids"] and "d" not in rs["seen_uuids"],
           "seq>2 的 pending 未送訊息不標 seen（遷移後會補送，不吞）")
    _check(rs["ts_high"] == T2, "ts_high = 已送段的最大 ts（不含 pending）")
    # 反面：首次 baseline（sent_up_to_seq=None）應把全部歷史視為已送、pending=無
    rs0 = nd._seed_room_baseline(msgs)
    _check(all(u in rs0["seen_uuids"] or (rs0["ts_high"] and m_ts <= nd._iso_minus_seconds(rs0["ts_high"], nd._STABLE_CURSOR_SKEW_WINDOW_SEC))
               for u, m_ts in [("c", T_NEW), ("d", "2026-07-17T10:06:00.000Z")]),
           "首次 baseline 全歷史視為已送（不回放）")


def main():
    print("=== T-STABLE-CURSOR 回歸測試 ===")
    test_dedup_status_pure()
    test_out_of_order_not_dropped()
    test_ancient_not_replayed_new_sent()
    test_record_and_prune()
    test_migration_preserves_pending()
    print()
    if _failures:
        print(f"❌ {len(_failures)} 個斷言失敗：")
        for f in _failures:
            print("   - " + f)
        return 1
    print("✅ 全部通過")
    return 0


if __name__ == "__main__":
    sys.exit(main())
