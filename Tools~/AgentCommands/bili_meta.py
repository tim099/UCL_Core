#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
bili_meta.py — 給一條 bilibili 連結，印出這支影片的公開 metadata（標題／up 主／簡介…）。

用途：**觀影準備階段的取資訊工具**（TASK-0067）。

⛔ 本工具**唯讀**：只發一個 GET、只印報告。
   它**不寫任何檔**、**不代填任何參數**、**不生成 media/work slug**。
   （Tim 2026-08-26 拍板：「工具主要是用在自動取資訊，實際填資訊仍然由主觀影者負責」。）
   ⇒ 所以它的輸出分兩段：**查到的**（唯讀讀數）與**你還要自己帶的**（指令草稿，人自己按）。

為什麼這些守衛存在（每一條都有讀數，不是預防性設計）：
  - **UA 必填**：不帶 User-Agent 打 view API 會回 **HTTP 412**（風控），
    而那長得像「端點壞了」。同族血證：Plurk 的 Cloudflare 1010 擋 python-urllib 預設 UA。
  - **判定看 `code`，不看 HTTP 狀態**：不存在的 BV 會回 **HTTP 200 + `code=-400`** ——
    **失敗長得像成功**。只看 http 200 就會把空字串當標題用。
  - **`-400` 不可分辨**：「這支影片不存在」與「BV 格式錯」回的是**同一個 code 同一句 message**。
    ⇒ 本工具照實說「B 站沒告訴我是哪一種」，不替它挑一個聽起來順的。
  - **欄位三態**：`desc` 可能是真簡介／作者留的 `-`／空字串／欄位缺。
    ⇒ 「查到了但欄位是空的」與「沒查到」**不可同形**（每一格分別標）。

用法：
  python bili_meta.py https://www.bilibili.com/video/BV1PiuJ6XEQ4
  python bili_meta.py BV1PiuJ6XEQ4                 # 直接給 BV 號也行
  python bili_meta.py https://b23.tv/xxxxxxx       # 短連結（跟隨 redirect 取 BV）
  python bili_meta.py https://www.bilibili.com/video/av117048143319325   # av 號走 aid 查
  python bili_meta.py <url> --format json          # 機器讀（原樣附 API 的 data）
  python bili_meta.py <url> --timeout 30

Exit codes：
  0 = 成功（`code=0`，讀數已印）
  2 = 輸入解析不出 BV／av 號（不是網路問題）
  4 = API 回應 `code != 0`（原樣印 code 與 message）
  5 = 網路層失敗（逾時／HTTP 4xx/5xx／DNS）—— 與 4 分開，因為處置不同
