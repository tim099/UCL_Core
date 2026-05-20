#!/usr/bin/env python3
"""ucl_secret.py — UCL_Core 通用 secret 加解密 CLI (passphrase-based, TKN2).

# 區塊職責：對 _secrets/ 下的 secret 提供 7 條 op — encrypt/decrypt/status/show-hint/list/rotate/reveal
# 物理意義：EOV 端 secret_install.py (encrypt/decrypt/status) 的跨專案通用化繼承版，
#          多 show-hint / list / rotate / reveal 四 op，補 passphrase 忘記時的雙重失憶救援。
# 數值影響：純 IO + crypto + 檔案總管 spawn，無 network / persistent state。exit code 是主要回值。

CLI 用法:

    # 加密 (互動 prompt 兩次 confirm; 可帶 hint/label):
    python ucl_secret.py encrypt _secrets/discord_bot_token.txt --hint "生日後三碼+貓名" --label "EOV Discord Bot"

    # 解密 (互動 / stdin / 先印 hint):
    python ucl_secret.py decrypt _secrets/discord_bot_token.enc [--show-hint]
    echo -n "<pw>" | python ucl_secret.py decrypt _secrets/discord_bot_token.enc --stdin-passphrase

    # status check (給 Unity / hook):  exit 0=ok 1=need-install 2=no-enc 3=stale
    python ucl_secret.py status _secrets/discord_bot_token

    # ⭐ show-hint — passphrase-free 印 metadata (失憶救援路徑 A):
    python ucl_secret.py show-hint _secrets/discord_bot_token.enc [--json]

    # list — 掃 dir 下所有 .enc 一覽:
    python ucl_secret.py list [--root _secrets] [--json]

    # rotate — 換 passphrase 或改 hint (舊 pw → 新 pw); Q5: 改 hint 全覆蓋不留軌跡:
    python ucl_secret.py rotate _secrets/discord_bot_token.enc [--hint "新提示"] [--label "新標籤"]

    # ⭐ reveal — 印明文路徑 + 開檔案總管 (失憶救援路徑 B: 手動貼上明文):
    python ucl_secret.py reveal _secrets/discord_bot_token [--no-open]

Exit codes (對齊 EOV secret_install.py):
    0 = OK
    1 = need-install (status: enc 有, plain 缺)
    2 = no-enc (status: 兩者都缺) / reveal: 無法定位
    3 = stale (status: plain 有, enc 缺)
    4 = file not found (encrypt/decrypt/rotate 目標檔不存在)
    5 = decrypt failed (passphrase 錯 / 密文損壞)
"""
from __future__ import annotations

import argparse
import getpass
import json
import os
import subprocess
import sys
from pathlib import Path

# 區塊職責：import 新 TKN2 crypto lib
# 物理意義：lib 在同層 _lib/ucl_secrets_crypto.py，把 _lib 加進 sys.path 後直接 import。
#          ucl_secret.py 不依賴任何 UCL_Core C# / 專案特定路徑 — 可獨立跑 + test。
HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE / "_lib"))

import ucl_secrets_crypto as crypto  # noqa: E402

# 區塊職責：強制 stdout/stderr UTF-8，避免 Windows cp950 console 印 emoji/中文 crash
# 物理意義：Editor 端走 PYTHONIOENCODING=utf-8 spawn，但 CLI 直接在 cp950 終端跑時
#          print("⚠"/"明文") 會 UnicodeEncodeError。reconfigure errors='replace' 保證不 crash。
# 數值影響：純輸出編碼，不影響加解密 bytes。
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass  # 老 Python / 已被替換的 stream，容忍


# ---------------------------------------------------------------------------
# Helpers — 路徑規範化 + passphrase 讀取
# ---------------------------------------------------------------------------

def _resolve_enc_path(plain_or_enc: Path) -> Path:
    """規範化: 'foo.txt' → 'foo.enc'; 'foo' → 'foo.enc'; 'foo.enc' 原樣。"""
    s = str(plain_or_enc)
    if s.endswith(".enc"):
        return plain_or_enc
    if s.endswith(".txt"):
        return Path(s[:-4] + ".enc")
    return Path(s + ".enc")


