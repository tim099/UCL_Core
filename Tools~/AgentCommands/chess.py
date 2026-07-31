#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
chess.py — 自由時間「下棋」活動 (西洋棋第一本 RuleBook)。

設計藍本: docs/Design/Chess_Activity_RuleBook.md (v0.3, summit 2026-06-14 拍板)。
哲學 (Tim + kiara + basecamp 共識):
  - 自律遵守, 非引擎硬 reject: move 預設信任套用; 非法步只警示不擋手 (lint), 全程可事後複驗。
  - FEN = 唯一真相; 棋盤圖是 FEN 的純函數 (渲染永不漂移)。保留前一手 FEN → 單手 O(1) 可驗。
  - 每步廣播帶三元組 (prior_FEN → UCI_move → result_FEN) + 字母版盤, 人人可複驗。
  - 對局 index 從 0 自增, 每局獨立狀態檔。
  - 獎勵繪圖券綁 persona (跟 ucl-canvas 共用同一份餘額 ledger): 勝+10 / 敗+5 / 和雙方各+5 / solo 一人拿滿。

純 stdlib (無 pip 依賴, 跟 canvas.py / library.py 一致)。

規則書 (RuleBook): 隨 code 放 UCL_Core/Tools~/AgentCommands/rulebooks/<ruleid>.yaml (跨專案共用 spec);
  reward/symbols/board 資料驅動 (有 pyyaml 就讀, 無則內建 fallback)。runtime 對局狀態留主專案
  AgentCommands/Chess/games/ (per-project), 繪圖券跟 ucl-canvas 共用 AgentCommands/Canvas/vouchers/。

子指令 (start/join/move/resign/draw 皆可帶 --say "<一句話>": 自言自語或跟對手聊天):
  start   開新局 (--persona / --side white|black|both / --vs-open 留座等人 / --say)
  lobby   列出『等待加入』的對局 (OPEN 座或可中途切入的 solo)
  join    加入 (idx --persona [--side]): 認領 OPEN 座, 或中途切入 solo 局轉 1v1
  release 中途釋出一座 → OPEN 等人加入 (idx --persona [--side])
  move    走子 (idx <uci> --persona [--say])  e2e4 / e7e8q(升變) / e1g1(易位)
  board   印當前盤面 (idx)
  resign  認輸 (idx --persona [--say])
  draw    提和 / 接受和 (idx --persona [--accept] [--say])
  list    列出對局
範例:
  python chess.py start --persona summit --side white --vs-open --say "誰來陪我下一盤?"
  python chess.py lobby
  python chess.py join 0 --persona kiara --say "我來會會你"
  python chess.py move 0 e2e4 --persona summit --say "經典開局, 先佔中路"
