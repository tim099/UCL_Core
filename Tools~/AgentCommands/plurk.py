#!/usr/bin/env python3
# 區塊職責：Plurk 串接的最底層 —— 帳號解析 ＋ OAuth 1.0a 簽章 ＋ **唯讀**端點驗證。
# 物理意義：Plan_Plurk_Bot §5 標了「精確端點與參數名未驗證（官方 API 頁抓取回 403）」。
#          所以本檔的第一個目的**不是發文**，是回答「憑證與簽章到底通不通」——
#          而那只能拿唯讀端點實跑，不能靠讀文件推論。
# 數值影響：`resolve` 不連網、零副作用；`whoami` 只打唯讀端點（不寫任何 Plurk 資料）。
#          **本檔沒有發文 op** —— 發文是 Plan 的 Phase 2，且發布不可回復（Plurk 沒有 history）。
#
# ⛔ 憑證紀律（硬界線，不是偏好）：
#   · 本檔**只讀已解密的明文** secret，不做加解密、不碰 passphrase（那是 Secret Manager 的事）。
#   · **任何情況都不印憑證值** —— 只印 secret id、欄位是否到齊、以及長度。
#     憑證外洩沒有錯誤訊息，而 log／對話／訊息都可能被保留或轉述。
#
# 路徑：一律走 `_lib/ucl_paths.py`（secret 目錄名可由 `secrets_config.json` 覆寫，
#      本專案 2026-08-21 起是 `Secret/` 且已拆成獨立 private submodule）。
#      ⛔ 不寫死 `_secrets` 或 `Secret` —— 寫死跨專案必壞，而寫檔會自動建目錄 ⇒ 靜默長出平行宇宙。
#
# 相關：ucl_core:Docs~/{lang}/Plan/Plan_Plurk_Bot.md（三期分工）
#      ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_PlurkAdminPage.md（帳號層與憑證檔契約）
#      ucl_core:Docs~/{lang}/Workflows/Plurk_Posting_Workflow.md（現行手動發文流程與鐵律）
from __future__ import annotations

import argparse
import base64
import hashlib
import hmac
import json
import secrets
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

sys.path.insert(0, str(Path(__file__).resolve().parent))
from _lib import ucl_paths  # noqa: E402
from _lib import persona_profile  # noqa: E402

API_BASE = "https://www.plurk.com"
# plurk.com 在 Cloudflare 後面，預設 urllib UA 會被 WAF 擋（403 + `error code: 1010`）
USER_AGENT = "UCL-PlurkBot/0.1 (+https://github.com/Persona9999) python-urllib"
SECRET_PREFIX = "plurk_"
REGISTRY_NAME = "plurk_accounts.json"
CRED_FIELDS = ("consumer_key", "consumer_secret", "access_token", "access_token_secret")
PROFILE_FIELD = "plurk_account"


# ===========================================================
# 區塊職責：帳號解析（三段，形狀對齊 C# `UCL_PlurkAccounts.Resolve`）
# 物理意義：persona override → 共用預設 → unset。**刻意沒有 agent 層** ——
#          Plurk 帳號是「某個人的」或「大家共用的」，多一個沒人用的槽就是多一個會漂的地方。
# 數值影響：回值一律帶 `source`。source 不是除錯資訊，是規則的輸入 ——
#          `shared-default` ⇒ 末行署名必填（Tim 2026-08-16 硬規則）。
# ⚠ 兩端對齊義務：本函式與 C# `UCL_PlurkAccounts.Resolve` 讀**同兩個輸入**
#   （profile 欄位 `plurk_account` ／ `AwakenInit/plurk_accounts.json` 的 `SharedSecretId`）。
#   改任一端的欄位名或段數，**必須同步另一端**，否則兩邊各自都能跑、答案卻不同。
# ===========================================================
def registry_path() -> Path:
    return ucl_paths.awaken_init_dir() / REGISTRY_NAME