def _resolve_plain_path(plain_or_enc: Path) -> Path:
    """規範化: 'foo.enc' → 'foo.txt'; 'foo' → 'foo.txt'; 'foo.txt' 原樣。"""
    s = str(plain_or_enc)
    if s.endswith(".enc"):
        return Path(s[:-4] + ".txt")
    if s.endswith(".txt"):
        return plain_or_enc
    return Path(s + ".txt")


def _read_passphrase(stdin_mode: bool, confirm: bool, prompt: str = "Passphrase: ") -> str:
    """讀 passphrase: stdin_mode 從 stdin 第一行 (給 Editor / pipe); 否則互動 getpass。"""
    if stdin_mode:
        line = sys.stdin.readline()
        return line.rstrip("\r\n")
    pw1 = getpass.getpass(prompt)
    if not pw1:
        sys.exit("✗ passphrase cannot be empty")
    if confirm:
        pw2 = getpass.getpass("Confirm:    ")
        if pw1 != pw2:
            sys.exit("✗ passphrases do not match")
    return pw1


def _age_str(created_at) -> str:
    """created_at datetime → 「N days ago」可讀字串；None → 「unknown」。"""
    if created_at is None:
        return "unknown"
    import datetime
    now = datetime.datetime.now(datetime.timezone.utc)
    delta = now - created_at
    days = delta.days
    if days <= 0:
        return "today"
    if days == 1:
        return "1 day ago"
    return f"{days} days ago"


# ---------------------------------------------------------------------------
# Op: encrypt
# ---------------------------------------------------------------------------

def cmd_encrypt(args) -> int:
    plain_path = Path(args.path)
    enc_path = _resolve_enc_path(plain_path) if not args.out else Path(args.out)

    if not plain_path.exists():
        print(f"[err] plaintext not found: {plain_path}", file=sys.stderr)
        return 4
    plaintext = plain_path.read_bytes()
    if not plaintext.strip():
        print("[err] plaintext is empty / whitespace-only", file=sys.stderr)
        return 4

    hint = args.hint or ""
    if len(hint) > crypto.HINT_MAX_LEN:
        print(f"[err] hint 超過 {crypto.HINT_MAX_LEN} char 上限 (目前 {len(hint)})", file=sys.stderr)
        return 4
    if hint:
        # 區塊職責: hint 明文入 git 警告 (Plan §Hint 安全性)
        print(f"⚠ hint 會明文寫進 .enc 並跟著 git commit 公開 — 別寫密碼本身。")
        print(f"   你設的 hint: {hint!r}")

    passphrase = _read_passphrase(args.stdin_passphrase, confirm=not args.stdin_passphrase)
    ciphertext = crypto.encrypt(plaintext, passphrase, hint=hint, label=args.label or "")
    enc_path.write_bytes(ciphertext)
    print(f"[OK] wrote {enc_path} ({len(ciphertext)} bytes, format=TKN2)")
    print(f"     plaintext source: {plain_path} ({len(plaintext)} bytes)")
    print(f"     next: `git add {enc_path}` to commit the encrypted version.")
    print(f"     keep {plain_path} gitignored.")
    return 0


# ---------------------------------------------------------------------------
# Op: decrypt
# ---------------------------------------------------------------------------

def cmd_decrypt(args) -> int:
    enc_path = Path(args.path)
    plain_path = _resolve_plain_path(enc_path) if not args.out else Path(args.out)

    if not enc_path.exists():
        print(f"[err] ciphertext not found: {enc_path}", file=sys.stderr)
        return 4
    ciphertext = enc_path.read_bytes()

    # --show-hint: 解密前先印 hint 給人腦輔助 (passphrase-free)
    if args.show_hint:
        try:
            meta = crypto.read_metadata(ciphertext)
            print(f"💡 hint: {meta.hint or '(無提示)'}", file=sys.stderr)
            if meta.label:
                print(f"   label: {meta.label}", file=sys.stderr)
        except ValueError:
            pass

    passphrase = _read_passphrase(args.stdin_passphrase, confirm=False)
    try:
        result = crypto.decrypt(ciphertext, passphrase)
    except ValueError as e:
        print(f"[err] decrypt failed: {e}", file=sys.stderr)
        return 5

    plain_path.write_bytes(result.plaintext)
    print(f"[OK] wrote {plain_path} ({len(result.plaintext)} bytes)")
    return 0


