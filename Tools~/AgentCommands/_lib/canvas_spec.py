"""canvas_spec — 2D 共用畫布的**規格常數與調色盤規則**（python 這一份）。

區塊職責：只放「兩端必須逐字同值」的那幾格 —— 畫布尺寸、空白 index、RGB332 編解碼。
物理意義：這些規則有**兩個宿主**（C# `SCP_CanvasSpec` ／ 本檔），那是設計上的兩份，
         不是漂移；`SCP_CanvasSpec.cs` 的檔頭就寫著「與 python 的 CANVAS_W / CANVAS_H /
         BLANK_INDEX 必須逐字同值」。**改任何一格 = 兩端一起改。**
數值影響：純函式，零 IO。

⚠ 為什麼從 `canvas.py` 搬出來（2026-09-07，TASK-0114 canvas.py 退場的前置）：
  `sculpt.py` 的 `png_to_painted` 逐像素叫 `rgb_to_index`，那是**純函式、走不了 CLI**
  （一張圖幾十萬像素，不可能一顆一次派 Cmd）。
  而它原本是用絕對路徑把整個 `canvas.py` 載進來拿這一個函式 ——
  於是「刪掉 canvas.py」會連帶弄壞 3D 雕刻，而錯誤訊息會指向 sculpt。
  ⇒ 規則搬到這裡：**canvas.py 退場不會帶走它**，而 python 端仍然只有一份。

⛔ 不要在別處重寫一份量化 —— 那正是 2026-06-04 canvas drift bug 的形狀。
"""
from __future__ import annotations

# 畫布解析度（spec §2：2048×2048）
CANVAS_W = 2048
CANVAS_H = 2048

# 空白底色 index：RGB332 的 255 解碼出來是純白。
# ⚠ **255 同時是「純白」與「沒人畫過」** —— 這兩件事在顏色上分不開，
#   要分得靠 painted-mask（誰畫過的位元圖），不要拿顏色當判準。
BLANK_INDEX = 255


def index_to_rgb(i: int) -> tuple[int, int, int]:
    """RGB332 palette index → (r,g,b)。r=3bit(高位), g=3bit, b=2bit(低位)。"""
    r = ((i >> 5) & 0x7) * 255 // 7
    g = ((i >> 2) & 0x7) * 255 // 7
    b = (i & 0x3) * 255 // 3
    return (r, g, b)


def rgb_to_index(r: int, g: int, b: int) -> int:
    """(r,g,b) 量化到最近的 RGB332 index —— 四捨五入分桶（不是截斷）。"""
    rb = round(r / 255 * 7)   # 0..7（3 bit）
    gb = round(g / 255 * 7)   # 0..7（3 bit）
    bb = round(b / 255 * 3)   # 0..3（2 bit）
    return (rb << 5) | (gb << 2) | bb


def build_palette() -> list[tuple[int, int, int]]:
    """256 筆 RGB332 LUT（寫進 `_meta.json` 的 palette 欄）。"""
    return [index_to_rgb(i) for i in range(256)]