def shared_secret_id() -> str:
    p = registry_path()
    if not p.exists():
        return ""
    try:
        data = json.loads(p.read_text(encoding="utf-8"))
    except Exception as exc:
        print(f"⚠ 讀不到 {p.name}（{exc}）—— 視為沒有共用預設", file=sys.stderr)
        return ""
    return str(data.get("SharedSecretId") or "").strip()


def resolve(persona: str) -> dict:
    override = ""
    if persona:
        override = str(persona_profile.get_field(persona, PROFILE_FIELD, "") or "").strip()
    if override:
        return {
            "persona": persona,
            "account": override,
            "source": "persona-override",
            "requires_signature": False,
            "why": f"個人帳號（{override}）",
        }
    shared = shared_secret_id()
    if shared:
        return {
            "persona": persona,
            "account": shared,
            "source": "shared-default",
            "requires_signature": True,
            "why": f"共用帳號（{shared}）—— 末行署名必填",
        }
    return {
        "persona": persona,
        "account": "",
        "source": "unset",
        "requires_signature": False,
        "why": "未設定 —— 沒有共用預設、也沒有個人 override",
    }


# ===========================================================
# 區塊職責：讀已安裝的明文憑證
# 物理意義：`.enc` 是密文、`.txt` 是**每台機器自己解密安裝**的產物。
#          工具讀的是後者 ⇒ 只有 `.enc` 存在時，這裡會（也應該）失敗。
# 數值影響：缺檔／缺欄位一律 **fail-loud 並列出缺了哪幾欄**，不回空字串假裝正常
#          —— 空字串會讓簽章算出一個看起來正常的值，然後在 Plurk 那端才失敗。
# ===========================================================
def secret_paths(account: str) -> tuple[Path, Path]:
    d = ucl_paths.secrets_dir()
    return d / f"{account}.txt", d / f"{account}.enc"


def load_credentials(account: str) -> dict:
    plain, enc = secret_paths(account)
    if not plain.exists():
        hint = "（`.enc` 存在但明文沒安裝 ⇒ 到 Secret Manager 做一次解密安裝）" if enc.exists() \
            else "（連 `.enc` 都沒有 ⇒ 先在 UCL_PlurkAdminPage 產出憑證）"
        raise SystemExit(f"✗ 找不到明文憑證：{plain}\n  {hint}")
    try:
        data = json.loads(plain.read_text(encoding="utf-8"))
    except Exception as exc:
        raise SystemExit(f"✗ 憑證明文不是合法 JSON：{plain}（{exc}）")
    missing = [k for k in CRED_FIELDS if not str(data.get(k) or "").strip()]
    if missing:
        raise SystemExit(
            "✗ 憑證缺欄位：" + ", ".join(missing)
            + "\n  OAuth 1.0a 一定是四個值：consumer key/secret 認 app、access token/secret 認帳號。"
            + "\n  只有前兩個是**不能發文也不能查自己**的。")
    return {k: str(data[k]).strip() for k in CRED_FIELDS}


# ===========================================================
# 區塊職責：OAuth 1.0a 簽章（HMAC-SHA1）
# 物理意義：簽章基底 = METHOD & percent(url) & percent(排序後的參數字串)；
#          金鑰 = percent(consumer_secret) & percent(token_secret)。
#          三個地方都要用 **RFC 3986** 的 percent-encoding（`urllib.parse.quote` 的
#          `safe="-._~"`）—— 用預設 safe 會漏掉 `/`，簽章就會對不上而**只回 400/401**，
#          不會告訴你是哪一格錯。
# 數值影響：純計算，不連網。nonce 走 `secrets.token_hex`（不是 random —— 這是簽章材料）。
# 為什麼不吃 pip：整段約 40 行 stdlib（hmac + hashlib + urllib），
#          對齊本 repo 既有 python 工具的純 stdlib 慣例（chess.py / canvas.py / library.py）。
# ===========================================================
def _q(value: str) -> str:
    return urllib.parse.quote(str(value), safe="-._~")