# ---------------------------------------------------------------------------
# Op: status — exit code 是主要回值
# ---------------------------------------------------------------------------

def cmd_status(args) -> int:
    base = Path(args.path)
    plain_path = _resolve_plain_path(base)
    enc_path = _resolve_enc_path(base)
    plain_exists = plain_path.exists()
    enc_exists = enc_path.exists()
    if plain_exists and enc_exists:
        print(f"[OK] plain={plain_path} enc={enc_path}", file=sys.stderr)
        return 0
    if plain_exists and not enc_exists:
        print("[stale] plaintext exists but no .enc — consider encrypting + committing", file=sys.stderr)
        return 3
    if enc_exists and not plain_exists:
        print("[need-install] enc exists, plaintext missing — run decrypt", file=sys.stderr)
        return 1
    print("[no-enc] neither plaintext nor .enc exists — run encrypt first", file=sys.stderr)
    return 2


# ---------------------------------------------------------------------------
# Op: show-hint — ⭐ passphrase-free 印 metadata (失憶救援路徑 A)
# ---------------------------------------------------------------------------

def cmd_show_hint(args) -> int:
    enc_path = _resolve_enc_path(Path(args.path))
    if not enc_path.exists():
        print(f"[err] .enc not found: {enc_path}", file=sys.stderr)
        return 4
    try:
        meta = crypto.read_metadata(enc_path.read_bytes())
    except ValueError as e:
        print(f"[err] cannot read metadata: {e}", file=sys.stderr)
        return 4

    if args.json:
        # Page / 自動化用 — 結構化輸出
        print(json.dumps({
            "enc_path": str(enc_path),
            "hint": meta.hint,
            "label": meta.label,
            "created_at": meta.created_at.isoformat() if meta.created_at else None,
            "format_version": meta.format_version,
            "salt_b64": meta.salt_b64,
            "plain_exists": _resolve_plain_path(enc_path).exists(),
        }, ensure_ascii=False))
        return 0

    created_disp = meta.created_at.strftime("%Y-%m-%d %H:%M:%S UTC") if meta.created_at else "unknown"
    print("# Secret Metadata (passphrase-free)")
    print(f"- Label       : {meta.label or '(無)'}")
    print(f"- Hint        : {meta.hint or '(無提示)'}")
    print(f"- Created at  : {created_disp} ({_age_str(meta.created_at)})")
    print(f"- Format ver  : {meta.format_version} (TKN{meta.format_version})")
    print(f"- Salt b64    : {meta.salt_b64} ({crypto.SALT_LEN} bytes)")
    return 0


# ---------------------------------------------------------------------------
# Op: list — 掃 dir 下所有 .enc 一覽
# ---------------------------------------------------------------------------

def cmd_list(args) -> int:
    root = Path(args.root)
    if not root.exists():
        print(f"[err] root not found: {root}", file=sys.stderr)
        return 4

    rows = []
    for enc in sorted(root.glob("*.enc")):
        try:
            meta = crypto.read_metadata(enc.read_bytes())
            rows.append({
                "enc_path": str(enc),
                "label": meta.label,
                "hint": meta.hint,
                "created_at": meta.created_at.isoformat() if meta.created_at else None,
                "format_version": meta.format_version,
                "plain_exists": _resolve_plain_path(enc).exists(),
            })
        except ValueError as e:
            rows.append({"enc_path": str(enc), "error": str(e)})

    if args.json:
        print(json.dumps(rows, ensure_ascii=False))
        return 0

    if not rows:
        print(f"(no .enc files under {root})")
        return 0
    print(f"# Secrets under {root} ({len(rows)} found)")
    for r in rows:
        if "error" in r:
            print(f"  ✗ {Path(r['enc_path']).name}: {r['error']}")
            continue
        installed = "✅ installed" if r["plain_exists"] else "🔒 need-install"
        print(f"  {installed}  {Path(r['enc_path']).name}  [TKN{r['format_version']}]")
        print(f"      label: {r['label'] or '(無)'}   hint: {r['hint'] or '(無提示)'}")
    return 0