"""

import argparse
import datetime
import io
import json
import os
import subprocess
import sys
import uuid
from pathlib import Path

# ───────────────────────── 路徑解析 ─────────────────────────
# 區塊職責: 解析 repo root → chess 狀態目錄 (主專案側) + 繪圖券 ledger (跟 canvas 共用)。
# 物理意義: 本檔在 UCL_Core/Tools~/AgentCommands/ (跨專案共用 code); 狀態落主專案 AgentCommands/。
_THIS = Path(__file__).resolve()


def find_repo_root() -> Path:
    """從本檔往上找含 AgentCommands/ 的 repo root。"""
    p = _THIS
    for _ in range(12):
        p = p.parent
        if (p / "AgentCommands").is_dir() and (p / "CardGame").is_dir():
            return p
    # fallback: 由已知層級 (…/CardGame/Assets/UCL/UCL_Core/Tools~/AgentCommands/chess.py)
    return _THIS.parents[6]


_REPO = find_repo_root()
_CHESS_DIR = _REPO / "AgentCommands" / "Chess"
_GAMES_DIR = _CHESS_DIR / "games"                                # runtime 對局狀態 (per-project, 留主專案)
_VOUCHER_DIR = _REPO / "AgentCommands" / "Canvas" / "vouchers"   # 跟 canvas 共用券餘額 (per-project)
_RUN_CMD = _THIS.parent / "run_cmd.py"                            # 同目錄, 廣播用
_RULEBOOK_DIR = _THIS.parent / "rulebooks"                       # 規則書 spec (跨專案共用, 隨 code 放 UCL_Core)


def utcnow_iso() -> str:
    return datetime.datetime.utcnow().strftime("%Y-%m-%dT%H:%M:%S.") + \
        f"{datetime.datetime.utcnow().microsecond // 1000:03d}Z"


def short_uuid() -> str:
    return uuid.uuid4().hex[:6]


# ───────────────────────── 規則書 (RuleBook) 載入 ─────────────────────────
# 區塊職責: 從 UCL_Core 內的 rulebooks/<ruleid>.yaml 讀「可資料驅動的元資料」
#   — reward(發券數)/symbols(glyph legend)/name/board 尺寸。引擎走子規則仍寫死求正確
#   (per 規則書 MVP 註); 本層做的是「經濟與顯示資料化」, 之後擴新棋類改 yaml 即可。
# 哲學: 跨專案 robust — 有 pyyaml 就讀 yaml; 沒有就用內建 fallback, 純 stdlib 仍正確跑。
_DEFAULT_RULEBOOK = {
    "ruleid": "chess", "name": "西洋棋 / Chess",
    "board": {"width": 8, "height": 8}, "empty_cell": ".",
    "reward": {"win": 10, "lose": 5, "draw": 5},
    "symbols": [
        {"id": "K", "letter": ["K", "k"], "glyph": ["♔", "♚"]},
        {"id": "Q", "letter": ["Q", "q"], "glyph": ["♕", "♛"]},
        {"id": "R", "letter": ["R", "r"], "glyph": ["♖", "♜"]},
        {"id": "B", "letter": ["B", "b"], "glyph": ["♗", "♝"]},
        {"id": "N", "letter": ["N", "n"], "glyph": ["♘", "♞"]},
        {"id": "P", "letter": ["P", "p"], "glyph": ["♙", "♟"]},
    ],
}


def load_rulebook(ruleid="chess"):
    """讀 rulebooks/<ruleid>.yaml; 缺檔或無 pyyaml → 回內建 fallback。淺合併確保關鍵欄位齊全。"""
    rb = dict(_DEFAULT_RULEBOOK)
    fpath = _RULEBOOK_DIR / f"{ruleid}.yaml"
    try:
        import yaml  # 非硬依賴: 沒裝就走 fallback
        if fpath.exists():
            loaded = yaml.safe_load(fpath.read_text(encoding="utf-8")) or {}
            for k, v in loaded.items():
                rb[k] = v
    except Exception:
        pass  # 解析失敗保底用 fallback, 不擋主流程
    return rb


RULEBOOK = load_rulebook("chess")


# ═════════════════════════ 引擎 (T01) ═════════════════════════
# 區塊職責: 西洋棋規則核心。盤面 = 64 格 list, index = rank*8 + file。
#   a1=0, b1=1, …, h1=7, a2=8, …, h8=63。白棋大寫 / 黑棋小寫 / '.' 空格。
#   白往 rank 增加方向走。FEN rank 由 8 排到 1。
START_FEN = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1"

KNIGHT_OFFS = [(1, 2), (2, 1), (2, -1), (1, -2), (-1, -2), (-2, -1), (-2, 1), (-1, 2)]
KING_OFFS = [(1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1)]
BISHOP_DIRS = [(1, 1), (1, -1), (-1, 1), (-1, -1)]
ROOK_DIRS = [(1, 0), (-1, 0), (0, 1), (0, -1)]


def sq(f, r):
    return r * 8 + f


def fr(i):
    return i % 8, i // 8


def alg(i):
    f, r = fr(i)
    return "abcdefgh"[f] + str(r + 1)


def alg_to_idx(s):
    return sq("abcdefgh".index(s[0]), int(s[1]) - 1)


def is_white(p):
    return p != "." and p.isupper()


def is_black(p):
    return p != "." and p.islower()


def parse_fen(fen):
    """FEN → state dict {board(list[64]), turn('w'/'b'), castling, ep(idx or -1), half, full}。"""
    parts = fen.strip().split()
    rows = parts[0].split("/")
    board = ["."] * 64
    for ri, row in enumerate(rows):           # rows[0] = rank 8
        r = 7 - ri
        f = 0
        for ch in row:
            if ch.isdigit():
                f += int(ch)
            else:
                board[sq(f, r)] = ch
                f += 1
    turn = parts[1] if len(parts) > 1 else "w"
    castling = parts[2] if len(parts) > 2 else "-"
    ep = alg_to_idx(parts[3]) if len(parts) > 3 and parts[3] != "-" else -1
    half = int(parts[4]) if len(parts) > 4 else 0
    full = int(parts[5]) if len(parts) > 5 else 1
    return {"board": board, "turn": turn, "castling": castling, "ep": ep, "half": half, "full": full}


def serialize_fen(st):
    rows = []
    for r in range(7, -1, -1):
        row = ""
        empty = 0
        for f in range(8):
            p = st["board"][sq(f, r)]
            if p == ".":
                empty += 1
            else:
                if empty:
                    row += str(empty)
                    empty = 0
                row += p
        if empty:
            row += str(empty)
        rows.append(row)
    ep = alg(st["ep"]) if st["ep"] >= 0 else "-"
    return f"{'/'.join(rows)} {st['turn']} {st['castling']} {ep} {st['half']} {st['full']}"


def position_key(st):
    """重複判定用 key: 子力擺放 + 輪誰 + 易位權 + 過路兵 (不含 half/full)。"""
    return " ".join(serialize_fen(st).split()[:4])


def is_square_attacked(board, target, by_white):
    """(target) 是否被 by_white 方任一子攻擊。"""
    tf, tr = fr(target)
    # 兵
    pr = -1 if by_white else 1          # 攻擊者兵相對防守格在哪個 rank 偏移
    for df in (-1, 1):
        f, r = tf + df, tr + pr
        if 0 <= f < 8 and 0 <= r < 8:
            p = board[sq(f, r)]
            if p == ("P" if by_white else "p"):
                return True
    # 馬
    for df, dr in KNIGHT_OFFS:
        f, r = tf + df, tr + dr
        if 0 <= f < 8 and 0 <= r < 8 and board[sq(f, r)] == ("N" if by_white else "n"):
            return True
    # 王
    for df, dr in KING_OFFS:
        f, r = tf + df, tr + dr
        if 0 <= f < 8 and 0 <= r < 8 and board[sq(f, r)] == ("K" if by_white else "k"):
            return True
    # 滑行子 (象/車/后)
    for dirs, pieces in ((BISHOP_DIRS, "BQ"), (ROOK_DIRS, "RQ")):
        want = pieces if by_white else pieces.lower()
        for df, dr in dirs:
            f, r = tf + df, tr + dr
            while 0 <= f < 8 and 0 <= r < 8:
                p = board[sq(f, r)]
                if p != ".":
                    if p in want:
                        return True
                    break
                f += df
                r += dr
    return False


def king_idx(board, white):
    k = "K" if white else "k"
    for i in range(64):
        if board[i] == k:
            return i
    return -1


def in_check(st, white):
    ki = king_idx(st["board"], white)
    return ki >= 0 and is_square_attacked(st["board"], ki, not white)


def _pseudo_moves(st):
    """產生輪到方的偽合法步 (尚未過濾自將), 回 UCI list。"""
    board = st["board"]
    white = st["turn"] == "w"
    own = is_white if white else is_black
    enemy = is_black if white else is_white
    moves = []
    for i in range(64):
        p = board[i]
        if p == "." or not own(p):
            continue
        f, r = fr(i)
        u = p.upper()
        if u == "P":
            fwd = 1 if white else -1
            start_rank = 1 if white else 6
            last_rank = 7 if white else 0
            # 前進一格
            r1 = r + fwd
            if 0 <= r1 < 8 and board[sq(f, r1)] == ".":
                _add_pawn(moves, i, sq(f, r1), r1 == last_rank)
                # 前進兩格
                if r == start_rank and board[sq(f, r + 2 * fwd)] == ".":
                    moves.append(alg(i) + alg(sq(f, r + 2 * fwd)))
            # 斜吃 (含過路兵)
            for df in (-1, 1):
                f2 = f + df
                if 0 <= f2 < 8 and 0 <= r1 < 8:
                    t = sq(f2, r1)
                    if enemy(board[t]) or t == st["ep"]:
                        _add_pawn(moves, i, t, r1 == last_rank)
        elif u == "N":
            for df, dr in KNIGHT_OFFS:
                f2, r2 = f + df, r + dr
                if 0 <= f2 < 8 and 0 <= r2 < 8 and not own(board[sq(f2, r2)]):
                    moves.append(alg(i) + alg(sq(f2, r2)))
        elif u == "K":
            for df, dr in KING_OFFS:
                f2, r2 = f + df, r + dr
                if 0 <= f2 < 8 and 0 <= r2 < 8 and not own(board[sq(f2, r2)]):
                    moves.append(alg(i) + alg(sq(f2, r2)))
            _castle_moves(st, moves, white)
        else:
            dirs = BISHOP_DIRS if u == "B" else ROOK_DIRS if u == "R" else BISHOP_DIRS + ROOK_DIRS
            for df, dr in dirs:
                f2, r2 = f + df, r + dr
                while 0 <= f2 < 8 and 0 <= r2 < 8:
                    t = sq(f2, r2)
                    if board[t] == ".":
                        moves.append(alg(i) + alg(t))
                    else:
                        if enemy(board[t]):
                            moves.append(alg(i) + alg(t))
                        break
                    f2 += df
                    r2 += dr
    return moves


def _add_pawn(moves, frm, to, promo):
    if promo:
        for pc in "qrbn":
            moves.append(alg(frm) + alg(to) + pc)
    else:
        moves.append(alg(frm) + alg(to))


def _castle_moves(st, moves, white):
    board = st["board"]
    rights = st["castling"]
    rank = 0 if white else 7
    ke = sq(4, rank)
    if board[ke] != ("K" if white else "k"):
        return
    if in_check(st, white):
        return
    kside = "K" if white else "k"
    qside = "Q" if white else "q"
    # 王翼: f,g 空 + e,f,g 不被攻擊 + h 角有車
    if kside in rights and board[sq(5, rank)] == "." and board[sq(6, rank)] == "." \
            and board[sq(7, rank)] == ("R" if white else "r"):
        if not is_square_attacked(board, sq(5, rank), not white) \
                and not is_square_attacked(board, sq(6, rank), not white):
            moves.append(alg(ke) + alg(sq(6, rank)))
    # 后翼: b,c,d 空 + e,d,c 不被攻擊 + a 角有車
    if qside in rights and board[sq(1, rank)] == "." and board[sq(2, rank)] == "." \
            and board[sq(3, rank)] == "." and board[sq(0, rank)] == ("R" if white else "r"):
        if not is_square_attacked(board, sq(3, rank), not white) \
                and not is_square_attacked(board, sq(2, rank), not white):
            moves.append(alg(ke) + alg(sq(2, rank)))


def apply_move(st, uci):
    """套用一手 (pattern-based, 信任出手): 回新 state。不做合法性 reject —
       易位/過路兵/升變靠 pattern 偵測自動處理; 非法步也照 relocate (autonomous)。"""
    import copy
    ns = copy.deepcopy(st)
    b = ns["board"]
    frm = alg_to_idx(uci[0:2])
    to = alg_to_idx(uci[2:4])
    promo = uci[4].lower() if len(uci) >= 5 else ""
    white = st["turn"] == "w"
    p = b[frm]
    u = p.upper() if p != "." else ""
    ff, fr_ = fr(frm)
    tf, tr_ = fr(to)
    captured = b[to] != "."

    is_castle = u == "K" and abs(tf - ff) == 2
    is_ep = u == "P" and tf != ff and b[to] == "." and to == st["ep"]
    last_rank = 7 if white else 0
    is_promo = u == "P" and tr_ == last_rank

    # 主移動
    b[frm] = "."
    if is_promo:
        pc = (promo or "q")
        b[to] = pc.upper() if white else pc.lower()
    else:
        b[to] = p
    # 過路兵: 移除被吃的兵 (與 to 同 file、與 from 同 rank)
    if is_ep:
        b[sq(tf, fr_)] = "."
        captured = True
    # 易位: 同步移車
    if is_castle:
        rank = 0 if white else 7
        if tf == 6:           # 王翼: h→f
            b[sq(5, rank)] = b[sq(7, rank)]
            b[sq(7, rank)] = "."
        elif tf == 2:         # 后翼: a→d
            b[sq(3, rank)] = b[sq(0, rank)]
            b[sq(0, rank)] = "."

    # 更新易位權
    rights = set(ns["castling"]) - {"-"}
    if u == "K":
        rights -= {"K", "Q"} if white else {"k", "q"}
    if u == "R":
        if frm == sq(0, 0):
            rights.discard("Q")
        elif frm == sq(7, 0):
            rights.discard("K")
        elif frm == sq(0, 7):
            rights.discard("q")
        elif frm == sq(7, 7):
            rights.discard("k")
    # 角上的車被吃 → 對方失去該側權
    for cidx, cr in ((sq(0, 0), "Q"), (sq(7, 0), "K"), (sq(0, 7), "q"), (sq(7, 7), "k")):
        if to == cidx:
            rights.discard(cr)
    ns["castling"] = "".join(c for c in "KQkq" if c in rights) or "-"

    # 過路兵目標 (對方可吃的格)
    if u == "P" and abs(tr_ - fr_) == 2:
        ns["ep"] = sq(ff, (fr_ + tr_) // 2)
    else:
        ns["ep"] = -1

    # 半回合 (50 步規則): 兵動或吃子歸零, 否則 +1
    ns["half"] = 0 if (u == "P" or captured) else st["half"] + 1
    if st["turn"] == "b":
        ns["full"] = st["full"] + 1
    ns["turn"] = "b" if white else "w"
    return ns


def legal_moves(st):
    """過濾掉走完自己王被將的偽合法步。"""
    white = st["turn"] == "w"
    out = []
    for m in _pseudo_moves(st):
        ns = apply_move(st, m)
        if not in_check(ns, white):     # 走完後「我方」王不能被將
            out.append(m)
    return out


def insufficient_material(board):
    """子力不足判和 (常見子集): K vs K / K+minor vs K / K+B vs K+B 同色格象。"""
    pieces = [p for p in board if p != "."]
    non_king = [p.upper() for p in pieces if p.upper() != "K"]
    if not non_king:
        return True
    if len(non_king) == 1 and non_king[0] in ("B", "N"):
        return True
    if all(p == "B" for p in non_king) and len(non_king) <= 2:
        # 兩象同色格才必和; 保守起見只在雙方各一象同色時判和
        bsq = [i for i, p in enumerate(board) if p in "Bb"]
        if len(bsq) == 2:
            c0 = (fr(bsq[0])[0] + fr(bsq[0])[1]) % 2
            c1 = (fr(bsq[1])[0] + fr(bsq[1])[1]) % 2
            return c0 == c1
    return False


def result_status(st, rep_count=0):
    """回 (status, detail)。status ∈ in_progress / checkmate / stalemate / draw。"""
    legal = legal_moves(st)
    white = st["turn"] == "w"
    checked = in_check(st, white)
    if not legal:
        if checked:
            return ("checkmate", "white" if not white else "black")   # 輪到方被將死 → 對手贏
        return ("stalemate", "")
    if st["half"] >= 100:
        return ("draw", "fifty_move")
    if insufficient_material(st["board"]):
        return ("draw", "insufficient_material")
    if rep_count >= 3:
        return ("draw", "threefold_repetition")
    return ("in_progress", "checkmate" if False else ("check" if checked else ""))


# ═════════════════════════ 渲染 (T04) ═════════════════════════
# 區塊職責: 字母版棋盤 — 大寫白 / 小寫黑 / '.' 空格, 含座標 a-h·1-8, 標 last move。
#   跨字型保證不歪 (ASCII 字母永遠 1 格寬, 不會 emoji fallback)。包 code block 由 caller 處理。
GLYPH_LEGEND = "K/k=王 Q/q=后 R/r=車 B/b=象 N/n=馬 P/p=兵 (大寫=白 小寫=黑) .=空格"


def render_board(st, last_move=""):
    b = st["board"]
    lines = ["  a b c d e f g h"]
    for r in range(7, -1, -1):
        row = [str(r + 1)]
        for f in range(8):
            row.append(b[sq(f, r)])
        lines.append(" ".join(row))
    out = "\n".join(lines)
    if last_move:
        out += f"\nlast: {last_move}"
    return out


# ═════════════════════════ 狀態存檔 (T03) ═════════════════════════
def ensure_dirs():
    _GAMES_DIR.mkdir(parents=True, exist_ok=True)
    _VOUCHER_DIR.mkdir(parents=True, exist_ok=True)


def next_index():
    """對局 index 從 0 自增 = 現有最大檔名 +1。"""
    ensure_dirs()
    idxs = []
    for fpath in _GAMES_DIR.glob("*.json"):
        try:
            idxs.append(int(fpath.stem))
        except ValueError:
            continue
    return (max(idxs) + 1) if idxs else 0


def game_path(idx):
    return _GAMES_DIR / f"{idx}.json"


def load_game(idx):
    p = game_path(idx)
    if not p.exists():
        raise SystemExit(f"❌ 對局 #{idx} 不存在")
    return json.loads(p.read_text(encoding="utf-8"))


def save_game(g):
    ensure_dirs()
    p = game_path(g["index"])
    tmp = p.with_suffix(".json.tmp")
    tmp.write_text(json.dumps(g, ensure_ascii=False, indent=2), encoding="utf-8")
    os.replace(tmp, p)


def new_game(idx, white_seat, black_seat, mode):
    return {
        "index": idx,
        "ruleid": "chess",
        "mode": mode,                      # solo | open | versus
        "seats": {"white": white_seat, "black": black_seat},   # persona 或 None(OPEN)
        "fen": START_FEN,
        "prior_fen": "",
        "last_move": "",
        "history": [],                     # [{n, uci, by, prior_fen, result_fen, ts}]
        "repetition": {position_key(parse_fen(START_FEN)): 1},
        "status": "in_progress",           # in_progress | checkmate | stalemate | draw | resigned
        "result": "",                      # white | black | draw
        "draw_offer": "",                  # 提和方 persona
        "created": utcnow_iso(),
        "updated": utcnow_iso(),
    }


def append_history(g, uci, by, prior_fen, result_fen, say=""):
    """統一寫一筆 history (move/resign/draw/join 共用)。say 為該動作附帶的一句話 (選填)。"""
    entry = {"n": len(g["history"]) + 1, "uci": uci, "by": by,
             "prior_fen": prior_fen, "result_fen": result_fen, "ts": utcnow_iso()}
    if say:
        entry["say"] = say
    g["history"].append(entry)


# ═════════════════════════ 繪圖券發放 (T06) ═════════════════════════
# 區塊職責: 繪圖券綁 persona, 跟 ucl-canvas 共用同一份 ledger (AgentCommands/Canvas/vouchers/<persona>.json)。
#   格式: {persona, balance, history:[{ts,uuid,type,amount,source,ref}]}。
def grant_voucher(persona, amount, source, ref):
    if not persona or amount <= 0:
        return 0
    ensure_dirs()
    p = _VOUCHER_DIR / f"{persona}.json"
    if p.exists():
        data = json.loads(p.read_text(encoding="utf-8"))
    else:
        data = {"persona": persona, "balance": 0, "history": []}
    data["balance"] = data.get("balance", 0) + amount
    data.setdefault("history", []).append({
        "ts": utcnow_iso(), "uuid": short_uuid(), "type": "grant",
        "amount": amount, "source": source, "ref": ref,
    })
    tmp = p.with_suffix(".json.tmp")
    tmp.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
    os.replace(tmp, p)
    return data["balance"]


# 發券數資料驅動: 取自 rulebook.reward (缺值補預設)。擴新棋類改 yaml 即可調獎勵。
_RW = RULEBOOK.get("reward") or {}
REWARD = {"win": _RW.get("win", 10), "lose": _RW.get("lose", 5), "draw": _RW.get("draw", 5)}


def settle_rewards(g):
    """依結果發券; 回發放明細 list[(persona, amount, ref)]。
       綁 persona; solo (兩座同一人) → 一人收兩座的份 (滿 15 或和 10)。"""
    seats = g["seats"]
    w, b = seats["white"], seats["black"]
    res = g["result"]
    grants = []
    if res == "white":
        plan = [(w, "win"), (b, "lose")]
    elif res == "black":
        plan = [(b, "win"), (w, "lose")]
    elif res == "draw":
        plan = [(w, "draw"), (b, "draw")]
    else:
        return grants
    for persona, kind in plan:
        if persona:
            amt = REWARD[kind]
            bal = grant_voucher(persona, amt, "chess_reward", f"chess#{g['index']}:{kind}")
            grants.append((persona, amt, kind, bal))
    return grants


# ═════════════════════════ 廣播酒館 (T05) ═════════════════════════
# 區塊職責: 每步把 board + 三元組 (prior_FEN→move→result_FEN) 廣播酒館 (best-effort, 走 run_cmd op=post)。
#   tag=chess 可篩; mirror 自動回 Discord。Editor 不在/失敗 → 吞掉不擋主流程。
def broadcast(g, header, sender_persona, say=""):
    if not _RUN_CMD.exists():
        return False
    st = parse_fen(g["fen"])
    # 一句話 (自言自語 / 跟對手聊天): 有則插在盤面前, 給觀眾人味
    say_line = f"💬 {sender_persona or '?'}：{say}\n" if say else ""
    seat_w = g["seats"]["white"] or "OPEN(待加入)"
    seat_b = g["seats"]["black"] or "OPEN(待加入)"
    body = (
        f"♟️ {RULEBOOK.get('name', 'Chess')} #{g['index']} — {header}\n"
        f"{say_line}"
        f"白:{seat_w} ⚔ 黑:{seat_b} | "
        f"輪:{'白' if st['turn'] == 'w' else '黑'} | status:{g['status']}\n"
        "```\n" + render_board(st, g["last_move"]) + "\n```\n"
        f"prior_FEN: {g['prior_fen'] or '(開局)'}\n"
        f"result_FEN: {g['fen']}\n"
        f"({GLYPH_LEGEND})"
    )
    meta = json.dumps({"tag": "chess", "category": "chat", "game": g["index"]})
    sender = sender_persona or "chess-system"
    # 身分＝下棋的 persona，通道＝棋局編號（Tim 2026-08-01 persona 資料夾制）。
    #   舊寫法是 --agent-id chess-<index>，在新制下棋局編號會變成**資料夾名也就是身分**，
    #   長出 queues/chess-1/ queues/chess-2/ 每局一個 —— 棋局不是人，那是身分層污染。
    #   改用 --lane 之後落 queues/<persona>/queue-chess-<index>.json：
    #   原本「每局獨立 queue 不互相阻塞」的意圖保留，身分則回到真正下棋的人身上。
    #   sender_persona 缺席（系統代發）→ 不帶 --persona，落 anonymous/queue-chess-N.json，
    #   誠實表示「這局沒有具名發送者」，不假造一個叫 chess-system 的人。
    cmd = [sys.executable, str(_RUN_CMD)]
    if sender_persona:
        cmd += ["--persona", sender_persona]
    cmd += ["--lane", f"chess-{g['index']}",
           "run", "Tavern", "--arg", "op=post", "--arg", "room=tavern",
           "--arg", f"sender_id={sender}", "--arg", f"persona={sender}",
           "--arg", f"body={body}", "--arg", f"meta={meta}"]
    try:
        # encoding/errors 必帶: Windows reader thread 預設 cp950 解 run_cmd 的 UTF-8 輸出(♟️/中文)
        #   會 UnicodeDecodeError 爆 thread traceback (post 仍落地但輸出髒)。指定 utf-8 + replace 根治。
        env = dict(os.environ, PYTHONIOENCODING="utf-8")
        subprocess.run(cmd, timeout=90, capture_output=True,
                       encoding="utf-8", errors="replace", env=env)
        return True
    except Exception:
        return False


# ═════════════════════════ 共用: 結束局結算 + 輸出 ═════════════════════════
def finalize_if_ended(g, no_broadcast=False, sender=None, say=""):
    """看 status 是否已結束, 是則結算發券 + 廣播收場, 回 grants。"""
    if g["status"] == "in_progress":
        return []
    grants = settle_rewards(g)
    save_game(g)
    if not no_broadcast:
        tag = {"checkmate": "將死", "stalemate": "逼和", "draw": "和局", "resigned": "認輸"}.get(g["status"], g["status"])
        win = {"white": "白方勝", "black": "黑方勝", "draw": "和局"}.get(g["result"], "")
        broadcast(g, f"對局結束 ({tag}) — {win}", sender, say)
    return grants


def print_board(g):
    st = parse_fen(g["fen"])
    status, detail = result_status(st, g["repetition"].get(position_key(st), 0))
    chk = " [將軍]" if detail == "check" else ""
    print(f"♟️ Chess #{g['index']} | 白:{g['seats']['white'] or 'OPEN'} ⚔ 黑:{g['seats']['black'] or 'OPEN'}"
          f" | 輪:{'白' if st['turn']=='w' else '黑'} | {g['status']}{chk}")
    print(render_board(st, g["last_move"]))
    print(f"FEN: {g['fen']}")
    if g["status"] != "in_progress":
        print(f"結果: {g['result']} ({g['status']})")


# ═════════════════════════ 子指令 ═════════════════════════
def cmd_start(a):
    side = a.side
    if side == "both":
        wseat = bseat = a.persona
        mode = "solo"
    elif side == "white":
        wseat, bseat, mode = a.persona, None, "open"
        if not a.vs_open:
            bseat = a.persona  # 沒指定開放 → 預設 solo 掌兩座, 之後可 release
            mode = "solo"
    elif side == "black":
        wseat, bseat, mode = None, a.persona, "open"
        if not a.vs_open:
            wseat = a.persona
            mode = "solo"
    else:
        raise SystemExit("--side 須為 white|black|both")
    if a.vs_open:
        mode = "open"
    idx = next_index()
    g = new_game(idx, wseat, bseat, mode)
    save_game(g)
    say = (a.say or "").strip()
    open_seat = "白" if wseat is None else "黑" if bseat is None else None
    print(f"✅ 開局 Chess #{idx} (mode={mode}) 白:{wseat or 'OPEN'} 黑:{bseat or 'OPEN'}")
    print_board(g)
    if open_seat:
        print(f"🪑 {open_seat}座 OPEN — 等人加入: python chess.py join {idx} --persona <你> [--say \"...\"]")
    if not a.no_broadcast:
        hdr = "新局開盤" + (f"·{open_seat}座徵人對弈" if open_seat else "")
        broadcast(g, hdr, a.persona, say)
    print(f"\n下一步: python chess.py move {idx} <uci> --persona {a.persona}")


def cmd_join(a):
    g = load_game(a.idx)
    if g["status"] != "in_progress":
        raise SystemExit(f"❌ 對局 #{a.idx} 已結束 ({g['status']})，無法加入")
    seats = g["seats"]
    say = (a.say or "").strip()
    solo_holder = seats["white"] if (seats["white"] and seats["white"] == seats["black"]) else None
    want = a.side
    if want is None:                  # 自動挑座: 優先 OPEN; 否則 solo 局挑黑座切入
        want = "white" if seats["white"] is None else "black" if seats["black"] is None else "black"
    occupant = seats.get(want)
    if occupant is None:                                  # 情況1: 認領 OPEN 座
        seats[want] = a.persona
        kind = "認領OPEN"
    elif solo_holder and a.persona != solo_holder:        # 情況2: 中途切入 solo 局 (接管一座, 單人保留另一座)
        seats[want] = a.persona
        kind = "中途切入"
    elif occupant == a.persona:
        raise SystemExit(f"❌ 你已經坐在 {want} 座了")
    else:
        raise SystemExit(f"❌ {want} 座已被 {occupant} 佔 (非 solo, 無法切入)；可改接另一座或開新局")
    g["mode"] = "versus" if seats["white"] and seats["black"] and seats["white"] != seats["black"] else "solo"
    append_history(g, f"join:{want}", a.persona, g["fen"], g["fen"], say)
    g["updated"] = utcnow_iso()
    save_game(g)
    print(f"✅ {a.persona} {kind} Chess #{a.idx} 接 {want} 座; mode={g['mode']}" + (f"  💬 {say}" if say else ""))
    print_board(g)
    if not a.no_broadcast:
        broadcast(g, f"{a.persona} {kind} 接 {want} 座 → {g['mode']}", a.persona, say)


def cmd_release(a):
    """solo/在座者中途釋出一座 → OPEN, 等別人加入 (中途轉 1v1)。"""
    g = load_game(a.idx)
    if g["status"] != "in_progress":
        raise SystemExit(f"❌ 對局 #{a.idx} 已結束")
    seats = g["seats"]
    if a.persona not in (seats["white"], seats["black"]):
        raise SystemExit(f"❌ {a.persona} 不在本局座位，無法釋座")
    side = a.side
    if side is None:
        side = "black" if seats["white"] == seats["black"] == a.persona else \
               ("white" if seats["white"] == a.persona else "black")
    if seats.get(side) != a.persona:
        raise SystemExit(f"❌ {side} 座不是你佔的，不能釋出")
    seats[side] = None
    g["mode"] = "open"
    say = (a.say or "").strip()
    append_history(g, f"release:{side}", a.persona, g["fen"], g["fen"], say)
    g["updated"] = utcnow_iso()
    save_game(g)
    print(f"🪑 {a.persona} 釋出 {side} 座 → OPEN，等人加入" + (f"  💬 {say}" if say else ""))
    print_board(g)
    if not a.no_broadcast:
        broadcast(g, f"{a.persona} 釋出 {side} 座徵人對弈", a.persona, say)


def cmd_lobby(a):
    """列出『等待加入』的對局 (有 OPEN 座, 或 solo 局可中途切入)。"""
    ensure_dirs()
    files = sorted(_GAMES_DIR.glob("*.json"), key=lambda p: int(p.stem))
    waiting = []
    for fpath in files:
        g = json.loads(fpath.read_text(encoding="utf-8"))
        if g["status"] != "in_progress":
            continue
        s = g["seats"]
        open_sides = [k for k in ("white", "black") if s[k] is None]
        solo = bool(s["white"]) and s["white"] == s["black"]
        if open_sides or solo:
            waiting.append((g, open_sides, solo))
    if not waiting:
        print("(目前沒有等待加入的對局; 用 `start --vs-open` 開一局徵人)")
        return
    print(f"🪑 等待加入的對局 ({len(waiting)}):")
    for g, open_sides, solo in waiting:
        tag = ("OPEN座:" + "/".join(open_sides)) if open_sides else f"solo({g['seats']['white']}) 可中途切入"
        nmoves = len([h for h in g["history"] if len(h.get("uci", "")) >= 4 and ":" not in h.get("uci", "")])
        print(f"  #{g['index']:>2} 白:{str(g['seats']['white'] or 'OPEN'):<10} 黑:{str(g['seats']['black'] or 'OPEN'):<10}"
              f" 已走{nmoves}手 — {tag}")
        print(f"      → python chess.py join {g['index']} --persona <你> [--say \"...\"]")


def cmd_move(a):
    g = load_game(a.idx)
    if g["status"] != "in_progress":
        raise SystemExit(f"❌ 對局 #{a.idx} 已結束 ({g['status']})")
    st = parse_fen(g["fen"])
    white = st["turn"] == "w"
    seat = g["seats"]["white"] if white else g["seats"]["black"]
    # 回合鎖: 1v1 只認該座持有者; solo (兩座同人) 放行
    warn = []
    if seat and seat != a.persona and g["seats"]["white"] != g["seats"]["black"]:
        warn.append(f"⚠ 現在輪{'白' if white else '黑'}({seat}), 你是 {a.persona} — 自律模式仍套用, 請自查回合")
    legal = legal_moves(st)
    uci = a.uci.strip().lower()
    # 防呆硬擋 (malformed move, 非 chess-legality 問題): autonomous 模式信任的是「棋規合法性」
    # (可無視牽制 / 送將等), 不是「憑空生子」。起點無子 / 起點是對方棋子 = malformed 輸入 —
    # 並發下拿過時盤面落子最常踩 (e.g. 他人已推進盤面, 你的 from 格已空), apply_move 會把 to 格的子
    # 靜默蒸發成不可能盤面。故此處在套用前硬 reject (這是輸入有效性, 不是棋規自律範疇)。
    if len(uci) >= 4:
        frm_idx = alg_to_idx(uci[0:2])
        pf = st["board"][frm_idx]
        if pf == ".":
            raise SystemExit(f"❌ 起點 {uci[0:2]} 無子, 拒絕套用 (盤面可能已被他人推進, 請先 `board {a.idx}` 看最新狀態再走)")
        if (white and not pf.isupper()) or (not white and not pf.islower()):
            raise SystemExit(f"❌ 起點 {uci[0:2]} 是對方棋子, 現在輪{'白' if white else '黑'} — 拒絕套用 (請確認回合 / 最新盤面)")
    if uci not in legal:
        warn.append(f"⚠ 此步不在合法步集合(自律模式仍套用, 請自查)。合法步示例: {', '.join(legal[:8])}{'…' if len(legal) > 8 else ''}")
    prior_fen = g["fen"]
    ns = apply_move(st, uci)
    g["prior_fen"] = prior_fen
    g["fen"] = serialize_fen(ns)
    g["last_move"] = uci
    key = position_key(ns)
    g["repetition"][key] = g["repetition"].get(key, 0) + 1
    say = (a.say or "").strip()
    append_history(g, uci, a.persona, prior_fen, g["fen"], say)
    status, detail = result_status(ns, g["repetition"][key])
    g["updated"] = utcnow_iso()
    grants = []
    if status == "checkmate":
        g["status"], g["result"] = "checkmate", detail
    elif status == "stalemate":
        g["status"], g["result"] = "stalemate", "draw"
    elif status == "draw":
        g["status"], g["result"] = "draw", "draw"
    save_game(g)
    for w in warn:
        print(w)
    print(f"✅ #{a.idx} {a.persona} 走 {uci}" + (f" → {detail}" if detail else "")
          + (f"  💬 {say}" if say else ""))
    print_board(g)
    if status in ("checkmate", "stalemate", "draw"):
        grants = finalize_if_ended(g, a.no_broadcast, a.persona, say)
    elif not a.no_broadcast:
        hdr = f"{a.persona} 走 {uci}" + (" — 將軍!" if detail == "check" else "")
        broadcast(g, hdr, a.persona, say)
    if grants:
        print("🎟 繪圖券發放:")
        for persona, amt, kind, bal in grants:
            print(f"   {persona} +{amt} ({kind}) → 餘額 {bal}")


def cmd_board(a):
    print_board(load_game(a.idx))


def cmd_resign(a):
    g = load_game(a.idx)
    if g["status"] != "in_progress":
        raise SystemExit(f"❌ 對局 #{a.idx} 已結束")
    seats = g["seats"]
    if a.persona == seats["white"]:
        loser = "white"
    elif a.persona == seats["black"]:
        loser = "black"
    else:
        raise SystemExit(f"❌ {a.persona} 不在本局座位")
    say = (a.say or "").strip()
    g["status"] = "resigned"
    g["result"] = "black" if loser == "white" else "white"
    append_history(g, "resign", a.persona, g["fen"], g["fen"], say)
    g["updated"] = utcnow_iso()
    save_game(g)
    print(f"🏳 {a.persona} ({loser}) 認輸 → {g['result']} 勝" + (f"  💬 {say}" if say else ""))
    grants = finalize_if_ended(g, a.no_broadcast, a.persona, say)
    for persona, amt, kind, bal in grants:
        print(f"   🎟 {persona} +{amt} ({kind}) → 餘額 {bal}")


def cmd_draw(a):
    g = load_game(a.idx)
    if g["status"] != "in_progress":
        raise SystemExit(f"❌ 對局 #{a.idx} 已結束")
    say = (a.say or "").strip()
    if a.accept:
        if not g["draw_offer"] or g["draw_offer"] == a.persona:
            raise SystemExit("❌ 沒有對方的提和可接受")
        g["status"], g["result"] = "draw", "draw"
        append_history(g, "draw_accept", a.persona, g["fen"], g["fen"], say)
        save_game(g)
        print(f"🤝 {a.persona} 接受和議 → 和局" + (f"  💬 {say}" if say else ""))
        grants = finalize_if_ended(g, a.no_broadcast, a.persona, say)
        for persona, amt, kind, bal in grants:
            print(f"   🎟 {persona} +{amt} ({kind}) → 餘額 {bal}")
    else:
        g["draw_offer"] = a.persona
        append_history(g, "draw_offer", a.persona, g["fen"], g["fen"], say)
        g["updated"] = utcnow_iso()
        save_game(g)
        print(f"🤝 {a.persona} 提和 (等對方 --accept)" + (f"  💬 {say}" if say else ""))
        if not a.no_broadcast:
            broadcast(g, f"{a.persona} 提和", a.persona, say)


def cmd_list(a):
    ensure_dirs()
    files = sorted(_GAMES_DIR.glob("*.json"), key=lambda p: int(p.stem))
    if not files:
        print("(無對局)")
        return
    print(f"{'idx':>3} {'mode':<7} {'白':<12} {'黑':<12} {'status':<11} result")
    for fpath in files:
        g = json.loads(fpath.read_text(encoding="utf-8"))
        print(f"{g['index']:>3} {g['mode']:<7} {str(g['seats']['white']):<12} "
              f"{str(g['seats']['black']):<12} {g['status']:<11} {g['result']}")


def main():
    ap = argparse.ArgumentParser(description="下棋活動 — 西洋棋 (chess.py)")
    ap.add_argument("--no-broadcast", action="store_true", help="不廣播酒館 (本地測試用)")
    sub = ap.add_subparsers(dest="cmd", required=True)

    ps = sub.add_parser("start", help="開新局")
    ps.add_argument("--persona", required=True)
    ps.add_argument("--side", default="both", choices=["white", "black", "both"], help="both=solo掌兩座")
    ps.add_argument("--vs-open", action="store_true", help="另一座留 OPEN 等人加入")
    ps.add_argument("--say", default="", help="開局帶一句話 (自言自語/喊話)")
    ps.set_defaults(func=cmd_start)

    pj = sub.add_parser("join", help="加入對局 (認領 OPEN 座, 或中途切入 solo 局)")
    pj.add_argument("idx", type=int)
    pj.add_argument("--persona", required=True)
    pj.add_argument("--side", default=None, choices=["white", "black"])
    pj.add_argument("--say", default="", help="加入帶一句話")
    pj.set_defaults(func=cmd_join)

    prl = sub.add_parser("release", help="中途釋出一座 → OPEN 等人加入 (轉 1v1)")
    prl.add_argument("idx", type=int)
    prl.add_argument("--persona", required=True)
    prl.add_argument("--side", default=None, choices=["white", "black"])
    prl.add_argument("--say", default="", help="釋座帶一句話")
    prl.set_defaults(func=cmd_release)

    plo = sub.add_parser("lobby", help="列出等待加入的對局 (OPEN座/可切入的 solo)")
    plo.set_defaults(func=cmd_lobby)

    pm = sub.add_parser("move", help="走子 (UCI: e2e4 / e7e8q / e1g1)")
    pm.add_argument("idx", type=int)
    pm.add_argument("uci")
    pm.add_argument("--persona", required=True)
    pm.add_argument("--say", default="", help="這一步帶一句話 (自言自語/跟對手聊天)")
    pm.set_defaults(func=cmd_move)

    pb = sub.add_parser("board", help="印盤面")
    pb.add_argument("idx", type=int)
    pb.set_defaults(func=cmd_board)

    pr = sub.add_parser("resign", help="認輸")
    pr.add_argument("idx", type=int)
    pr.add_argument("--persona", required=True)
    pr.add_argument("--say", default="", help="認輸帶一句話")
    pr.set_defaults(func=cmd_resign)

    pd = sub.add_parser("draw", help="提和 / --accept 接受")
    pd.add_argument("idx", type=int)
    pd.add_argument("--persona", required=True)
    pd.add_argument("--accept", action="store_true")
    pd.add_argument("--say", default="", help="提和/接受帶一句話")
    pd.set_defaults(func=cmd_draw)

    pl = sub.add_parser("list", help="列對局")
    pl.set_defaults(func=cmd_list)

    args = ap.parse_args()
    args.func(args)


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8") if hasattr(sys.stdout, "reconfigure") else None
    main()