"""

import argparse
import json
import re
import sys
import urllib.error
import urllib.request
from pathlib import Path

# Force UTF-8 stdout/stderr on Windows (default cp950 chokes on 中文/emoji)
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

VIEW_API = "https://api.bilibili.com/x/web-interface/view?bvid={bvid}"
VIEW_API_AID = "https://api.bilibili.com/x/web-interface/view?aid={aid}"

# 區塊職責：自報身分。
# 物理意義：不帶 UA → HTTP 412。這不是「B 站擋 agent」，是我們的 client 沒自報身分。
HEADERS = {
    "User-Agent": (
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
        "(KHTML, like Gecko) Chrome/126.0 Safari/537.36"
    ),
    "Referer": "https://www.bilibili.com/",
}

BV_RE = re.compile(r"BV[0-9A-Za-z]{10}")
# ⚠ av 號網址（`/video/av123…`）**不會**被 B 站 redirect 成 BV —— 實測跟到最後 URL 仍是 av。
#   而 view API 本身吃 `aid=` ⇒ 這種輸入直接走 aid 查，不要假裝解析失敗。
AV_RE = re.compile(r"(?:^|/)av(\d{1,20})(?:[/?#]|$)", re.IGNORECASE)


# ---------------------------------------------------------------- BV 解析

def _noise_note(text: str, m: "re.Match") -> str:
    """輸入不乾淨就**明說**（不擋）。

    🩸 QA 抓到（basecamp 2026-08-27）：`BV1vM8P6EEDYX`（多一個 X）會被 `search` 取前 12 碼、
    回一支**真的影片**、而**零警告** —— 「你問了一個有雜訊的問題，拿到一個乾淨的答案，
    而沒有讀數告訴你問題被改過」。最壞情況是截斷後命中**另一支**合法影片。
    ⇒ 判準：不必擋，但**必須說**。BV 是定長 12，所以緊鄰的英數字元就是雜訊。
    """
    left = text[m.start() - 1] if m.start() > 0 else ""
    right = text[m.end():m.end() + 8]
    extra = []
    if left.isalnum():
        extra.append(f"左側緊鄰 `{left}`")
    if right[:1].isalnum():
        extra.append(f"右側緊鄰 `{right}`")
    if not extra:
        return ""
    return (f"　⚠ **輸入不乾淨**：我用的是這 12 碼 `{m.group(0)}`，"
            f"但{('、'.join(extra))} —— BV 是定長 12，多出來的字元被我忽略了。"
            "**請確認這是你要的那一支**（截斷後仍可能命中另一支合法影片）。")



def extract_id(text: str, timeout: int) -> tuple[str, str, str]:
    """回傳 (kind, id, 怎麼拿到的)；kind ∈ {"bvid", "aid", ""}。

    ⚠ 短連結（b23.tv）本身不含 BV ⇒ 必須跟隨 redirect 才看得到。
    ⚠ av 號網址跟到最後仍是 av（B 站不轉），所以 av 走 aid 查 —— 兩條路都通到同一個 view API。
    """
    text = (text or "").strip()
    m = BV_RE.search(text)
    if m:
        return "bvid", m.group(0), "直接從輸入字串取得" + _noise_note(text, m)
    m = AV_RE.search(text)
    if m:
        return "aid", m.group(1), "從輸入字串取得 av 號（view API 吃 aid）"

    if "b23.tv" in text or text.startswith("http"):
        try:
            req = urllib.request.Request(text, headers=HEADERS)
            with urllib.request.urlopen(req, timeout=timeout) as r:
                final = r.url or ""
        except Exception as e:                                    # noqa: BLE001
            return "", "", f"跟隨短連結失敗：{type(e).__name__} {e}"
        m = BV_RE.search(final)
        if m:
            return "bvid", m.group(0), f"跟隨 redirect 取得（最終 URL：{final}）"
        m = AV_RE.search(final)
        if m:
            return "aid", m.group(1), f"跟隨 redirect 取得 av 號（最終 URL：{final}）"
        return "", "", f"跟隨 redirect 之後仍找不到 BV／av 號（最終 URL：{final}）"

    return "", "", "輸入既不是 BV／av 號也不是網址"


# ---------------------------------------------------------------- API

def fetch_view(kind: str, ident: str, timeout: int) -> dict:
    """打 view API。回 dict：{ok, http, code, message, data, error}

    ⚠ 網路層失敗（exit 5）與 API 說不行（exit 4）**分開回報** —— 兩者處置不同。
    """
    url = VIEW_API.format(bvid=ident) if kind == "bvid" else VIEW_API_AID.format(aid=ident)
    try:
        req = urllib.request.Request(url, headers=HEADERS)
        with urllib.request.urlopen(req, timeout=timeout) as r:
            http = r.status
            body = r.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as e:
        hint = ""
        if e.code == 412:
            hint = "（412 = 風控擋下。最常見原因是沒帶 User-Agent —— 本工具有帶，若仍 412 代表被更上層擋）"
        return {"ok": False, "layer": "net", "http": e.code,
                "error": f"HTTPError {e.code} {e.reason}{hint}"}
    except Exception as e:                                        # noqa: BLE001
        return {"ok": False, "layer": "net", "http": None,
                "error": f"{type(e).__name__}: {e}"}

    try:
        d = json.loads(body)
    except Exception as e:                                        # noqa: BLE001
        return {"ok": False, "layer": "net", "http": http,
                "error": f"回應不是 JSON（{type(e).__name__}）；前 120 字：{body[:120]!r}"}

    code = d.get("code")
    if code != 0:
        return {"ok": False, "layer": "api", "http": http,
                "code": code, "message": d.get("message"), "raw": d}
    return {"ok": True, "http": http, "code": code,
            "message": d.get("message"), "data": d.get("data") or {}, "raw": d}


# ---------------------------------------------------------------- 欄位三態

def field(data: dict, key: str) -> str:
    """把一個欄位印成**看得出狀態**的字串。

    ⚠ 「查到了但欄位是空的」與「沒查到」不可同形 —— 這裡是那條判準的落點。
    """
    if key not in data:
        return "⚠ （欄位缺 —— API 回應裡根本沒有這個 key）"
    v = data.get(key)
    if v is None:
        return "⚠ （值是 null）"
    if isinstance(v, str):
        if v == "":
            return "⚠ （空字串 —— 查到了，但這格是空的）"
        if v.strip() in {"-", "－"}:
            return f"⚠ `{v}` （**作者沒寫簡介**，這不是簡介內容）"
        return v
    return str(v)


def hhmmss(sec) -> str:
    try:
        s = int(sec)
    except Exception:                                             # noqa: BLE001
        return "?"
    return f"{s // 3600:d}:{(s % 3600) // 60:02d}:{s % 60:02d}" if s >= 3600 else f"{s // 60:d}:{s % 60:02d}"


# ---------------------------------------------------------------- 既有 work 清單（唯讀）

def existing_bilibili_works() -> tuple[list[str], str]:
    """列出閱讀庫裡既有的 `bilibili-*` work。**只讀不寫。**

    給人挑 slug 用 —— 本工具**不生 slug**（自動生 slug 會長出平行宇宙，BUG-39 血證）。
    """
    try:
        sys.path.insert(0, str(Path(__file__).resolve().parent))
        from _lib.ucl_paths import data_root                       # 唯一路徑入口，不自己拼
        works = Path(data_root()) / "BookNotes" / "Library" / "works"
    except Exception as e:                                        # noqa: BLE001
        return [], f"（讀不到既有 work 清單：{type(e).__name__} {e}）"
    if not works.is_dir():
        return [], f"（works 目錄不存在：{works}）"
    hits = sorted(p.name for p in works.iterdir() if p.is_dir() and p.name.startswith("bilibili"))
    return hits, ""


# ---------------------------------------------------------------- 報告

def render_md(bvid: str, how: str, res: dict) -> str:
    d = res["data"]
    owner = d.get("owner") or {}
    pages = d.get("pages") or []
    works, works_note = existing_bilibili_works()

    L = []
    L.append(f"# bilibili 取資訊 — `{bvid}`")
    L.append("")
    L.append(f"- BV 號來源：{how}")
    L.append(f"- 端點：`GET /x/web-interface/view`　http **{res['http']}**　`code={res['code']}`　message=`{res['message']}`")
    L.append("")
    L.append("## ① 查到的（唯讀 API 讀數 —— 原樣，未經改寫）")
    L.append("")
    L.append(f"- **影片標題**：{field(d, 'title')}")
    L.append(f"- **up 主**：{field(owner, 'name')}　／　**mid**：{field(owner, 'mid')}"
             "　←　mid 才是穩定鍵（up 主名會改）")
    L.append(f"- **簡介 desc**：{field(d, 'desc')}")
    L.append(f"- **長度**：{field(d, 'duration')} 秒（{hhmmss(d.get('duration'))}）")
    L.append(f"- **發佈時間 pubdate**：{field(d, 'pubdate')}（unix）")
    L.append(f"- **分 P**：{len(pages)} 個（`videos={field(d, 'videos')}`）")
    for p in pages[:20]:
        L.append(f"    - P{p.get('page')}　{p.get('part')}　{hhmmss(p.get('duration'))}")
    if len(pages) > 20:
        L.append(f"    - …（另有 {len(pages) - 20} 個未列）")
    L.append(f"- **封面 pic**：{field(d, 'pic')}")
    L.append("")
    L.append("## ② 你還要自己帶的（**工具不代填**）")
    L.append("")
    L.append("> Tim 2026-08-26 拍板：工具只取資訊，**填資訊仍由主觀影者負責**。")
    L.append("> ⇒ 下面是**草稿不是結果**：`<…>` 的部分沒有人替你決定，貼上去之前自己填。")
    L.append("")
    L.append("```bash")
    L.append("python <UCL_Core>/Tools~/AgentCommands/run_cmd.py --persona <me> run StreamWatch \\")
    L.append("    --arg step=prepare --arg title=<作品名> --arg episode=<第幾集> \\")
    L.append("    --arg media_id=<媒材 id —— 查既有、不發明>")
    L.append("```")
    L.append("")
    L.append("**work / media 鍵：本工具不生，也不建議一個具體字串。** 規則是 `bilibili-<up主 slug>`"
             "（一個 up 主＝一個 work），而 up 主名要怎麼轉成 slug 是**命名決策**，不是抓取。")
    if works:
        L.append("")
        L.append("既有的 `bilibili-*` work（命中就用，別另開）：")
        for w in works:
            L.append(f"- `{w}`")
    else:
        L.append("")
        L.append(f"既有的 `bilibili-*` work：**0 筆** {works_note}")
    L.append("")
    L.append("## ③ 本工具沒有做的事（講出來，免得被當成已經生效）")
    L.append("")
    L.append("- ⛔ **沒有寫任何檔**：`prepared/*.json`、session、Library 都沒碰（本工具只發一個 GET）。")
    L.append("- ⛔ **沒有生成任何 media/work slug**。")
    L.append("- ⛔ **場次還沒設定** —— 查到 ≠ 設定好。要開場請自己跑上面那行 prepare。")
    return "\n".join(L)


def render_fail_md(bvid: str, how: str, res: dict) -> str:
    L = [f"# bilibili 取資訊 — **失敗**", ""]
    if bvid:
        L.append(f"- BV 號：`{bvid}`（{how}）")
    if res.get("layer") == "net":
        L.append(f"- **失敗在網路層**：{res.get('error')}")
        L.append(f"- http：{res.get('http')}")
        L.append("")
        L.append("⇒ 這一格失敗的是**連線／風控**，不是「影片不存在」。兩者處置不同：")
        L.append("　前者重試或查網路，後者要換 BV 號。")
    else:
        L.append(f"- **API 回應 `code={res.get('code')}`**　message=`{res.get('message')}`　（http {res.get('http')} —— "
                 "⚠ **http 200 不代表成功**，判定看 code）")
        if res.get("code") == -400:
            L.append("")
            L.append("⚠ **`-400` 不可分辨**：「這支影片不存在」與「BV 號格式錯」B 站回的是"
                     "**同一個 code、同一句 message**（實測兩者皆為 `http 200 / code=-400 / 请求错误`）。")
            L.append("⇒ 本工具**不替它挑一個** —— 請自己確認那條連結是不是還活著。")
    L.append("")
    L.append("⛔ 本工具唯讀：這次失敗**沒有寫任何檔**，也沒有留下半套狀態。")
    return "\n".join(L)


def main() -> int:
    ap = argparse.ArgumentParser(description="給 bilibili 連結，印出影片公開 metadata（唯讀，不代填）")
    ap.add_argument("url", help="bilibili 網址、b23.tv 短連結，或直接給 BV 號")
    ap.add_argument("--format", choices=["md", "json"], default="md")
    ap.add_argument("--timeout", type=int, default=20)
    a = ap.parse_args()

    kind, ident, how = extract_id(a.url, a.timeout)
    if not kind:
        if a.format == "json":
            print(json.dumps({"ok": False, "layer": "input", "error": how}, ensure_ascii=False, indent=2))
        else:
            print(f"# bilibili 取資訊 — **解析不出 BV 號**\n\n- 輸入：`{a.url}`\n- 原因：{how}\n\n"
                  "⇒ 這一格失敗在**輸入解析**，還沒打過 API。")
        return 2

    res = fetch_view(kind, ident, a.timeout)
    # 用 API 回的 bvid 當顯示鍵（av 查進來時，這一格才是真正的 BV）
    bvid = (res.get("data") or {}).get("bvid") or (ident if kind == "bvid" else f"av{ident}")

    if a.format == "json":
        print(json.dumps(res.get("raw", res), ensure_ascii=False, indent=2))
    elif res["ok"]:
        print(render_md(bvid, how, res))
    else:
        print(render_fail_md(ident if kind == "bvid" else f"av{ident}", how, res))

    if res["ok"]:
        return 0
    return 5 if res.get("layer") == "net" else 4


if __name__ == "__main__":
    sys.exit(main())