# ---------------------------------------------------------------------------
# Op: rotate — 換 passphrase / 改 hint (Q5: 改 hint 全覆蓋不留軌跡)
# ---------------------------------------------------------------------------

def cmd_rotate(args) -> int:
    enc_path = _resolve_enc_path(Path(args.path))
    if not enc_path.exists():
        print(f"[err] .enc not found: {enc_path}", file=sys.stderr)
        return 4
    ciphertext = enc_path.read_bytes()

    # 先讀現有 metadata 當預設 (沒帶 --hint/--label 就沿用舊的)
    try:
        old_meta = crypto.read_metadata(ciphertext)
    except ValueError as e:
        print(f"[err] cannot read existing metadata: {e}", file=sys.stderr)
        return 4

    print(f"Rotating {enc_path.name} (current: TKN{old_meta.format_version}, label={old_meta.label or '(無)'})")
    old_pw = _read_passphrase(args.stdin_passphrase, confirm=False, prompt="Old passphrase: ")
    try:
        result = crypto.decrypt(ciphertext, old_pw)
    except ValueError as e:
        print(f"[err] decrypt with old passphrase failed: {e}", file=sys.stderr)
        return 5

    # Q5 全覆蓋: 帶了就用新值, 沒帶沿用舊值 (rotate 不存舊 hint 軌跡)
    new_hint = args.hint if args.hint is not None else old_meta.hint
    new_label = args.label if args.label is not None else old_meta.label
    if len(new_hint) > crypto.HINT_MAX_LEN:
        print(f"[err] hint 超過 {crypto.HINT_MAX_LEN} char 上限", file=sys.stderr)
        return 4

    # 新 passphrase (不強制 != 舊, per Q3); stdin 模式讀第二行
    if args.stdin_passphrase:
        new_pw = sys.stdin.readline().rstrip("\r\n") or old_pw
    else:
        new_pw = getpass.getpass("New passphrase (空=沿用舊密碼): ")
        if not new_pw:
            new_pw = old_pw
        else:
            confirm = getpass.getpass("Confirm new:    ")
            if new_pw != confirm:
                print("[err] new passphrases do not match", file=sys.stderr)
                return 5

    new_cipher = crypto.encrypt(result.plaintext, new_pw, hint=new_hint, label=new_label)
    enc_path.write_bytes(new_cipher)
    print(f"[OK] rotated {enc_path} (now TKN2, label={new_label or '(無)'}, hint={new_hint or '(無提示)'})")
    print(f"     next: `git add {enc_path}` to commit the rotated version.")
    return 0


# ---------------------------------------------------------------------------
# Op: reveal — ⭐ 印明文路徑 + 開檔案總管 (失憶救援路徑 B)
# ---------------------------------------------------------------------------

def cmd_reveal(args) -> int:
    base = Path(args.path)
    plain_path = _resolve_plain_path(base).resolve()
    folder = plain_path.parent

    print(f"明文應落地路徑: {plain_path}")
    print(f"所在資料夾    : {folder}")
    print(f"明文目前存在  : {'是' if plain_path.exists() else '否 (忘記密碼時可手動把原始 token 存成此檔)'}")

    if args.no_open:
        return 0

    if not folder.exists():
        print(f"[warn] 資料夾不存在: {folder}", file=sys.stderr)
        return 2

    # 區塊職責: 跨平台開檔案總管定位資料夾 (Windows/mac/Linux)
    # 物理意義: 路徑 B 救援 — 一鍵開資料夾讓使用者手動貼明文，降低貼錯位置風險
    try:
        if sys.platform.startswith("win"):
            # Windows: 若明文已存在就 select 它, 否則開資料夾
            if plain_path.exists():
                subprocess.run(["explorer", "/select,", str(plain_path)])
            else:
                os.startfile(str(folder))  # noqa: S606
        elif sys.platform == "darwin":
            if plain_path.exists():
                subprocess.run(["open", "-R", str(plain_path)])
            else:
                subprocess.run(["open", str(folder)])
        else:
            subprocess.run(["xdg-open", str(folder)])
    except Exception as e:
        print(f"[warn] 開檔案總管失敗 (路徑仍有效, 可手動開): {e}", file=sys.stderr)
        return 0
    print("✓ 已開檔案總管")
    return 0


