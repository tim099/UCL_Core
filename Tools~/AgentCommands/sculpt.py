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
"""

import os
import sys
import glob
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

    for _, x, y, z, color_idx in visible_points:
        cx = origin_x + (x - y) * W_half
        cy = origin_y + (x + y) * H_half - z * Z_step
        base_rgb = get_rgb332_color(color_idx)
        
        top_col = apply_lighting(base_rgb, (0.0, 0.0, 1.0), light_vec, ambient_val)
        left_col = apply_lighting(base_rgb, (-0.707, 0.707, 0.0), light_vec, ambient_val)
        right_col = apply_lighting(base_rgb, (0.707, 0.707, 0.0), light_vec, ambient_val)

        has_top_neighbor = (x, y, z + 1) in voxel_set
        has_left_neighbor = (x - 1, y, z) in voxel_set
        has_right_neighbor = (x, y - 1, z) in voxel_set

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

    for _, x, y, z, color_idx in visible_points:
        # Exact Center of Top Face Rhombus
        cx = origin_x + (x - y) * W_half
        cy = origin_y + (x + y) * H_half - z * Z_step
        
        base_rgb = get_rgb332_color(color_idx)
        
        top_col = apply_lighting(base_rgb, (0.0, 0.0, 1.0), light_vec, ambient_val)
        left_col = apply_lighting(base_rgb, (-0.707, 0.707, 0.0), light_vec, ambient_val)
        right_col = apply_lighting(base_rgb, (0.707, 0.707, 0.0), light_vec, ambient_val)

        # Occlusion Checks (Strict Expose Check)
        has_top_neighbor = (x, y, z + 1) in voxel_set
        has_left_neighbor = (x - 1, y, z) in voxel_set
        has_right_neighbor = (x, y - 1, z) in voxel_set

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

    # view
    p_view = subparsers.add_parser("view", help="渲染 2.5D 等角視圖 (帶打光陰影)")
    p_view.add_argument("--region", help="空間範圍裁剪 (例如 '0..50,0..50,0..20')")
    p_view.add_argument("--exclude-color", help="排除顏色 (逗號分隔)")
    p_view.add_argument("--light-dir", help="平行日光照方向向量 (例如 '-1,-1,-1')")
    p_view.add_argument("--ambient", type=float, default=0.4, help="環境光強度 (0.0~1.0)")
    p_view.add_argument("--smooth", action="store_true", help="開啟平滑表面模式 (自動消除相鄰同平面內部網格線)")
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
    p_ex_reg.add_argument("--exclude-color", help="排除顏色")
    p_ex_reg.add_argument("--bg-color", help="背景顏色")
    p_ex_reg.add_argument("--skybox", help="Skybox 貼圖路徑")
    p_ex_reg.add_argument("--light-dir", help="最佳打光方向 (例如 '-1,-1,-1')")
    p_ex_reg.add_argument("--ambient", type=float, default=0.4, help="環境光強度 (0.0~1.0)")
    p_ex_reg.add_argument("--smooth", action="store_true", help="預設啟用平滑表面模式")
    
    p_ex_list = p_ex_sub.add_parser("list", help="列出全館所有登錄展品")
    
    p_ex.set_defaults(func=cmd_exhibit)

    # stats
    p_stats = subparsers.add_parser("stats", help="顯示統計資訊")
    p_stats.set_defaults(func=cmd_stats)

    args = parser.parse_args()
    if not args.command:
        parser.print_help()
        return 0
    return args.func(args)

if __name__ == "__main__":
    sys.exit(main() or 0)
