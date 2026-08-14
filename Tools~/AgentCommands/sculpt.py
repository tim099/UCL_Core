#!/usr/bin/env python3
"""
3D Voxel Sculpture Engine (sculpt.py)
UCL_Core AgentCommands Package

Pure Geometry & Rendering Engine for 256x256x256 3D Voxel Space.
Features:
- Sparse Voxel Hashmap Storage & Event-Sourced Logs
- Incremental Cache (sculpt_cache.json) with auto self-healing
- No-Overwrite Safeguard for Box/Fill (only fills empty voxels)
- Carve operation for removing voxels (returns to transparent 0x00)
- 2.5D Isometric PNG Renderer with --region cropping, --exclude-color filtering & Skybox support
- MagicaVoxel .vox and Wavefront .obj exporters
- Image → 3D stamping (stamp2d / stampimg): RGBA PNG 為唯一輸入格式，alpha=painted-mask，
  透明像素不放 voxel。兩 op 共用 png_to_painted() + stamp_pixels()，貼圖語意只有一份。
"""

import os
import sys
import glob
import hashlib
import json
import math
import time
import uuid
import argparse
from pathlib import Path
from datetime import datetime

# Pillow for PNG rendering
try:
    from PIL import Image, ImageDraw
except ImportError:
    Image = None
    ImageDraw = None

def get_repo_root():
    curr = Path(__file__).resolve()
    for parent in curr.parents:
        if (parent / "AgentCommands" / "ChatTavern").exists():
            return parent
    return curr.parents[5]

def get_sculpt_dir():
    d = get_repo_root() / "AgentCommands" / "Sculpture"
    d.mkdir(parents=True, exist_ok=True)
    (d / "events").mkdir(parents=True, exist_ok=True)
    return d

def get_cache_file():
    return get_sculpt_dir() / "sculpt_cache.json"

# RGB332 Palette mapping to 256 RGB Colors
def get_rgb332_color(idx):
    if idx < 0 or idx > 255:
        idx = 0
    r = ((idx >> 5) & 0x07) * 255 // 7
    g = ((idx >> 2) & 0x07) * 255 // 7
    b = (idx & 0x03) * 255 // 3
    return (r, g, b)