def oauth_header(method: str, url: str, cred: dict, params: dict | None = None) -> str:
    oauth = {
        "oauth_consumer_key": cred["consumer_key"],
        "oauth_token": cred["access_token"],
        "oauth_signature_method": "HMAC-SHA1",
        "oauth_timestamp": str(int(time.time())),
        "oauth_nonce": secrets.token_hex(16),
        "oauth_version": "1.0",
    }
    all_params = dict(params or {})
    all_params.update(oauth)
    norm = "&".join(f"{_q(k)}={_q(v)}" for k, v in sorted(all_params.items()))
    base = f"{method.upper()}&{_q(url)}&{_q(norm)}"
    key = f"{_q(cred['consumer_secret'])}&{_q(cred['access_token_secret'])}"
    sig = base64.b64encode(hmac.new(key.encode(), base.encode(), hashlib.sha1).digest()).decode()
    oauth["oauth_signature"] = sig
    return "OAuth " + ", ".join(f'{_q(k)}="{_q(v)}"' for k, v in sorted(oauth.items()))


# ===========================================================
# 區塊職責：打一個端點並回傳 (http_status, 解析後的 body)
# 物理意義：Plurk API 2.0 全部走 POST，參數 form-encoded 在 body、OAuth 在 Authorization header。
# 數值影響：**唯讀端點才由本檔的 op 呼叫**；HTTPError 也讀 body 回來 ——
#          錯誤訊息在 body 裡（只看 status code 等於把診斷資訊丟掉）。
# ===========================================================
def call(path: str, cred: dict, params: dict | None = None, timeout: int = 20) -> tuple[int, object]:
    url = API_BASE + path
    body = urllib.parse.urlencode(params or {}).encode()
    req = urllib.request.Request(url, data=body, method="POST")
    req.add_header("Authorization", oauth_header("POST", url, cred, params))
    req.add_header("Content-Type", "application/x-www-form-urlencoded")
    # 區塊職責：顯式 User-Agent
    # 物理意義：plurk.com 在 Cloudflare 後面，而 urllib 的預設 UA（`Python-urllib/3.x`）
    #          被 WAF 依瀏覽器簽章封鎖 ⇒ 回 **403 ＋ body `error code: 1010`**（Cloudflare 的碼，
    #          不是 Plurk API 的錯誤格式）。
    # 🩸 為什麼值得寫死在這裡：那個 403 跟「簽章錯」「端點不存在」**長得一樣**，
    #   而它其實連 Plurk 的應用層都沒碰到。Plan §5 記的「官方 API 頁抓取回 403」很可能是同一隻。
    # 數值影響：只影響請求標頭；不帶這行會 100% 失敗且錯誤訊息指向錯的地方。
    req.add_header("User-Agent", USER_AGENT)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            raw = resp.read().decode("utf-8", errors="replace")
            status = resp.status
    except urllib.error.HTTPError as exc:
        raw = exc.read().decode("utf-8", errors="replace")
        status = exc.code
    except urllib.error.URLError as exc:
        raise SystemExit(f"✗ 連不上 {url}：{exc.reason}")
    try:
        return status, json.loads(raw)
    except Exception:
        return status, raw


def cmd_resolve(args) -> int:
    r = resolve(args.persona)
    print(f"# 🐦 帳號解析　persona={r['persona'] or '(未給)'}")
    print(f"  account   : {r['account'] or '(無)'}")
    print(f"  source    : {r['source']}")
    print(f"  署名必填  : {'是' if r['requires_signature'] else '否'}")
    print(f"  說明      : {r['why']}")
    if r["source"] == "unset":
        return 3
    plain, enc = secret_paths(r["account"])
    # `.enc 有` 與 `明文已安裝` 分開報 —— 合成一個綠燈的話，只有密文的機器看起來也像好了
    print(f"  .enc      : {'有' if enc.exists() else '無'}　({enc})")
    print(f"  明文安裝  : {'有' if plain.exists() else '無'}　({plain})")
    if plain.exists():
        cred = load_credentials(r["account"])
        print("  憑證欄位  : 四欄到齊（長度 "
              + " / ".join(f"{k}={len(cred[k])}" for k in CRED_FIELDS) + "）")
        print("　　　　　　  ⛔ 值不印 —— 憑證外洩沒有錯誤訊息")
    return 0