# ---------------------------------------------------------------------------
# Entry
# ---------------------------------------------------------------------------

def main():
    ap = argparse.ArgumentParser(description="UCL secret encrypt/decrypt/status/show-hint/list/rotate/reveal CLI (TKN2).")
    sub = ap.add_subparsers(dest="op", required=True)

    sp_enc = sub.add_parser("encrypt", help="Encrypt plaintext → .enc (TKN2, with hint/label).")
    sp_enc.add_argument("path", help="Plaintext file path (e.g. _secrets/discord_bot_token.txt).")
    sp_enc.add_argument("--hint", help="密碼提示 (明文入 git; ≤256 char; 別寫密碼本身).")
    sp_enc.add_argument("--label", help="人類可讀標籤 (e.g. 'EOV Discord Bot Token').")
    sp_enc.add_argument("--out", help="Override .enc output path.")
    sp_enc.add_argument("--stdin-passphrase", action="store_true", help="Read passphrase from stdin first line.")
    sp_enc.set_defaults(func=cmd_encrypt)

    sp_dec = sub.add_parser("decrypt", help="Decrypt .enc → plaintext.")
    sp_dec.add_argument("path", help=".enc file path.")
    sp_dec.add_argument("--out", help="Override plaintext output path.")
    sp_dec.add_argument("--stdin-passphrase", action="store_true", help="Read passphrase from stdin first line.")
    sp_dec.add_argument("--show-hint", action="store_true", help="解密前先印 hint (passphrase-free).")
    sp_dec.set_defaults(func=cmd_decrypt)

    sp_st = sub.add_parser("status", help="Check install state. Exit: 0=ok 1=need-install 2=no-enc 3=stale.")
    sp_st.add_argument("path", help="Plaintext or .enc path (other auto-derived).")
    sp_st.set_defaults(func=cmd_status)

    sp_sh = sub.add_parser("show-hint", help="⭐ Print metadata (hint/label/created/ver) — passphrase-free.")
    sp_sh.add_argument("path", help=".enc file path.")
    sp_sh.add_argument("--json", action="store_true", help="JSON output (for Editor Page / automation).")
    sp_sh.set_defaults(func=cmd_show_hint)

    sp_ls = sub.add_parser("list", help="List all .enc under a dir with metadata.")
    sp_ls.add_argument("--root", default="AgentCommands/_secrets", help="Dir to scan (default: AgentCommands/_secrets).")
    sp_ls.add_argument("--json", action="store_true", help="JSON output.")
    sp_ls.set_defaults(func=cmd_list)

    sp_rot = sub.add_parser("rotate", help="Re-encrypt (change passphrase or hint). Q5: hint 全覆蓋不留軌跡.")
    sp_rot.add_argument("path", help=".enc file path.")
    sp_rot.add_argument("--hint", help="新 hint (不帶=沿用舊; ≤256 char).")
    sp_rot.add_argument("--label", help="新 label (不帶=沿用舊).")
    sp_rot.add_argument("--stdin-passphrase", action="store_true",
                        help="Read old pw (line1) + new pw (line2) from stdin.")
    sp_rot.set_defaults(func=cmd_rotate)

    sp_rev = sub.add_parser("reveal", help="⭐ Print plaintext path + open file manager (路徑B手動貼上救援).")
    sp_rev.add_argument("path", help="Plaintext or .enc path (plaintext auto-derived).")
    sp_rev.add_argument("--no-open", action="store_true", help="只印路徑不開檔案總管 (CI/headless).")
    sp_rev.set_defaults(func=cmd_reveal)

    args = ap.parse_args()
    sys.exit(args.func(args))


if __name__ == "__main__":
    main()