# ───────────────── 圖片 → 已繪像素（stamp2d / stampimg 共用前端） ─────────────────
# 區塊職責：把一張 RGBA PNG 解成「要放 voxel 的格子」清單，供投影核心 stamp_pixels 使用。
# 物理意義：**alpha 就是 painted-mask**（Tim 2026-08-14 拍板：2D→3D 一律先出預覽再轉繪）——
#          透明＝沒畫過＝不放 voxel；不透明＝畫過（含故意畫的白）＝放。
#          這條之所以成立，是因為 canvas.py 的透明變體把 mask 編碼進 alpha，而
#          RGB332 的 256 個 index 解碼出 256 個相異 RGB（實算驗過）⇒ index→RGB→index 往返無損。
#          所以「改走 PNG」不是把事實源換成投影，是換成一個**無損且人眼可核准**的中介格式：
#          人核准的那張圖，跟貼進 3D 的那份 bytes，是同一批。
# 數值影響：回傳 [(u, v, color_index), ...]，u/v 是相對圖左上角的偏移（(0,0) 在左上，影像慣例）。
#          非 canvas 來源的外部圖用 canvas.rgb_to_index 量化到最近的 RGB332 色 —— 只有一份量化規則。
#          alpha_threshold：alpha < 門檻視為未繪製。canvas 產的圖 alpha 只有 0/255，門檻無差；
#          外部去背圖常有反鋸齒半透明邊，預設 128 ＝ 半透明以上才算畫過（可調）。
# 失敗處置：檔案不存在 / 非圖檔 → raise，由呼叫端印人話 + 非零退出（不吞）。
def png_to_painted(png_path, alpha_threshold=128, resize=None):
    from PIL import Image
    _cv = _load_canvas_module()
    img = Image.open(str(png_path))
    if img.mode != "RGBA":
        img = img.convert("RGBA")
    if resize:
        # NEAREST：任何插值都會生出 0<alpha<255 的半透明邊與盤外中間色，
        # 而「畫過/沒畫過」是二值判定 —— 插值等於製造來源上不存在的像素。
        img = img.resize((resize[0], resize[1]), resample=Image.NEAREST)
    w, h = img.width, img.height
    if w * h > 16000000:
        raise ValueError(f"圖片過大 {w}x{h}（上限 16,000,000 像素）—— 先用 --resize 縮小")
    data = img.tobytes()          # RGBA 逐像素 4 bytes
    out = []
    for i in range(0, len(data), 4):
        a = data[i + 3]
        if a < alpha_threshold:
            continue              # 透明 = 沒畫過 = 不存在（不是「白色」）
        px = i >> 2
        out.append((px % w, px // w, _cv.rgb_to_index(data[i], data[i + 1], data[i + 2])))
    return out, w, h


# 區塊職責：依絕對檔案路徑載入同目錄 canvas.py（2D 端規則的唯一來源）。
# 邊界：**不重造 replay / 調色盤 / 量化邏輯** —— 造第二份就是 2026-06-04 canvas drift bug 的形狀。
#      不用 import canvas：本檔可能被以任意 CWD 執行，模組搜尋路徑不可靠。
def _load_canvas_module():
    import importlib.util as _ilu
    _cv_path = Path(__file__).resolve().parent / "canvas.py"
    _spec = _ilu.spec_from_file_location("_ucl_canvas_for_sculpt", _cv_path)
    _cv = _ilu.module_from_spec(_spec)
    _spec.loader.exec_module(_cv)
    return _cv


# 區塊職責：把 2D 共用畫布的某矩形區域渲成 RGBA 預覽 PNG（落檔），回傳路徑與非透明像素數。
# 物理意義：stamp2d 的第一步。落檔而不是留在記憶體，是因為**預覽必須可被人與工具事後檢查**——
#          「我貼進去的是這張」要能被第三方驗證，不能只有我自己知道。
# 數值影響：回傳 (png 路徑, 非透明像素數, region_w, region_h, sha256)。
#          區域座標兩角任意順序、clamp 進畫布邊界（與 canvas view --region 同語意）。
def render_canvas_region_png(src_x1, src_y1, src_x2, src_y2, out_path):
    _cv = _load_canvas_module()
    x1, x2 = min(src_x1, src_x2), max(src_x1, src_x2)
    y1, y2 = min(src_y1, src_y2), max(src_y1, src_y2)
    x1, y1 = max(0, x1), max(0, y1)
    x2, y2 = min(_cv.CANVAS_W - 1, x2), min(_cv.CANVAS_H - 1, y2)

    buf, mask = _cv.build_buffer(_cv.Paths(_cv.DEFAULT_CANVAS_ROOT, _cv.DEFAULT_TREASURY_ROOT),
                                 with_mask=True)
    img = _cv.buffer_to_image_rgba(buf, mask).crop((x1, y1, x2 + 1, y2 + 1))
    out_path = Path(out_path)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    img.save(str(out_path))
    opaque = sum(1 for a in img.tobytes()[3::4] if a != 0)
    sha = hashlib.sha256(out_path.read_bytes()).hexdigest()
    return out_path, opaque, (x2 - x1 + 1), (y2 - y1 + 1), sha


class SparseVoxelSpace:
    def __init__(self):
        # Key: "x,y,z", Value: color_index (0..255)
        self.voxels = {}
        self.last_event_file = ""

    def get_voxel(self, x, y, z):
        return self.voxels.get(f"{x},{y},{z}", 0)

    def set_voxel(self, x, y, z, color_idx):
        key = f"{x},{y},{z}"
        if color_idx == 0:
            if key in self.voxels:
                del self.voxels[key]
        else:
            self.voxels[key] = color_idx

    def to_dict(self):
        return {
            "last_event_file": self.last_event_file,
            "voxels": self.voxels
        }

    def from_dict(self, data):
        self.last_event_file = data.get("last_event_file", "")
        self.voxels = data.get("voxels", {})

def load_all_events():
    events_dir = get_sculpt_dir() / "events"
    event_files = sorted(events_dir.glob("**/*.json"))
    events = []
    for ef in event_files:
        try:
            with open(ef, "r", encoding="utf-8") as f:
                data = json.load(f)
                data["_file"] = str(ef.relative_to(events_dir))
                events.append(data)
        except Exception:
            pass
    return events

def load_space_state():
    space = SparseVoxelSpace()
    cache_path = get_cache_file()
    
    # Attempt loading from cache
    if cache_path.exists():
        try:
            with open(cache_path, "r", encoding="utf-8") as f:
                data = json.load(f)
                space.from_dict(data)
        except Exception:
            space = SparseVoxelSpace()

    # Incremental update or rebuild
    all_events = load_all_events()
    new_events = []
    
    if space.last_event_file:
        found_last = False
        for ev in all_events:
            if found_last:
                new_events.append(ev)
            elif ev.get("_file") == space.last_event_file:
                found_last = True
        if not found_last: # Cache corrupted/stale, rebuild
            space = SparseVoxelSpace()
            new_events = all_events
    else:
        new_events = all_events

    # Apply incremental events
    for ev in new_events:
        apply_event_to_space(space, ev)
        space.last_event_file = ev.get("_file", "")

    # Save updated cache
    save_cache(space)
    return space

# 貼圖類 op 集合 —— 事件 shape 相同（placed_colored 逐 voxel 帶色），故重播共用一個分支。
# 新增貼圖 op 只要加進這裡；漏加會被 apply_event_to_space 的 else 分支當場吵（不再靜默略過）。
STAMP_OPS = {"stamp2d", "stampimg"}


def apply_event_to_space(space, ev):
    op = ev.get("op")
    if op in ["box", "fill"]:
        color = ev.get("color", 19)
        # Apply only to empty voxels or placed list
        placed_voxels = ev.get("placed_voxels", [])
        if placed_voxels:
            for v in placed_voxels:
                space.set_voxel(v[0], v[1], v[2], color)
        else: # Legacy/Direct AABB without pre-check
            x1, x2 = ev.get("x1", 0), ev.get("x2", 0)
            y1, y2 = ev.get("y1", 0), ev.get("y2", 0)
            z1, z2 = ev.get("z1", 0), ev.get("z2", 0)
            for x in range(min(x1, x2), max(x1, x2) + 1):
                for y in range(min(y1, y2), max(y1, y2) + 1):
                    for z in range(min(z1, z2), max(z1, z2) + 1):
                        if space.get_voxel(x, y, z) == 0:
                            space.set_voxel(x, y, z, color)
    elif op == "carve":
        carved_voxels = ev.get("carved_voxels", [])
        if carved_voxels:
            for v in carved_voxels:
                space.set_voxel(v[0], v[1], v[2], 0)
        else:
            x1, x2 = ev.get("x1", 0), ev.get("x2", 0)
            y1, y2 = ev.get("y1", 0), ev.get("y2", 0)
            z1, z2 = ev.get("z1", 0), ev.get("z2", 0)
            for x in range(min(x1, x2), max(x1, x2) + 1):
                for y in range(min(y1, y2), max(y1, y2) + 1):
                    for z in range(min(z1, z2), max(z1, z2) + 1):
                        space.set_voxel(x, y, z, 0)
    elif op == "point":
        x, y, z = ev.get("x", 0), ev.get("y", 0), ev.get("z", 0)
        color = ev.get("color", 19)
        space.set_voxel(x, y, z, color)
    elif op in STAMP_OPS:
        # 逐 voxel 帶自己的顏色（來自 2D 像素／PNG），所以不能共用 box 的單色 placed_voxels 欄位；
        # 欄位名刻意不同 —— 同名不同 shape 會讓舊分支「成功地」讀錯。
        # ⚠ 這個分支是必要的，不是選配：cache 失效時 load_space_state 會重播 events，
        #   而未知 op 會被**安靜略過** ⇒ stamp 過的東西下次重播就消失（靜默失效）。
        for v in ev.get("placed_colored", []):
            space.set_voxel(v[0], v[1], v[2], v[3])
    else:
        # ⚠ 未知 op 過去是靜默略過 —— 那正是「新增一個 stamp op 卻忘了擴充這裡」的
        #   完美隱身衣：事件寫了、錢扣了、JSON 說 success，而 voxel 一顆都不會出現。
        #   （summit 2026-08-14 加 stampimg 時原樣踩中，就在這段警告的正下方。）
        #   規則從此長在通道上，不掛在記憶裡：不認得就吵，不要安靜地成功。
        raise ValueError(
            f"apply_event_to_space 不認得 op='{op}'（事件 {ev.get('_file', '?')}）—— "
            f"新增 op 必須同時擴充本函式，否則重播時該事件會靜默消失。"
            f"已知：box/fill/carve/point/{'/'.join(sorted(STAMP_OPS))}")

def save_cache(space):
    cache_path = get_cache_file()
    try:
        with open(cache_path, "w", encoding="utf-8") as f:
            json.dump(space.to_dict(), f, ensure_ascii=False)
    except Exception:
        pass

def record_event(ev_data):
    today = datetime.now().strftime("%Y-%m-%d")
    day_dir = get_sculpt_dir() / "events" / today
    day_dir.mkdir(parents=True, exist_ok=True)
    
    ts_str = datetime.now().strftime("%H%M%S_%f")[:10]
    uid_str = str(uuid.uuid4())[:6]
    fname = f"{ts_str}_{uid_str}.json"
    
    file_path = day_dir / fname
    with open(file_path, "w", encoding="utf-8") as f:
        json.dump(ev_data, f, ensure_ascii=False, indent=2)
    return file_path

# --- Commands ---

def cmd_box(args):
    space = load_space_state()
    x1, x2 = min(args.x1, args.x2), max(args.x1, args.x2)
    y1, y2 = min(args.y1, args.y2), max(args.y1, args.y2)
    z1, z2 = min(args.z1, args.z2), max(args.z1, args.z2)
    
    # Boundary check 0..255
    x1, x2 = max(0, x1), min(255, x2)
    y1, y2 = max(0, y1), min(255, y2)
    z1, z2 = max(0, z1), min(255, z2)

    total_vol = (x2 - x1 + 1) * (y2 - y1 + 1) * (z2 - z1 + 1)
    
    # Safety Check: Max 1,000,000 voxels per box
    if total_vol > 1000000:
        print(f"❌ 體積過大警告：單次 box 最大允許 1,000,000 voxels，當前為 {total_vol} voxels。")
        return 1

    placed_voxels = []
    skipped_count = 0
    
    for x in range(x1, x2 + 1):
        for y in range(y1, y2 + 1):
            for z in range(z1, z2 + 1):
                if space.get_voxel(x, y, z) == 0:
                    placed_voxels.append((x, y, z))
                else:
                    skipped_count += 1

    placed_count = len(placed_voxels)
    
    ev_data = {
        "op": "box",
        "persona": args.persona,
        "x1": x1, "x2": x2,
        "y1": y1, "y2": y2,
        "z1": z1, "z2": z2,
        "color": args.color,
        "total_volume": total_vol,
        "placed_count": placed_count,
        "skipped_count": skipped_count,
        "placed_voxels": placed_voxels,
        "timestamp": datetime.now().isoformat()
    }
    
    ev_file = record_event(ev_data)
    
    # Apply & Update Cache
    apply_event_to_space(space, ev_data)
    space.last_event_file = str(ev_file.relative_to(get_sculpt_dir() / "events"))
    save_cache(space)

    print(json.dumps({
        "status": "success",
        "op": "box",
        "persona": args.persona,
        "total_volume": total_vol,
        "placed_count": placed_count,
        "skipped_count": skipped_count,
        "event_file": str(ev_file)
    }, ensure_ascii=False, indent=2))

# ───────────────── 貼圖投影核心（stamp2d / stampimg 共用） ─────────────────
# 區塊職責：把「已繪像素清單」依 facing 貼上某平面、沿法線擠出 thickness 層 → 算出要落的 voxel。
# 物理意義：facing 是**貼片的法線**（貼紙朝哪邊看）。平面內兩軸的取法固定如下，
#          並且會原樣印在回傳 JSON 的 axis_map 欄位裡（呼叫端不必猜、可事後對帳）：
#            ±Z 法線 → u→X、v→Y（v 反向，讓圖不上下顛倒）
#            ±Y 法線 → u→X、v→Z（地板/天花板；v 沿 Z 前進）
#            ±X 法線 → u→Z、v→Y（v 反向，同上）
#          2D 的 y 往下增長（影像慣例），3D 的 Y 往上 —— 所以垂直軸一律翻轉，
#          否則貼上去的山會倒過來（而它看起來只會「怪」，不會報錯）。
# 數值影響：voxel 數 = 已繪像素數 × thickness（**不是圖面積 × thickness** —— 透明不佔）。
#          與 box 一致：預設不覆蓋既有 voxel（skipped 計數回報）；--overwrite 才蓋。
# ⚠ 顏色 0 的例外：3D 用 0 表示「空」（carve 就是寫 0），所以純黑(index 0) 若原樣寫進去
#   會變成「沒放」。畫過的東西必須留下痕跡 ⇒ 純黑重映到最接近的非零暗色 index 4 (0,36,0)，
#   並在回傳 JSON 的 remapped_black 計數回報 —— 不靜默改人家的顏色。
AXIS_MAP = {
    "z+": ("x", "y", "z", True),  "z-": ("x", "y", "z", True),
    "y+": ("x", "z", "y", False), "y-": ("x", "z", "y", False),
    "x+": ("z", "y", "x", True),  "x-": ("z", "y", "x", True),
}
AXIS_I = {"x": 0, "y": 1, "z": 2}


def stamp_pixels(space, painted, region_h, at, facing, thickness, overwrite):
    u_ax, v_ax, n_ax, flip_v = AXIS_MAP[facing]
    n_dir = 1 if facing.endswith("+") else -1

    placed, skipped, oob, remapped_black = [], 0, 0, 0
    for (u, v, cidx) in painted:
        if cidx == 0:                       # 3D 的 0 = 空 → 純黑重映到最近的非零暗色
            cidx = 4
            remapped_black += 1
        vv = (region_h - 1 - v) if flip_v else v
        for t in range(thickness):
            p = list(at)
            p[AXIS_I[u_ax]] += u
            p[AXIS_I[v_ax]] += vv
            p[AXIS_I[n_ax]] += n_dir * t
            x, y, z = p
            if not (0 <= x <= 255 and 0 <= y <= 255 and 0 <= z <= 255):
                oob += 1
                continue
            if not overwrite and space.get_voxel(x, y, z) != 0:
                skipped += 1
                continue
            placed.append([x, y, z, cidx])
    axis_map = {"u": u_ax, "v": v_ax, "normal": n_ax, "v_flipped": flip_v}
    return placed, skipped, oob, remapped_black, axis_map


# 區塊職責：解析兩個 stamp op 共用的幾何參數（facing / at / thickness）。
# 失敗處置：回 (None, 錯誤訊息) —— 呼叫端印人話 + 非零退出，不 raise 也不靜默預設。
def parse_stamp_geometry(args):
    facing = (args.facing or "z+").lower().replace("+z", "z+").replace("-z", "z-")
    if facing not in AXIS_MAP:
        return None, f"facing 非法: {args.facing}（可用 x+ x- y+ y- z+ z-）"
    try:
        at = [int(v) for v in str(args.at).split(",")]
        if len(at) != 3:
            raise ValueError
    except ValueError:
        return None, f"--at 需為 'x,y,z': {args.at}"
    return (facing, at, max(1, int(args.thickness))), None


# ───────────────── stamp 共用後半段：閘門 → 投影 → 落事件 → 回報 ─────────────────
# 區塊職責：painted 清單到手之後的所有事 —— 期望值閘門、體積上限、投影、越界處置、落事件、印 JSON。
# 物理意義：**expect_pixels 是閘門不是資訊**（Tim 2026-08-14 拍板「先輸出預覽再轉繪」的落地形式）。
#          預覽印出非透明像素數，呼叫端把那個數字帶回來；對不上代表「你看的」與「我吃的」不是同一張，
#          此時停下來比貼錯更便宜。沒帶 --expect-pixels 就沒有閘門 —— 那是呼叫端放棄保護，不是預設安全。
# 數值影響：越界（oob>0）預設**拒絕**而非靜默裁掉 —— 一張 1920×1080 貼進 256³ 會有 99% 落在界外，
#          而「安靜地只貼了一角」看起來完全像成功。要裁必須顯式 --allow-clip。
# 失敗處置：任一關卡不過 → 印原因 + 非零退出，**不寫事件、不扣費**（沒貼成不是成功）。
def run_stamp(args, op, painted, region_w, region_h, src_meta):
    space = load_space_state()

    geo, err = parse_stamp_geometry(args)
    if err:
        print(f"❌ {err}")
        return 2
    facing, at, thickness = geo

    # 閘門①：期望值 —— 「你看的那張」與「我吃的這張」是不是同一批 bytes
    expect = getattr(args, "expect_pixels", None)
    if expect is not None and expect != len(painted):
        print(json.dumps({
            "status": "mismatch", "op": op,
            "reason": f"--expect-pixels {expect} 與實際非透明像素 {len(painted)} 不符 —— "
                      f"來源已變動或不是預覽的那張圖；未貼、未扣費",
            "expect_pixels": expect, "actual_painted_pixels": len(painted),
            "source": src_meta,
        }, ensure_ascii=False, indent=2))
        return 4

    if not painted:
        print(json.dumps({
            "status": "empty", "op": op,
            "reason": "來源沒有任何非透明像素 —— 透明視為未繪製，無可貼內容",
            "region_w": region_w, "region_h": region_h, "source": src_meta,
        }, ensure_ascii=False, indent=2))
        return 3

    # 閘門②：體積上限（與 Cmd_Sculpture.MAX_BOX_VOLUME 同值 —— 兩端對齊義務）
    total = len(painted) * thickness
    if total > 1000000:
        print(f"❌ 體積過大：{total} voxels（上限 1,000,000）—— 縮小圖或降 thickness")
        return 1

    placed, skipped, oob, remapped_black, axis_map = stamp_pixels(
        space, painted, region_h, at, facing, thickness, args.overwrite)

    # 閘門③：越界 —— 預設不靜默裁切
    if oob > 0 and not getattr(args, "allow_clip", False):
        print(json.dumps({
            "status": "out_of_bounds", "op": op,
            "reason": f"{oob} 個 voxel 落在 256³ 空間之外 —— 未貼、未扣費。"
                      f"圖 {region_w}x{region_h} @ at={at} facing={facing} 放不下；"
                      f"改小 --at、用 --resize 縮圖，或顯式 --allow-clip 接受裁切",
            "out_of_bounds": oob, "would_place": len(placed),
            "image_size": f"{region_w}x{region_h}", "at": at, "facing": facing,
            "axis_map": axis_map, "source": src_meta,
        }, ensure_ascii=False, indent=2))
        return 5

    if not placed:
        print(json.dumps({
            "status": "empty", "op": op,
            "reason": "所有目標格都越界或已被佔用（--overwrite 可覆蓋）",
            "out_of_bounds": oob, "skipped_occupied": skipped, "source": src_meta,
        }, ensure_ascii=False, indent=2))
        return 3

    ev_data = {
        "op": op,
        "persona": args.persona,
        "source": src_meta,
        "at": at, "facing": facing, "thickness": thickness,
        "axis_map": axis_map,
        "region_w": region_w, "region_h": region_h,
        "painted_source_pixels": len(painted),
        "placed_count": len(placed),
        "skipped_occupied": skipped,
        "out_of_bounds": oob,
        "remapped_black": remapped_black,
        "placed_colored": placed,
        "timestamp": datetime.now().isoformat()
    }

    ev_file = record_event(ev_data)
    apply_event_to_space(space, ev_data)
    space.last_event_file = str(ev_file.relative_to(get_sculpt_dir() / "events"))
    save_cache(space)

    # 展品登錄放在 voxel 落地與 cache 之後 —— 展品是作品的「框」，框只能框已經存在的東西
    exhibit = _auto_exhibit(args, placed, args.persona)

    print(json.dumps({
        "status": "success", "op": op, "persona": args.persona,
        "exhibit": exhibit,
        "region": f"{region_w}x{region_h}",
        "painted_source_pixels": len(painted),   # 非透明像素數（透明不算）
        "placed_count": len(placed),
        "skipped_occupied": skipped,
        "out_of_bounds": oob,
        "remapped_black": remapped_black,
        "at": at, "facing": facing, "thickness": thickness,
        "axis_map": axis_map,
        "source": src_meta,
        "event_file": str(ev_file)
    }, ensure_ascii=False, indent=2))
    return 0


# ───────────────── slice：3D 切片 → 2D PNG（stamp 的逆運算） ─────────────────
# 區塊職責：把 region 內的 voxel 沿指定軸壓成一張 RGBA PNG（空的地方 alpha 0）。
# 物理意義：**與 stamp 共用 AXIS_MAP** —— 同一組 u/v 取法與垂直翻轉。
#          所以 `slice --axis z+` 切出來的圖，原樣 `stampimg` 貼回同一個 at 會**還原**，
#          往返可驗證（單元測試釘死這條）。若這裡另寫一套軸映射，圖會上下顛倒或轉 90°，
#          而它看起來只會「怪」，不會報錯 —— 那正是 stamp 當初的血證。
# 數值影響：厚度＝region 在法線軸上的跨度（寫 `10..10` 就是厚度 1）。
#          厚度 > 1 時**前覆蓋後**：沿 axis 方向由近到遠掃，第一顆非空的 voxel 勝出，
#          後面的被擋住（就是正射投影的遮擋，不是混色）。整條都空 → alpha 0。
#          axis=z+ → 近端是 z1（由 z1 往 z2 掃）；z- → 近端是 z2（由 z2 往 z1 掃）。
# 失敗處置：region 格式錯 / 軸非法 / 切完整張全空 → 印原因 + 非零退出，不落檔
#          （落一張全透明的圖然後說成功，就是「安靜地什麼都沒做」）。
def cmd_slice(args):
    space = load_space_state()

    axis = (args.axis or "z+").lower().replace("+z", "z+").replace("-z", "z-")
    if axis not in AXIS_MAP:
        print(f"❌ axis 非法: {args.axis}（可用 x+ x- y+ y- z+ z-）")
        return 2
    rg = _parse_region(args.region)
    if not rg:
        print(f"❌ --region 需為 'x1..x2,y1..y2,z1..z2': {args.region}")
        return 2
    x1, x2, y1, y2, z1, z2 = rg
    bounds = {"x": (x1, x2), "y": (y1, y2), "z": (z1, z2)}

    u_ax, v_ax, n_ax, flip_v = AXIS_MAP[axis]
    u1, u2 = bounds[u_ax]
    v1, v2 = bounds[v_ax]
    n1, n2 = bounds[n_ax]
    w, h = u2 - u1 + 1, v2 - v1 + 1
    # 由近到遠的法線層序：'+' 從小到大（近端＝n1），'-' 從大到小（近端＝n2）
    layers = range(n1, n2 + 1) if axis.endswith("+") else range(n2, n1 - 1, -1)
    thickness = n2 - n1 + 1

    from PIL import Image as _Img
    img = _Img.new("RGBA", (w, h), (0, 0, 0, 0))
    px = img.load()
    hit = 0
    for vi in range(h):
        for ui in range(w):
            # 影像 (ui,vi) → 世界座標：與 stamp 同式（vv = h-1-v 的反解）
            coord = {u_ax: u1 + ui, v_ax: v1 + (h - 1 - vi) if flip_v else v1 + vi}
            for n in layers:                       # 前覆蓋後：第一顆非空就停
                coord[n_ax] = n
                idx = space.get_voxel(coord["x"], coord["y"], coord["z"])
                if idx != 0:
                    px[ui, vi] = (*get_rgb332_color(idx), 255)
                    hit += 1
                    break

    if hit == 0:
        print(json.dumps({
            "status": "empty", "op": "slice",
            "reason": f"region {args.region} 沿 {axis} 切完整張全空 —— 未落檔",
            "size": f"{w}x{h}", "thickness": thickness,
        }, ensure_ascii=False, indent=2))
        return 3

    out = Path(args.out) if args.out else (get_sculpt_dir() / "_last_slice.png")
    out.parent.mkdir(parents=True, exist_ok=True)
    img.save(str(out))
    sha = hashlib.sha256(out.read_bytes()).hexdigest()

    print(json.dumps({
        "status": "success", "op": "slice",
        "region": args.region, "axis": axis,
        "size": f"{w}x{h}", "thickness": thickness,
        "axis_map": {"u": u_ax, "v": v_ax, "normal": n_ax, "v_flipped": flip_v},
        "non_transparent_pixels": hit,       # ← 可直接當 stampimg 的 --expect-pixels
        "sha256": sha,
        "output_path": str(out),
        "note": f"貼回 3D：stampimg --png \"{out}\" --expect-pixels {hit} --at <近端那一層的 at> --facing {axis}",
    }, ensure_ascii=False, indent=2))
    return 0


# ───────────────── stamp2d：2D 共用畫布的一塊 → 預覽 PNG → 3D ─────────────────
# 區塊職責：渲染來源區域的 RGBA 預覽 PNG（落檔）→ 解回已繪像素 → 走共用後半段。
# 物理意義：Tim 2026-08-14 拍板全面改道 —— 2D→3D 只有一條 code path，中介格式是 RGBA PNG。
#          繞經 PNG 不是把事實源換成投影：canvas 的 alpha 就是 painted-mask，且 RGB332 往返無損，
#          所以這一步無損；換來的是**人核准的那張圖，就是被貼進去的那份 bytes**。
#          預覽落檔在 Sculpture/_stamp_src.png，帶 sha256 進事件 —— 事後可證「我貼的是這張」。
# 失敗處置：預覽渲染失敗 / 無非透明像素 → 非零退出，不寫事件。
# ───────────────── 貼圖後自動建立／擴充展品（Tim 2026-08-14 追加） ─────────────────
# 區塊職責：只給展品 ID，就依「這次實際貼到哪」自動算 region 與觀測參數，登錄成展品。
# 物理意義：作品的範圍是**從落地的 voxel 反推**，不是人手填 —— 手填的 region 會跟作品漂移
#          （貼第二刀忘了改範圍 ⇒ 展品照片切掉一半，而它看起來像「作品就長這樣」）。
# 數值影響：region ＝ 本次 placed 的 AABB ∪ 既有展品 region，再各方向外擴 margin 並 clamp 0..255。
#          **union 不是覆蓋**：同一件作品可以分多刀貼，每刀都只會把框放大、不會把先前的裁掉。
#          既有展品的 title/author/打光等欄位一律沿用（只有 region 會被擴）——
#          自動化可以擴框，但不准擅自改別人寫的標題。
# 失敗處置：登錄或出圖失敗只回報，不推翻已成功的貼圖（voxel 已落地、錢已結算，不能因為
#          周邊步驟失敗就假裝整件事沒發生）。
def _auto_exhibit(args, placed, persona):
    ex_id = getattr(args, "exhibit_id", None)
    if not ex_id:
        return None
    margin = max(0, int(getattr(args, "exhibit_margin", 2) or 0))
    x1 = min(v[0] for v in placed); x2 = max(v[0] for v in placed)
    y1 = min(v[1] for v in placed); y2 = max(v[1] for v in placed)
    z1 = min(v[2] for v in placed); z2 = max(v[2] for v in placed)

    exhibits = load_exhibits()
    old = exhibits.get(ex_id)
    # ⚠ union 一定要對**未加 margin 的 bbox** 做，不能對 region 做：
    #   region 已含 margin，拿它去 union 再加一次 margin ⇒ 每貼一刀框就往外爬一個 margin，
    #   而作品根本沒變大（複利膨脹，且每一步看起來都合理 —— 只有連貼幾刀才看得出來）。
    #   bbox 是本次新增的欄位；舊展品沒有它 → 退回讀 region（會多含一次 margin，但只發生一次）。
    if old:
        prev = _parse_region(old.get("bbox") or old.get("region") or "")
        if prev:
            (px1, px2, py1, py2, pz1, pz2) = prev
            x1, x2 = min(x1, px1), max(x2, px2)
            y1, y2 = min(y1, py1), max(y2, py2)
            z1, z2 = min(z1, pz1), max(z2, pz2)

    def clamp(v):
        return max(0, min(255, v))
    bbox = f"{x1}..{x2},{y1}..{y2},{z1}..{z2}"          # 作品實體的精確範圍（無 margin）
    region = (f"{clamp(x1 - margin)}..{clamp(x2 + margin)},"
              f"{clamp(y1 - margin)}..{clamp(y2 + margin)},"
              f"{clamp(z1 - margin)}..{clamp(z2 + margin)}")   # 觀測框＝bbox＋留白

    preset = dict(old) if old else {}
    preset.update({
        "id": ex_id,
        "title": getattr(args, "exhibit_title", None) or preset.get("title") or ex_id,
        "author": preset.get("author") or persona,
        "description": getattr(args, "exhibit_desc", None) or preset.get("description", ""),
        "bbox": bbox,                          # ← 作品實體範圍（union 的依據，不含 margin）
        "region": region,                      # ← 觀測框＝bbox＋margin（本函式重算的兩個欄位之一）
        "exclude_color": preset.get("exclude_color", ""),
        "bg_color": preset.get("bg_color", ""),
        "skybox": preset.get("skybox", ""),
        "light_dir": preset.get("light_dir", "-1,-1,-1"),
        "ambient": preset.get("ambient", 0.4),
        "smooth": preset.get("smooth", False),
        "shadow": preset.get("shadow", False),
        "zoom": preset.get("zoom", None),
        "created_at": preset.get("created_at") or datetime.now().isoformat(),
        "updated_at": datetime.now().isoformat(),
    })
    info = {"id": ex_id, "region": region, "mode": "updated" if old else "created",
            "title": preset["title"], "author": preset["author"]}
    try:
        info["file"] = str(save_exhibit(ex_id, preset))
        info["photo"] = str(render_exhibit_photo(preset))
    except Exception as e:
        info["warning"] = f"展品登錄／出圖失敗（貼圖本身已成功、已結算）: {e}"
    return info


def _parse_region(s):
    """區塊職責：解析 'x1..x2,y1..y2,z1..z2'；不合格回 None（不猜、不吞成 0）。"""
    try:
        parts = str(s).split(",")
        if len(parts) != 3:
            return None
        out = []
        for p in parts:
            a, b = p.split("..")
            lo, hi = int(a), int(b)
            out += [min(lo, hi), max(lo, hi)]
        return (out[0], out[1], out[2], out[3], out[4], out[5])
    except (ValueError, AttributeError):
        return None


def cmd_stamp2d(args):
    src_png = get_sculpt_dir() / "_stamp_src.png"
    png_path, opaque, region_w, region_h, sha = render_canvas_region_png(
        args.src_x1, args.src_y1, args.src_x2, args.src_y2, src_png)

    painted, w, h = png_to_painted(png_path, args.alpha_threshold)
    src_meta = {
        "kind": "canvas_region",
        "src": {"x1": args.src_x1, "y1": args.src_y1, "x2": args.src_x2, "y2": args.src_y2},
        "preview_png": str(png_path),
        "sha256": sha,
        "non_transparent_pixels": opaque,
        "alpha_threshold": args.alpha_threshold,
    }
    return run_stamp(args, "stamp2d", painted, w, h, src_meta)


# ───────────────── stampimg：任意 PNG → 3D ─────────────────
# 區塊職責：吃一張外部 RGBA PNG（去背圖／canvas view 的 _last_view_t.png 皆可）→ 走共用後半段。
# 物理意義：alpha 即 painted-mask —— 透明不畫入 3D（Tim 2026-08-14 規格）。
#          外部圖的顏色不在 RGB332 盤上，用 canvas.rgb_to_index 量化到最近色（量化規則只有一份）。
# 數值影響：--alpha-threshold 預設 128 —— 去背圖的反鋸齒半透明邊，半透明以上才算畫過。
#          --resize W,H 用 NEAREST 縮放（插值會生出畫布上不存在的半透明邊與中間色）。
# 失敗處置：檔案不存在 / 尺寸過大 / 無非透明像素 → 非零退出，不寫事件。
# 區塊職責：兩個 stamp op 共用的保護旗標 —— 定義只有一份，新增保護時不會漏掉其中一個 op。
# 物理意義：expect-pixels 是預覽→轉繪的交接閘門；allow-clip 是「我知道會裁掉」的顯式簽名。
def _add_stamp_common_args(p):
    p.add_argument("--expect-pixels", type=int, default=None, dest="expect_pixels",
                   help="預覽印出的非透明像素數；對不上就拒絕（不帶＝放棄這道保護）")
    p.add_argument("--alpha-threshold", type=int, default=128, dest="alpha_threshold",
                   help="alpha 低於此值視為未繪製 (預設 128；canvas 產的圖只有 0/255，門檻無差)")
    p.add_argument("--allow-clip", action="store_true", dest="allow_clip",
                   help="接受超出 256³ 的部分被裁掉（預設越界即拒絕，不靜默只貼一角）")
    # ── 貼完自動建立／擴充展品：只要給 ID，region 由實際落地的 voxel 反推（多刀 union 不覆蓋）──
    p.add_argument("--exhibit-id", default=None, dest="exhibit_id",
                   help="作品 ID；給了就自動登錄展品，region 依本次貼到的位置反推＋與既有 union")
    p.add_argument("--exhibit-title", default=None, dest="exhibit_title",
                   help="作品標題（省略＝沿用既有，全新則用 ID）")
    p.add_argument("--exhibit-desc", default=None, dest="exhibit_desc",
                   help="作品說明（省略＝沿用既有）")
    p.add_argument("--exhibit-margin", type=int, default=2, dest="exhibit_margin",
                   help="region 外擴格數 (預設 2；框太緊自動縮放會貼邊)")


def cmd_stampimg(args):
    png_path = Path(args.png)
    if not png_path.exists():
        print(f"❌ 圖檔不存在: {png_path}")
        return 2

    resize = None
    if args.resize:
        try:
            rw, rh = (int(v) for v in str(args.resize).split(","))
            if rw <= 0 or rh <= 0:
                raise ValueError
            resize = (rw, rh)
        except ValueError:
            print(f"❌ --resize 需為 'W,H' 且為正整數: {args.resize}")
            return 2

    try:
        painted, w, h = png_to_painted(png_path, args.alpha_threshold, resize)
    except Exception as e:
        print(f"❌ 讀圖失敗 {png_path}: {e}")
        return 2

    src_meta = {
        "kind": "image_file",
        "png": str(png_path.resolve()),
        "sha256": hashlib.sha256(png_path.read_bytes()).hexdigest(),
        "image_size": f"{w}x{h}",
        "resized": f"{resize[0]},{resize[1]}" if resize else None,
        "alpha_threshold": args.alpha_threshold,
    }
    return run_stamp(args, "stampimg", painted, w, h, src_meta)


def cmd_carve(args):
    space = load_space_state()
    x1, x2 = min(args.x1, args.x2), max(args.x1, args.x2)
    y1, y2 = min(args.y1, args.y2), max(args.y1, args.y2)
    z1, z2 = min(args.z1, args.z2), max(args.z1, args.z2)
    
    carved_voxels = []
    for x in range(x1, x2 + 1):
        for y in range(y1, y2 + 1):
            for z in range(z1, z2 + 1):
                if space.get_voxel(x, y, z) != 0:
                    carved_voxels.append((x, y, z))

    carved_count = len(carved_voxels)
    
    ev_data = {
        "op": "carve",
        "persona": args.persona,
        "x1": x1, "x2": x2,
        "y1": y1, "y2": y2,
        "z1": z1, "z2": z2,
        "carved_count": carved_count,
        "carved_voxels": carved_voxels,
        "timestamp": datetime.now().isoformat()
    }
    
    ev_file = record_event(ev_data)
    apply_event_to_space(space, ev_data)
    space.last_event_file = str(ev_file.relative_to(get_sculpt_dir() / "events"))
    save_cache(space)

    print(json.dumps({
        "status": "success",
        "op": "carve",
        "persona": args.persona,
        "carved_count": carved_count,
        "event_file": str(ev_file)
    }, ensure_ascii=False, indent=2))

def get_exhibits_dir():
    d = get_sculpt_dir() / "exhibits"
    d.mkdir(parents=True, exist_ok=True)
    return d

def load_exhibits():
    ex_dir = get_exhibits_dir()
    exhibits = {}
    for ef in ex_dir.glob("*.json"):
        try:
            with open(ef, "r", encoding="utf-8") as f:
                data = json.load(f)
                eid = data.get("id") or ef.stem
                exhibits[eid] = data
        except Exception:
            pass
    return exhibits

def save_exhibit(ex_id, preset_data):
    ex_dir = get_exhibits_dir()
    file_path = ex_dir / f"{ex_id}.json"
    with open(file_path, "w", encoding="utf-8") as f:
        json.dump(preset_data, f, ensure_ascii=False, indent=2)
    return file_path

def cmd_exhibit(args):
    exhibits = load_exhibits()
    if args.ex_op == "register":
        ex_id = args.id
        preset = {
            "id": ex_id,
            "title": args.title,
            "author": args.author,
            "description": args.desc or "",
            "region": args.region or "",
            "exclude_color": args.exclude_color or "",
            "bg_color": args.bg_color or "",
            "skybox": args.skybox or "",
            "light_dir": args.light_dir or "-1,-1,-1",
            "ambient": args.ambient if args.ambient is not None else 0.4,
            "smooth": bool(args.smooth),
            "shadow": bool(getattr(args, "shadow", False)),
            "zoom": getattr(args, "zoom", None),
            "created_at": datetime.now().isoformat()
        }
        fpath = save_exhibit(ex_id, preset)
        
        # Render & Archive Exhibit Snapshot Photo PNG
        photo_path = render_exhibit_photo(preset)
        
        print(f"✅ 展品標記與典藏寫真登錄成功！ID: {ex_id} | 標題: 《{args.title}》 | 創作者: {args.author} | 照片: {photo_path.name}")
    elif args.ex_op == "list":
        print(f"# 🏛️ 3D 雕刻展覽館 展品目錄 (共 {len(exhibits)} 件):")
        for eid, item in exhibits.items():
            print(f"  - [{eid}] 《{item['title']}》 by {item['author']} | region: {item['region']} | photo: exhibits/{eid}.png")

def render_exhibit_photo(preset):
    # Renders the exhibit's snapshot photo to exhibits/<id>.png
    ex_id = preset["id"]
    out_file = get_exhibits_dir() / f"{ex_id}.png"
    
    space = load_space_state()
    voxels = space.voxels

    region_str = preset.get("region", "")
    exclude_color_str = preset.get("exclude_color", "")
    light_dir_str = preset.get("light_dir", "-1,-1,-1")
    ambient_val = preset.get("ambient", 0.4)
    smooth_mode = preset.get("smooth", True)
    shadow_mode = bool(preset.get("shadow", False))
    zoom_val = preset.get("zoom")

    try:
        light_vec = [float(v) for v in light_dir_str.split(",")]
    except Exception:
        light_vec = [-1.0, -1.0, -1.0]

    rx1, rx2, ry1, ry2, rz1, rz2 = 0, 255, 0, 255, 0, 255
    if region_str:
        try:
            parts = region_str.replace(" ", "").split(",")
            rx1, rx2 = [int(n) for n in parts[0].split("..")]
            ry1, ry2 = [int(n) for n in parts[1].split("..")]
            rz1, rz2 = [int(n) for n in parts[2].split("..")]
        except Exception:
            pass

    exclude_colors = set()
    if exclude_color_str:
        try:
            exclude_colors = {int(c) for c in exclude_color_str.split(",")}
        except Exception:
            pass

    img_w, img_h = 1024, 1024
    bg_col = (15, 23, 42)
    
    img = Image.new("RGB", (img_w, img_h), bg_col)
    draw = ImageDraw.Draw(img)

    origin_x = img_w // 2
    origin_y = img_h // 2 + 150

    W_half = 12
    H_half = 6
    Z_step = 12

    visible_points = []
    voxel_set = set()
    for key, color_idx in voxels.items():
        if color_idx in exclude_colors:
            continue
        parts = key.split(",")
        x, y, z = int(parts[0]), int(parts[1]), int(parts[2])
        if rx1 <= x <= rx2 and ry1 <= y <= ry2 and rz1 <= z <= rz2:
            depth = z * 10000 + (x + y)
            visible_points.append((depth, x, y, z, color_idx))
            voxel_set.add((x, y, z))

    visible_points.sort(key=lambda item: item[0])

    # 自動置中＋自動縮放 (2026-08-13; --zoom 可覆寫): 依投影包圍盒縮放置中 —
    # 固定縮放時大場景會爆出畫布 (Tim 實測), 自動模式「只縮不放大」把整個場景收進畫面;
    # --zoom 給定時用指定倍率 (1.0 = 原始 24px/voxel, <1 縮小, >1 放大特寫)。
    if visible_points:
        iso_xs = []
        iso_ys = []
        for _, x, y, z, _c in visible_points:
            iso_xs.append((x - y) * W_half)
            iso_ys.append((x + y) * H_half - z * Z_step)
        min_x, max_x = min(iso_xs) - W_half, max(iso_xs) + W_half
        min_y, max_y = min(iso_ys) - H_half, max(iso_ys) + H_half + Z_step
        if zoom_val and zoom_val > 0:
            s = float(zoom_val)
        else:
            ext_x = max(1.0, max_x - min_x)
            ext_y = max(1.0, max_y - min_y)
            s = min(1.0, (img_w * 0.92) / ext_x, (img_h * 0.92) / ext_y)
        W_half *= s
        H_half *= s
        Z_step *= s
        origin_x = img_w // 2 - (min_x + max_x) * s / 2
        origin_y = img_h // 2 - (min_y + max_y) * s / 2

    for _, x, y, z, color_idx in visible_points:
        cx = origin_x + (x - y) * W_half
        cy = origin_y + (x + y) * H_half - z * Z_step
        base_rgb = get_rgb332_color(color_idx)
        
        top_col = apply_lighting(base_rgb, (0.0, 0.0, 1.0), light_vec, ambient_val)
        left_col = apply_lighting(base_rgb, (-0.707, 0.707, 0.0), light_vec, ambient_val)
        right_col = apply_lighting(base_rgb, (0.707, 0.707, 0.0), light_vec, ambient_val)

        # 陰影 (可開關): 被其他 voxel 擋住光 → 三面同乘暗係數
        if shadow_mode and is_shadowed(x, y, z, voxel_set, light_vec):
            top_col = apply_shadow(top_col)
            left_col = apply_shadow(left_col)
            right_col = apply_shadow(right_col)

        has_top_neighbor = (x, y, z + 1) in voxel_set
        has_left_neighbor = (x, y + 1, z) in voxel_set
        has_right_neighbor = (x + 1, y, z) in voxel_set

        pad = 0.5 if smooth_mode else 0.0

        if not has_top_neighbor:
            draw.polygon([
                (cx, cy - H_half - pad),
                (cx + W_half + pad, cy),
                (cx, cy + H_half + pad),
                (cx - W_half - pad, cy)
            ], fill=top_col, outline=top_col if smooth_mode else None)

        if not has_left_neighbor:
            draw.polygon([
                (cx - W_half - pad, cy),
                (cx, cy + H_half + pad),
                (cx, cy + H_half + Z_step + pad),
                (cx - W_half - pad, cy + Z_step + pad)
            ], fill=left_col, outline=left_col if smooth_mode else None)

        if not has_right_neighbor:
            draw.polygon([
                (cx, cy + H_half + pad),
                (cx + W_half + pad, cy),
                (cx + W_half + pad, cy + Z_step + pad),
                (cx, cy + H_half + Z_step + pad)
            ], fill=right_col, outline=right_col if smooth_mode else None)

    img.save(out_file)
    return out_file


# 區塊職責: 陰影判定 (Tim 2026-08-13 追加, 可開關) — 從 voxel 中心朝光源反方向做 voxel 行進,
#          途中撞到任何實心 voxel = 在陰影中 (面色乘暗係數)。
# 物理意義: 正交等角圖沒有影子會有深度歧義 (並排被誤讀成疊放 — Tim 實際誤讀過一次),
#          cast shadow 讓「誰在誰上面/前面」重新可判。
# 數值影響: O(可見voxel × 行進步數上限120); 千級 voxel 無感, 十萬級再考慮 shadow map 快取。
#          遮擋集合用 render 過濾後的 voxel_set — 被 exclude-color/region 排除的東西不投影
#          (「排除=不存在」語意一致)。
SHADOW_FACTOR = 0.55

def is_shadowed(x, y, z, voxel_set, light_vec):
    lx, ly, lz = light_vec
    mag = (lx * lx + ly * ly + lz * lz) ** 0.5
    if mag < 1e-6:
        return False
    # 朝光源方向 = light_dir 的反向
    sx, sy, sz = -lx / mag, -ly / mag, -lz / mag
    fx, fy, fz = x + 0.5, y + 0.5, z + 0.5
    step = 0.5
    for i in range(1, 241):  # 最遠 120 voxel
        fx += sx * step; fy += sy * step; fz += sz * step
        cx, cy, cz = int(fx), int(fy), int(fz)
        if not (0 <= cx <= 255 and 0 <= cy <= 255 and 0 <= cz <= 255):
            return False
        if (cx, cy, cz) == (x, y, z):
            continue
        if (cx, cy, cz) in voxel_set:
            return True
    return False


def apply_shadow(col):
    return tuple(int(c * SHADOW_FACTOR) for c in col)

def apply_lighting(rgb, face_normal, light_dir_vec, ambient=0.4):
    # Normalize light vector
    lx, ly, lz = light_dir_vec
    length = math.sqrt(lx*lx + ly*ly + lz*lz) or 1.0
    lx, ly, lz = -lx/length, -ly/length, -lz/length # Inverse to pointing towards light
    
    nx, ny, nz = face_normal
    dot = max(0.0, nx*lx + ny*ly + nz*lz)
    factor = ambient + (1.0 - ambient) * dot
    
    r = min(255, int(rgb[0] * factor))
    g = min(255, int(rgb[1] * factor))
    b = min(255, int(rgb[2] * factor))
    return (r, g, b)

def cmd_view(args):
    if Image is None:
        print("❌ 錯誤：缺 Pillow 庫，無法渲染 2.5D 畫面 (pip install Pillow)。")
        return 1

    space = load_space_state()
    voxels = space.voxels

    region_str = args.region
    exclude_color_str = args.exclude_color
    light_dir_str = args.light_dir or "-1,-1,-1"
    ambient_val = args.ambient if args.ambient is not None else 0.4
    smooth_mode = args.smooth
    shadow_mode = bool(getattr(args, 'shadow', False))
    zoom_val = getattr(args, 'zoom', None)

    # If --exhibit <id> is specified, auto-load its Preset!
    if args.exhibit:
        exhibits = load_exhibits()
        if args.exhibit in exhibits:
            preset = exhibits[args.exhibit]
            region_str = region_str or preset.get("region", "")
            exclude_color_str = exclude_color_str or preset.get("exclude_color", "")
            light_dir_str = preset.get("light_dir", light_dir_str)
            ambient_val = preset.get("ambient", ambient_val)
            if "smooth" in preset:
                smooth_mode = preset["smooth"]
            if "shadow" in preset and not shadow_mode:
                shadow_mode = bool(preset["shadow"])
            if preset.get("zoom") and not zoom_val:
                zoom_val = preset["zoom"]
            print(f"🏛️ 正在一鍵載入展品 [{args.exhibit}] 《{preset['title']}》 觀測與打光 Preset (創作者: {preset['author']})...")

    # Parse light dir
    try:
        light_vec = [float(v) for v in light_dir_str.split(",")]
    except Exception:
        light_vec = [-1.0, -1.0, -1.0]

    # Region Filter
    rx1, rx2, ry1, ry2, rz1, rz2 = 0, 255, 0, 255, 0, 255
    if region_str:
        try:
            parts = region_str.replace(" ", "").split(",")
            rx1, rx2 = [int(n) for n in parts[0].split("..")]
            ry1, ry2 = [int(n) for n in parts[1].split("..")]
            rz1, rz2 = [int(n) for n in parts[2].split("..")]
        except Exception:
            pass

    # Exclude Colors Filter
    exclude_colors = set()
    if exclude_color_str:
        try:
            exclude_colors = {int(c) for c in exclude_color_str.split(",")}
        except Exception:
            pass

    # Canvas Size & Exact Isometric Grid Parameters
    img_w, img_h = 1024, 1024
    bg_col = (15, 23, 42) # Default Dark Slate
    
    img = Image.new("RGB", (img_w, img_h), bg_col)
    draw = ImageDraw.Draw(img)

    origin_x = img_w // 2
    origin_y = img_h // 2 + 150

    # Grid Constants for Golden Ratio 3D Seamless Isometric Projection
    W_half = 12  # Half Width of Isometric Rhombus (12px -> Full Width 24px)
    H_half = 6   # Half Height of Isometric Rhombus (6px -> Full Height 12px)
    Z_step = 12  # Exact Height of Voxel Wall (Z_step == 2 * H_half for perfect alignment)

    visible_points = []
    voxel_set = set()
    for key, color_idx in voxels.items():
        if color_idx in exclude_colors:
            continue
        parts = key.split(",")
        x, y, z = int(parts[0]), int(parts[1]), int(parts[2])
        if rx1 <= x <= rx2 and ry1 <= y <= ry2 and rz1 <= z <= rz2:
            # Depth sorting: z first (bottom to top), then x+y (back to front)
            depth = z * 10000 + (x + y)
            visible_points.append((depth, x, y, z, color_idx))
            voxel_set.add((x, y, z))

    visible_points.sort(key=lambda item: item[0])

    # 自動置中＋自動縮放 (2026-08-13; --zoom 可覆寫): 依投影包圍盒縮放置中 —
    # 固定縮放時大場景會爆出畫布 (Tim 實測), 自動模式「只縮不放大」把整個場景收進畫面;
    # --zoom 給定時用指定倍率 (1.0 = 原始 24px/voxel, <1 縮小, >1 放大特寫)。
    if visible_points:
        iso_xs = []
        iso_ys = []
        for _, x, y, z, _c in visible_points:
            iso_xs.append((x - y) * W_half)
            iso_ys.append((x + y) * H_half - z * Z_step)
        min_x, max_x = min(iso_xs) - W_half, max(iso_xs) + W_half
        min_y, max_y = min(iso_ys) - H_half, max(iso_ys) + H_half + Z_step
        if zoom_val and zoom_val > 0:
            s = float(zoom_val)
        else:
            ext_x = max(1.0, max_x - min_x)
            ext_y = max(1.0, max_y - min_y)
            s = min(1.0, (img_w * 0.92) / ext_x, (img_h * 0.92) / ext_y)
        W_half *= s
        H_half *= s
        Z_step *= s
        origin_x = img_w // 2 - (min_x + max_x) * s / 2
        origin_y = img_h // 2 - (min_y + max_y) * s / 2

    for _, x, y, z, color_idx in visible_points:
        # Exact Center of Top Face Rhombus
        cx = origin_x + (x - y) * W_half
        cy = origin_y + (x + y) * H_half - z * Z_step
        
        base_rgb = get_rgb332_color(color_idx)
        
        top_col = apply_lighting(base_rgb, (0.0, 0.0, 1.0), light_vec, ambient_val)
        left_col = apply_lighting(base_rgb, (-0.707, 0.707, 0.0), light_vec, ambient_val)
        right_col = apply_lighting(base_rgb, (0.707, 0.707, 0.0), light_vec, ambient_val)

        # 陰影 (可開關): 被其他 voxel 擋住光 → 三面同乘暗係數
        if shadow_mode and is_shadowed(x, y, z, voxel_set, light_vec):
            top_col = apply_shadow(top_col)
            left_col = apply_shadow(left_col)
            right_col = apply_shadow(right_col)

        # Occlusion Checks (Strict Expose Check)
        has_top_neighbor = (x, y, z + 1) in voxel_set
        has_left_neighbor = (x, y + 1, z) in voxel_set
        has_right_neighbor = (x + 1, y, z) in voxel_set

        # Overlap offset to eliminate any sub-pixel raster gaps
        pad = 0.5 if smooth_mode else 0.0

        # 1. Render Top Face (if no voxel on top)
        if not has_top_neighbor:
            draw.polygon([
                (cx, cy - H_half - pad),
                (cx + W_half + pad, cy),
                (cx, cy + H_half + pad),
                (cx - W_half - pad, cy)
            ], fill=top_col, outline=top_col if smooth_mode else None)

        # 2. Render Left Face (if no voxel on left / x-1)
        if not has_left_neighbor:
            draw.polygon([
                (cx - W_half - pad, cy),
                (cx, cy + H_half + pad),
                (cx, cy + H_half + Z_step + pad),
                (cx - W_half - pad, cy + Z_step + pad)
            ], fill=left_col, outline=left_col if smooth_mode else None)

        # 3. Render Right Face (if no voxel on right / y-1)
        if not has_right_neighbor:
            draw.polygon([
                (cx, cy + H_half + pad),
                (cx + W_half + pad, cy),
                (cx + W_half + pad, cy + Z_step + pad),
                (cx, cy + H_half + Z_step + pad)
            ], fill=right_col, outline=right_col if smooth_mode else None)

    out_file = get_sculpt_dir() / "_last_view.png"
    img.save(out_file)
    
    print(f"# 🎨 3D Isometric View Rendered (Exact Grid, Smooth={smooth_mode})!")
    print(f"  total_voxels   : {len(voxels)}")
    print(f"  visible_rendered: {len(visible_points)}")
    print(f"  output_path     : {out_file}")


# 區塊職責: 模型匯出 (Tim 2026-08-13 追加) — 觀測區域 → .obj(+.mtl) / MagicaVoxel .vox
# 物理意義: docstring 一直宣稱有 exporter 但 CLI 沒有入口 (名字比事實大) — 本段補實。
#          只匯出 region 內、未被 exclude-color 濾掉的 voxel; 面剔除同 render 語意
#          (六方向鄰居存在即不出面 — Minecraft 式 culling, 匯出檔輕量)。
# 數值影響: obj 座標 = 世界座標 (Y-up 慣例: 世界 z(高) 寫到 obj y); vox 座標平移到 region 原點
#          (MagicaVoxel 單模型上限 256^3, region 裁剪後必在範圍內)。純新增檔案, 不動空間狀態。

def _filtered_voxels(space, region_str, exclude_color_str):
    rx1, rx2, ry1, ry2, rz1, rz2 = 0, 255, 0, 255, 0, 255
    if region_str:
        try:
            parts = region_str.replace(" ", "").split(",")
            rx1, rx2 = [int(n) for n in parts[0].split("..")]
            ry1, ry2 = [int(n) for n in parts[1].split("..")]
            rz1, rz2 = [int(n) for n in parts[2].split("..")]
        except Exception:
            pass
    exclude_colors = set()
    if exclude_color_str:
        try:
            exclude_colors = {int(c) for c in exclude_color_str.split(",")}
        except Exception:
            pass
    out = {}
    for key, color_idx in space.voxels.items():
        if color_idx in exclude_colors:
            continue
        x, y, z = map(int, key.split(","))
        if rx1 <= x <= rx2 and ry1 <= y <= ry2 and rz1 <= z <= rz2:
            out[(x, y, z)] = color_idx
    return out


# 六個面: (鄰居偏移, 4 頂點 offset — 以 voxel min 角為原點)
_FACES = [
    ((1, 0, 0),  [(1,0,0),(1,1,0),(1,1,1),(1,0,1)]),
    ((-1, 0, 0), [(0,0,1),(0,1,1),(0,1,0),(0,0,0)]),
    ((0, 1, 0),  [(1,1,0),(0,1,0),(0,1,1),(1,1,1)]),
    ((0, -1, 0), [(0,0,0),(1,0,0),(1,0,1),(0,0,1)]),
    ((0, 0, 1),  [(0,0,1),(1,0,1),(1,1,1),(0,1,1)]),
    ((0, 0, -1), [(0,1,0),(1,1,0),(1,0,0),(0,0,0)]),
]


def cmd_export(args):
    space = load_space_state()
    vox = _filtered_voxels(space, args.region, args.exclude_color)
    if not vox:
        print("⚠ 觀測區域內沒有任何 voxel — 沒東西可匯出")
        return 1
    fmt = args.format
    out_dir = get_sculpt_dir() / "exports"
    out_dir.mkdir(parents=True, exist_ok=True)
    stamp = datetime.now().strftime("%Y%m%dT%H%M%S")
    out_path = Path(args.out) if args.out else out_dir / f"sculpt_{stamp}.{fmt}"

    if fmt == "obj":
        used_colors = sorted({c for c in vox.values()})
        mtl_path = out_path.with_suffix(".mtl")
        with open(mtl_path, "w", encoding="utf-8") as mf:
            for c in used_colors:
                r, g, b = get_rgb332_color(c)
                mf.write(f"newmtl c{c}\nKd {r/255:.4f} {g/255:.4f} {b/255:.4f}\n")
        # 繞序自我驗證 (Tim 2026-08-13 Unity 實測 backface culling 抓出翻面):
        # 世界(z-up)→OBJ(y-up) 交換兩軸=鏡像, 手排頂點表的手性會全翻 —— 不靠手排,
        # 每面用叉積驗「法線是否朝外」, 不合就反轉頂點序; 並寫出 vn 供引擎直接用。
        def _obj_coord(wx, wy, wz):
            return (wx, wz, wy)   # OBJ y-up

        face_count = 0
        with open(out_path, "w", encoding="utf-8") as f:
            f.write(f"mtllib {mtl_path.name}\n")
            vi = 1
            ni = 1
            for c in used_colors:
                f.write(f"usemtl c{c}\n")
                for (x, y, z), color in vox.items():
                    if color != c:
                        continue
                    for (dx, dy, dz), corners in _FACES:
                        if (x + dx, y + dy, z + dz) in vox:
                            continue  # 被鄰居遮住的面不出 (culling)
                        pts = [_obj_coord(x + ox, y + oy, z + oz) for (ox, oy, oz) in corners]
                        nrm = _obj_coord(dx, dy, dz)   # 外向法線 (OBJ 座標)
                        # 叉積 (p1-p0)×(p2-p0) 與外向同向 = CCW 正確; 反向 = 反轉頂點序
                        ux, uy, uz = (pts[1][0]-pts[0][0], pts[1][1]-pts[0][1], pts[1][2]-pts[0][2])
                        vx, vy, vz = (pts[2][0]-pts[0][0], pts[2][1]-pts[0][1], pts[2][2]-pts[0][2])
                        cxn = (uy*vz - uz*vy, uz*vx - ux*vz, ux*vy - uy*vx)
                        if cxn[0]*nrm[0] + cxn[1]*nrm[1] + cxn[2]*nrm[2] < 0:
                            pts.reverse()
                        for (px, py, pz) in pts:
                            f.write(f"v {px} {py} {pz}\n")
                        f.write(f"vn {nrm[0]} {nrm[1]} {nrm[2]}\n")
                        f.write(f"f {vi}//{ni} {vi+1}//{ni} {vi+2}//{ni} {vi+3}//{ni}\n")
                        vi += 4
                        ni += 1
                        face_count += 1
        print("# 📦 OBJ 匯出完成")
        print(f"  voxels    : {len(vox)}")
        print(f"  faces     : {face_count} (culled)")
        print(f"  obj       : {out_path}")
        print(f"  mtl       : {mtl_path}")
        return 0

    if fmt == "vox":
        import struct
        xs = [k[0] for k in vox]; ys = [k[1] for k in vox]; zs = [k[2] for k in vox]
        mnx, mny, mnz = min(xs), min(ys), min(zs)
        sx, sy, sz = max(xs)-mnx+1, max(ys)-mny+1, max(zs)-mnz+1
        if max(sx, sy, sz) > 256:
            print(f"⚠ region 尺寸 {sx}x{sy}x{sz} 超過 MagicaVoxel 單模型上限 256 — 縮小觀測區域再匯")
            return 1
        xyzi = b"".join(struct.pack("<4B", k[0]-mnx, k[1]-mny, k[2]-mnz, c if c > 0 else 1)
                        for k, c in vox.items())
        def chunk(cid, content, children=b""):
            return cid + struct.pack("<ii", len(content), len(children)) + content + children
        size_c = chunk(b"SIZE", struct.pack("<3i", sx, sy, sz))
        xyzi_c = chunk(b"XYZI", struct.pack("<i", len(vox)) + xyzi)
        pal = b""
        for i in range(1, 257):
            r, g, b = get_rgb332_color(i % 256)
            pal += struct.pack("<4B", r, g, b, 255)
        rgba_c = chunk(b"RGBA", pal)
        main = chunk(b"MAIN", b"", size_c + xyzi_c + rgba_c)
        with open(out_path, "wb") as f:
            f.write(b"VOX " + struct.pack("<i", 150) + main)
        print("# 📦 VOX 匯出完成 (MagicaVoxel)")
        print(f"  voxels    : {len(vox)}  size: {sx}x{sy}x{sz}")
        print(f"  vox       : {out_path}")
        return 0

    print(f"❌ 未知格式: {fmt}")
    return 2


def cmd_stats(args):
    space = load_space_state()
    voxels = space.voxels
    print(f"# 📊 3D Sculpture Stats:")
    print(f"  總非空 Voxels 數 : {len(voxels)}")
    print(f"  空間使用率       : {len(voxels) / (256**3) * 100:.6f}%")

def main():
    if hasattr(sys.stdout, "reconfigure"):
        try:
            sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass

    parser = argparse.ArgumentParser(description="3D Voxel Sculpture Engine")
    subparsers = parser.add_subparsers(dest="command")

    # box
    p_box = subparsers.add_parser("box", help="放置 AABB 體積方塊 (非覆蓋)")
    p_box.add_argument("--x1", type=int, required=True)
    p_box.add_argument("--x2", type=int, required=True)
    p_box.add_argument("--y1", type=int, required=True)
    p_box.add_argument("--y2", type=int, required=True)
    p_box.add_argument("--z1", type=int, required=True)
    p_box.add_argument("--z2", type=int, required=True)
    p_box.add_argument("--color", type=int, default=19)
    p_box.add_argument("--persona", required=True)
    p_box.set_defaults(func=cmd_box)

    # carve
    p_carve = subparsers.add_parser("carve", help="雕刻/移除指定 3D 空間方塊")
    p_carve.add_argument("--x1", type=int, required=True)
    p_carve.add_argument("--x2", type=int, required=True)
    p_carve.add_argument("--y1", type=int, required=True)
    p_carve.add_argument("--y2", type=int, required=True)
    p_carve.add_argument("--z1", type=int, required=True)
    p_carve.add_argument("--z2", type=int, required=True)
    p_carve.add_argument("--persona", required=True)
    p_carve.set_defaults(func=cmd_carve)

    # stamp2d
    p_st = subparsers.add_parser("stamp2d", help="把 2D 共用畫布某區域貼進 3D (未繪製=空, 不放 voxel)")
    p_st.add_argument("--src-x1", type=int, required=True, dest="src_x1")
    p_st.add_argument("--src-y1", type=int, required=True, dest="src_y1")
    p_st.add_argument("--src-x2", type=int, required=True, dest="src_x2")
    p_st.add_argument("--src-y2", type=int, required=True, dest="src_y2")
    p_st.add_argument("--at", required=True, help="3D 錨點 'x,y,z' (來源區域左上角貼在這)")
    p_st.add_argument("--facing", default="z+", help="貼片法線: x+ x- y+ y- z+ z- (預設 z+)")
    p_st.add_argument("--thickness", type=int, default=1, help="沿法線擠出層數 (預設 1)")
    p_st.add_argument("--overwrite", action="store_true", help="覆蓋既有 voxel (預設跳過並回報)")
    p_st.add_argument("--persona", required=True)
    _add_stamp_common_args(p_st)
    p_st.set_defaults(func=cmd_stamp2d)

    # slice — 3D 切片輸出 2D PNG（stamp 的逆運算，共用同一組軸映射 ⇒ 可往返）
    p_sl = subparsers.add_parser("slice", help="把 region 內的 voxel 沿軸壓成 2D PNG (空=透明, 厚度>1 前覆蓋後)")
    p_sl.add_argument("--region", required=True, help="x1..x2,y1..y2,z1..z2（法線軸的跨度＝厚度，寫 10..10 就是 1）")
    p_sl.add_argument("--axis", default="z+", help="投影法線與近端方向: x+ x- y+ y- z+ z- (預設 z+；'+'＝近端是較小那端)")
    p_sl.add_argument("--out", default=None, help="輸出 PNG 路徑（省略＝Sculpture/_last_slice.png）")
    p_sl.set_defaults(func=cmd_slice)

    # stampimg — 任意 PNG 貼進 3D（透明像素不畫入；與 stamp2d 共用投影核心）
    p_si = subparsers.add_parser("stampimg", help="把一張 PNG 貼進 3D (透明像素=空, 不放 voxel)")
    p_si.add_argument("--png", required=True, help="來源 PNG（RGBA；非 RGBA 會被轉檔，全不透明＝整張都畫）")
    p_si.add_argument("--at", required=True, help="3D 錨點 'x,y,z' (圖左上角貼在這)")
    p_si.add_argument("--facing", default="z+", help="貼片法線: x+ x- y+ y- z+ z- (預設 z+)")
    p_si.add_argument("--thickness", type=int, default=1, help="沿法線擠出層數 (預設 1)")
    p_si.add_argument("--overwrite", action="store_true", help="覆蓋既有 voxel (預設跳過並回報)")
    p_si.add_argument("--resize", default=None, help="縮放到 'W,H'（NEAREST；插值會生出不存在的半透明邊）")
    p_si.add_argument("--persona", required=True)
    _add_stamp_common_args(p_si)
    p_si.set_defaults(func=cmd_stampimg)

    # view
    p_view = subparsers.add_parser("view", help="渲染 2.5D 等角視圖 (帶打光陰影)")
    p_view.add_argument("--region", help="空間範圍裁剪 (例如 '0..50,0..50,0..20')")
    p_view.add_argument("--exclude-color", help="排除顏色 (逗號分隔)")
    p_view.add_argument("--light-dir", help="平行日光照方向向量 (例如 '-1,-1,-1')")
    p_view.add_argument("--ambient", type=float, default=0.4, help="環境光強度 (0.0~1.0)")
    p_view.add_argument("--smooth", action="store_true", help="開啟平滑表面模式 (自動消除相鄰同平面內部網格線)")
    p_view.add_argument("--shadow", action="store_true", help="開啟 cast shadow (被其他 voxel 擋光的面變暗 — 解正交圖深度歧義)")
    p_view.add_argument("--zoom", type=float, default=None, help="觀測距離/縮放倍率 (1.0=原始 24px/voxel; 省略=自動縮放收進畫布)")
    p_view.add_argument("--exhibit", help="一鍵載入指定展品 ID 的觀看與打光 Preset")
    p_view.set_defaults(func=cmd_view)

    # exhibit
    p_ex = subparsers.add_parser("exhibit", help="3D 展品標記與導覽設定")
    p_ex_sub = p_ex.add_subparsers(dest="ex_op")
    
    p_ex_reg = p_ex_sub.add_parser("register", help="登錄新展品觀看與打光設定")
    p_ex_reg.add_argument("--id", required=True, help="展品唯一 ID")
    p_ex_reg.add_argument("--title", required=True, help="展品標題")
    p_ex_reg.add_argument("--author", required=True, help="創作者")
    p_ex_reg.add_argument("--desc", help="展品理念介紹")
    p_ex_reg.add_argument("--region", help="最佳觀測空間裁剪")
    p_ex_reg.add_argument("--shadow", action="store_true", help="展品照與導覽預設開陰影")
    p_ex_reg.add_argument("--zoom", type=float, default=None, help="展品預設觀測縮放 (省略=自動)")
    p_ex_reg.add_argument("--exclude-color", help="排除顏色")
    p_ex_reg.add_argument("--bg-color", help="背景顏色")
    p_ex_reg.add_argument("--skybox", help="Skybox 貼圖路徑")
    p_ex_reg.add_argument("--light-dir", help="最佳打光方向 (例如 '-1,-1,-1')")
    p_ex_reg.add_argument("--ambient", type=float, default=0.4, help="環境光強度 (0.0~1.0)")
    p_ex_reg.add_argument("--smooth", action="store_true", help="預設啟用平滑表面模式")
    
    p_ex_list = p_ex_sub.add_parser("list", help="列出全館所有登錄展品")
    
    p_ex.set_defaults(func=cmd_exhibit)

    # stats
    p_export = subparsers.add_parser("export", help="匯出觀測區域為 3D 模型檔 (.obj+.mtl / MagicaVoxel .vox)")
    p_export.add_argument("--format", required=True, choices=["obj", "vox"])
    p_export.add_argument("--region", help="觀測區域裁剪 (x1..x2,y1..y2,z1..z2; 省略=全空間)")
    p_export.add_argument("--exclude-color", help="排除顏色 (逗號分隔)")
    p_export.add_argument("--out", help="輸出路徑 (省略=Sculpture/exports/sculpt_<ts>.<fmt>)")
    p_export.set_defaults(func=cmd_export)

    p_stats = subparsers.add_parser("stats", help="顯示統計資訊")
    p_stats.set_defaults(func=cmd_stats)

    args = parser.parse_args()
    if not args.command:
        parser.print_help()
        return 0
    return args.func(args)

if __name__ == "__main__":
    sys.exit(main() or 0)