def cmd_whoami(args) -> int:
    """唯讀驗證：簽章對不對、帳號是誰。**不寫入任何 Plurk 資料。**"""
    r = resolve(args.persona)
    if r["source"] == "unset":
        raise SystemExit("✗ 帳號未設定 —— 先在 UCL_PlurkAdminPage 設共用帳號或個人 override")
    cred = load_credentials(r["account"])
    print(f"# 🐦 whoami（唯讀）　account={r['account']}　source={r['source']}")
    print(f"  endpoint: POST {args.endpoint}")
    status, body = call(args.endpoint, cred, timeout=args.timeout)
    print(f"  http    : {status}")
    if isinstance(body, dict):
        # 只印身分欄位；整包 body 可能含 email 等個資，不無條件倒出來
        keys = ("id", "nick_name", "display_name", "full_name", "karma", "timezone")
        shown = {k: body[k] for k in keys if k in body}
        print("  identity: " + (json.dumps(shown, ensure_ascii=False) if shown else "(回應裡沒有身分欄位)"))
        if "error_text" in body:
            print(f"  ✗ error_text: {body['error_text']}")
        if args.dump_keys:
            print("  body keys: " + ", ".join(sorted(body.keys())))
    else:
        print(f"  body    : {str(body)[:400]}")
    if status != 200:
        print("\n⇒ 簽章或端點還沒對。判準：**先確認這個端點本身存在**（換 --endpoint 再試），"
              "\n  再懷疑簽章 —— 兩者的失敗都是 4xx，長得一樣。", file=sys.stderr)
        return 4
    print("\n✓ 簽章與憑證通了（唯讀端點）。⇒ 下一步才輪到 lint / preview，發文仍是 Phase 2。")
    return 0


# ===========================================================
# 區塊職責：**這裡刻意沒有 lint**
# 物理意義：形式檢查（lint）的規則本體在 C# 的 `UCL_PlurkLint`，入口是 `Cmd_Plurk`：
#              run_cmd.py --persona <me> run Plurk --arg op=lint --arg slip_file=<檔>
#          理由是「規則要長在必經路上」：`post` 在 C#（唯一寫入端）且**強制先跑 lint**。
#          規則若在這支 python 裡再放一份，發文那條路就繞得過它 —— 而繞過去不會報錯。
# 🩸 我 2026-08-21 先在這裡寫過一份 lint，一小時後把它撤掉：兩份規則引擎遲早各說各話，
#   而「python 說過了、C# 說擋下」這種分歧**兩邊都不會覺得自己錯**。
# ⇒ 本檔只留**唯讀診斷**：`resolve`（不連網）與 `whoami`（唯讀端點）。
#   保留它們的理由不是方便，是**獨立第二條路** —— Editor 那條壞掉時，
#   還有一條不經過 Cmd 的路能回答「憑證與簽章到底通不通」。
# ===========================================================


def main() -> int:
    ap = argparse.ArgumentParser(description="Plurk 帳號解析與唯讀簽章驗證（不含發文）")
    sub = ap.add_subparsers(dest="op", required=True)

    p1 = sub.add_parser("resolve", help="解析某 persona 用哪個帳號（不連網）")
    p1.add_argument("--persona", default="", help="空＝只看共用預設")
    p1.set_defaults(func=cmd_resolve)

    p2 = sub.add_parser("whoami", help="唯讀端點驗證 OAuth 簽章（不發文）")
    p2.add_argument("--persona", default="")
    # 預設端點：Plan §5 標明「未對照官方文件」⇒ 端點做成參數，讓實測結果本身當證據
    p2.add_argument("--endpoint", default="/APP/Users/me")
    p2.add_argument("--timeout", type=int, default=20)
    p2.add_argument("--dump-keys", action="store_true", help="印出回應的 key 名（不印值）")
    p2.set_defaults(func=cmd_whoami)


    args = ap.parse_args()
    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())
