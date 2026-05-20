"""ucl_secrets_crypto.py — UCL_Core 通用 secret 對稱加密 lib (passphrase-based, TKN2).

# 區塊職責：把任意 secret bytes 用 user 自訂 passphrase 加密成可入 git 的密文，
#          並在 header 帶 hint / label / created_at metadata，passphrase 忘記時靠 hint 救援。
# 物理意義：EOV 端 secrets_crypto.py (TKN1) 的跨專案通用化繼承版。TKN1 只有 salt + token，
#          TKN2 多三段明文 metadata (H:/C:/L:) 讓 .enc 不再是「忘密碼就變磚」的黑盒。
# 數值影響：KDF 200k iter / Fernet (AES-128-CBC + HMAC-SHA256) — 與 TKN1 完全相同，
#          metadata 不參與 KDF，passphrase 對錯都能讀 (失憶救援設計)。

格式 (TKN2 encrypted bytes layout):

    "TKN2\n"                <- magic + newline (區分 TKN1)
    "<salt urlsafe-b64>\n"  <- 16-byte salt (同 TKN1)
    "H:<hint>\n"            <- hint 明文單行 (utf-8); 空 hint = "H:\n"
    "C:<created_at iso>\n"  <- created_at ISO8601 UTC, e.g. "C:2026-05-20T09:10:00Z"
    "L:<label>\n"          <- label 明文單行 (utf-8); 空 label = "L:\n"
    "<fernet token>"        <- 同 TKN1 (timestamp + IV + ct + HMAC, base64url)

向後相容 — 同時認 TKN1：

    "TKN1\n<salt b64>\n<fernet token>"   <- decode 時 hint/label/created_at 全 fallback 空

設計約束 (Plan_UCL_Secret_Manager.md, Tim 拍板)：
    - Hint 明文 (Q2)：加密 hint 與失憶救援目的悖論，故明文存 header 跟著 git 公開。
    - Hint 256 char 上限 (Q6)：encrypt/rotate 超過即 raise (UI 顯示友善 + 防 mis-paste markdown)。
    - Rotate 改 hint 完全覆蓋不留軌跡 (Q5)：本 lib 不存舊 hint，rotate 由上層 (CLI) 組合 decrypt+encrypt。

Public API:
    encrypt(plaintext: bytes, passphrase: str, *, hint="", label="", created_at=None) -> bytes
    decrypt(ciphertext: bytes, passphrase: str) -> DecryptedSecret
    read_metadata(ciphertext: bytes) -> SecretMetadata   # passphrase-free
"""
from __future__ import annotations

import base64
import datetime
import os
from dataclasses import dataclass

from cryptography.fernet import Fernet, InvalidToken
from cryptography.hazmat.primitives import hashes
from cryptography.hazmat.primitives.kdf.pbkdf2 import PBKDF2HMAC

# 區塊職責：格式 magic + KDF 參數 + metadata 約束
# 物理意義：MAGIC_V2 = TKN2 (本 lib 一律輸出)，MAGIC_V1 = TKN1 (只讀不寫，相容舊檔)；
#          KDF 200k iter 是 OWASP 2023 推薦下限 + margin；SALT_LEN 16 byte = 128 bit 隨機。
#          HINT_MAX_LEN 256 char 是 Tim Q6 拍板上限。
# 數值影響：MAGIC 變動會破壞既有 .enc 解碼，KDF 參數變動會讓舊密文解不開 (salt 同但 iter 不同 → key 不同)。
MAGIC_V1 = b"TKN1"
MAGIC_V2 = b"TKN2"
KDF_ITERATIONS = 200_000
SALT_LEN = 16
HINT_MAX_LEN = 256

# 區塊職責：metadata 段前綴常數
# 物理意義：H:/C:/L: 前綴讓 parser forward-compat — 未來加新 metadata (R:/A:) 只要新前綴不撞舊的；
#          parser 讀 metadata 段時跳過不認識的前綴 (warn but continue)。
PREFIX_HINT = "H:"
PREFIX_CREATED = "C:"
PREFIX_LABEL = "L:"

# 區塊職責：TKN2 metadata 段固定行數
# 物理意義：magic(1) + salt(1) + H/C/L(3) = 前 5 行為非 token，第 6 段起為 fernet token。
#          用「split 前 5 個 \n」切出 metadata，剩餘整塊當 token (token 內含 base64url 不含裸 \n)。
_TKN2_META_LINES = 5


@dataclass
class SecretMetadata:
    """passphrase-free 讀出的 header metadata。"""
    hint: str
    label: str
    created_at: datetime.datetime | None
    format_version: int            # 1 or 2
    salt_b64: str                  # debug 用，不直接洩 plaintext info


@dataclass
class DecryptedSecret:
    """decrypt() 回傳 — 明文 + 隨附 metadata。"""
    plaintext: bytes
    hint: str                      # 從 header 讀，passphrase 對錯都可拿到
    label: str
    created_at: datetime.datetime | None
    format_version: int            # 1 or 2


def _derive_key(passphrase: str, salt: bytes) -> bytes:
    """PBKDF2-HMAC-SHA256 推 32-byte key 給 Fernet base64url 用 (同 TKN1)。

    Fernet 規格: key 必須是 urlsafe_b64encode(32 bytes raw) = 44 chars。
    """
    kdf = PBKDF2HMAC(
        algorithm=hashes.SHA256(),
        length=32,
        salt=salt,
        iterations=KDF_ITERATIONS,
    )
    raw_key = kdf.derive(passphrase.encode("utf-8"))
    return base64.urlsafe_b64encode(raw_key)


def _validate_single_line(value: str, field: str) -> str:
    """metadata 欄位不可含 newline (會破壞行分隔格式)；hint 額外查 256 char 上限。"""
    if "\n" in value or "\r" in value:
        raise ValueError(f"{field} 不可含換行字元 (會破壞 TKN2 行格式)")
    return value


def _fmt_created_at(created_at: datetime.datetime | None) -> str:
    """datetime → ISO8601 UTC 字串 (Z 結尾)。None → 當下 UTC。"""
    if created_at is None:
        created_at = datetime.datetime.now(datetime.timezone.utc)
    # 統一轉 UTC + 去 microsecond 雜訊，輸出 Z 結尾
    if created_at.tzinfo is not None:
        created_at = created_at.astimezone(datetime.timezone.utc)
    return created_at.replace(microsecond=0).strftime("%Y-%m-%dT%H:%M:%SZ")


def _parse_created_at(s: str) -> datetime.datetime | None:
    """ISO8601 (Z 或 offset) → aware datetime；無法解析回 None (容錯不 raise)。"""
    if not s:
        return None
    try:
        # Python fromisoformat 不吃裸 "Z"，先轉 +00:00
        normalized = s.replace("Z", "+00:00")
        dt = datetime.datetime.fromisoformat(normalized)
        if dt.tzinfo is None:
            dt = dt.replace(tzinfo=datetime.timezone.utc)
        return dt
    except (ValueError, TypeError):
        return None


def encrypt(
    plaintext: bytes,
    passphrase: str,
    *,
    hint: str = "",
    label: str = "",
    created_at: datetime.datetime | None = None,
) -> bytes:
    """加密任意 bytes → TKN2 格式 (bytes, utf-8)。

    salt 每次加密都重 gen，同 passphrase 兩次加密產出不同密文 (semantically secure)。
    hint/label/created_at 走明文 metadata 段，passphrase 不參與。
    """
    if not isinstance(plaintext, (bytes, bytearray)):
        raise TypeError("plaintext must be bytes")
    if not passphrase:
        raise ValueError("passphrase cannot be empty")

    hint = _validate_single_line(hint or "", "hint")
    if len(hint) > HINT_MAX_LEN:
        raise ValueError(f"hint 超過 {HINT_MAX_LEN} char 上限 (目前 {len(hint)}); 提示應簡短，別貼整段文字")
    label = _validate_single_line(label or "", "label")
    created_str = _fmt_created_at(created_at)

    salt = os.urandom(SALT_LEN)
    key = _derive_key(passphrase, salt)
    token = Fernet(key).encrypt(bytes(plaintext))

    salt_b64 = base64.urlsafe_b64encode(salt)

    # 區塊職責：組 TKN2 六段
    # 物理意義：magic / salt / H: / C: / L: / token，每段以 \n 分隔 (token 為最後一段不加尾 \n)
    parts = [
        MAGIC_V2,
        salt_b64,
        (PREFIX_HINT + hint).encode("utf-8"),
        (PREFIX_CREATED + created_str).encode("utf-8"),
        (PREFIX_LABEL + label).encode("utf-8"),
        token,
    ]
    return b"\n".join(parts)


def _decode_header(ciphertext: bytes):
    """解析 header → (format_version, salt, hint, label, created_at, token_bytes)。

    不做 KDF / 不需 passphrase — 純結構解析。malformed → ValueError。
    """
    if not isinstance(ciphertext, (bytes, bytearray)):
        raise TypeError("ciphertext must be bytes")
    # 區塊職責：CRLF 正規化 — .enc 經 git autocrlf / Windows 編輯器存檔會變 \r\n
    # 物理意義：salt b64 與 fernet token 都是 base64url (絕不含 \r/\n)，hint/label 已在 encrypt
    #          驗證禁含換行，故把 \r\n → \n 只會影響結構分隔符，不會破壞任何欄位內容。
    #          不正規化的話舊 TKN1 .enc (CRLF) 會 "bad magic: TKN1\r" 解不開 (content-layer 盲點)。
    # 數值影響：純結構正規化，加解密 bytes 不變。
    # rstrip 去掉檔尾殘留換行 (token 為 base64url，尾端無意義空白)，避免 fernet decode 雜訊
    raw = bytes(ciphertext).replace(b"\r\n", b"\n").rstrip(b"\r\n")

    # 先看 magic 決定走哪個分支
    first_nl = raw.find(b"\n")
    if first_nl < 0:
        raise ValueError("malformed ciphertext: 找不到 magic 換行")
    magic = raw[:first_nl]

    if magic == MAGIC_V1:
        # TKN1: magic / salt / token (3 段)，metadata 全空
        parts = raw.split(b"\n", 2)
        if len(parts) != 3:
            raise ValueError("malformed TKN1: expected 3 newline-separated parts")
        _, salt_b64, token = parts
        salt = _decode_salt(salt_b64)
        return (1, salt, "", "", None, token)

    if magic == MAGIC_V2:
        # TKN2: magic / salt / H: / C: / L: / token (前 5 個 \n 切 metadata，剩餘是 token)
        parts = raw.split(b"\n", _TKN2_META_LINES)
        if len(parts) != _TKN2_META_LINES + 1:
            raise ValueError("malformed TKN2: header 段不足 (需 magic/salt/H/C/L/token)")
        _, salt_b64, h_line, c_line, l_line, token = parts
        salt = _decode_salt(salt_b64)
        hint = _strip_prefix(h_line.decode("utf-8", "replace"), PREFIX_HINT)
        created_at = _parse_created_at(_strip_prefix(c_line.decode("utf-8", "replace"), PREFIX_CREATED))
        label = _strip_prefix(l_line.decode("utf-8", "replace"), PREFIX_LABEL)
        return (2, salt, hint, label, created_at, token)

    raise ValueError(f"bad magic: expected {MAGIC_V1!r} or {MAGIC_V2!r}, got {magic!r}")


def _strip_prefix(line: str, prefix: str) -> str:
    """去掉 metadata 前綴 (H:/C:/L:)；前綴不符就原樣回 (forward-compat 容錯)。"""
    if line.startswith(prefix):
        return line[len(prefix):]
    return line


def _decode_salt(salt_b64: bytes) -> bytes:
    try:
        salt = base64.urlsafe_b64decode(salt_b64)
    except Exception as e:
        raise ValueError(f"bad salt encoding: {e}") from e
    if len(salt) != SALT_LEN:
        raise ValueError(f"bad salt length: expected {SALT_LEN}, got {len(salt)}")
    return salt


def read_metadata(ciphertext: bytes) -> SecretMetadata:
    """passphrase-free 讀 header metadata — 失憶救援核心路徑 (路徑 A)。

    給錯 / 不給 passphrase 都能讀 hint / label / created_at / format_version。
    """
    version, salt, hint, label, created_at, _token = _decode_header(ciphertext)
    return SecretMetadata(
        hint=hint,
        label=label,
        created_at=created_at,
        format_version=version,
        salt_b64=base64.urlsafe_b64encode(salt).decode("ascii"),
    )


def decrypt(ciphertext: bytes, passphrase: str) -> DecryptedSecret:
    """解密 + 回 metadata。passphrase 錯 / 密文損壞 → ValueError；metadata 任何 case 都填。"""
    if not passphrase:
        raise ValueError("passphrase cannot be empty")

    version, salt, hint, label, created_at, token = _decode_header(ciphertext)
    key = _derive_key(passphrase, salt)
    try:
        plaintext = Fernet(key).decrypt(token)
    except InvalidToken as e:
        raise ValueError("decrypt failed: wrong passphrase or corrupted ciphertext") from e

    return DecryptedSecret(
        plaintext=plaintext,
        hint=hint,
        label=label,
        created_at=created_at,
        format_version=version,
    )


# ===========================================================================
# Self-test — 跑 `python ucl_secrets_crypto.py` 自我驗證 round-trip + 向後相容
# ===========================================================================
def _run_selftest():
    pw = "test-passphrase-12345"

    # (1) round-trip — 空 hint / 有 hint / 長 hint(256) / binary / 空 plaintext
    long_hint = "x" * HINT_MAX_LEN
    cases = [
        (b"", "", ""),
        (b"hello", "生日後三碼 + 貓的名字", "EOV Discord Bot Token"),
        (b"MTUwMjQ3MzIyMDEyNzcyMzU2MA.GuD3cK.fake", long_hint, "Long Hint Case"),
        (os.urandom(512), "", "Binary Secret"),
    ]
    for plain, hint, label in cases:
        enc = encrypt(plain, pw, hint=hint, label=label)
        # magic 必為 TKN2
        assert enc.startswith(MAGIC_V2 + b"\n"), "encoder 必須輸出 TKN2"
        dec = decrypt(enc, pw)
        assert dec.plaintext == plain, f"round-trip plaintext mismatch (len={len(plain)})"
        assert dec.hint == hint, f"hint mismatch: {dec.hint!r} != {hint!r}"
        assert dec.label == label, f"label mismatch: {dec.label!r} != {label!r}"
        assert dec.format_version == 2
        # read_metadata passphrase-free 必跟 decrypt 的 metadata 一致
        meta = read_metadata(enc)
        assert meta.hint == hint and meta.label == label and meta.format_version == 2
        assert meta.created_at is not None
        # 給錯 passphrase 仍能讀 metadata，但 decrypt 失敗
        meta_wrong = read_metadata(enc)
        assert meta_wrong.hint == hint, "read_metadata 不該依賴 passphrase"
        try:
            decrypt(enc, pw + "x")
        except ValueError:
            pass
        else:
            raise AssertionError("wrong passphrase did not raise")

    # (2) semantically secure — 同 input 兩次加密產出不同密文，但解出相同
    e1 = encrypt(b"same", pw, hint="h")
    e2 = encrypt(b"same", pw, hint="h")
    assert e1 != e2, "兩次加密應產出不同密文 (salt 隨機)"
    assert decrypt(e1, pw).plaintext == decrypt(e2, pw).plaintext == b"same"

    # (3) hint 256 上限 — 超過即 raise
    try:
        encrypt(b"x", pw, hint="y" * (HINT_MAX_LEN + 1))
    except ValueError:
        pass
    else:
        raise AssertionError("超長 hint 沒有 raise")

    # (4) hint 含換行 — raise
    try:
        encrypt(b"x", pw, hint="line1\nline2")
    except ValueError:
        pass
    else:
        raise AssertionError("含換行 hint 沒有 raise")

    # (5) 向後相容 TKN1 — 手工組一個 TKN1 密文，decode/decrypt 應 work + metadata 空
    salt = os.urandom(SALT_LEN)
    key = _derive_key(pw, salt)
    tkn1_token = Fernet(key).encrypt(b"legacy-secret")
    tkn1 = MAGIC_V1 + b"\n" + base64.urlsafe_b64encode(salt) + b"\n" + tkn1_token
    dec_v1 = decrypt(tkn1, pw)
    assert dec_v1.plaintext == b"legacy-secret", "TKN1 解密失敗"
    assert dec_v1.format_version == 1
    assert dec_v1.hint == "" and dec_v1.label == "" and dec_v1.created_at is None
    meta_v1 = read_metadata(tkn1)
    assert meta_v1.format_version == 1 and meta_v1.hint == ""

    # (6b) CRLF 污染相容 — git autocrlf / Windows 存檔把 \n 變 \r\n，舊 .enc 仍要能解
    enc_crlf = encrypt(b"crlf-secret", pw, hint="h", label="L").replace(b"\n", b"\r\n")
    dec_crlf = decrypt(enc_crlf, pw)
    assert dec_crlf.plaintext == b"crlf-secret", "CRLF 污染密文解密失敗"
    assert dec_crlf.hint == "h" and dec_crlf.label == "L"
    # TKN1 + CRLF 也要過
    salt_c = os.urandom(SALT_LEN)
    tok_c = Fernet(_derive_key(pw, salt_c)).encrypt(b"legacy-crlf")
    tkn1_crlf = MAGIC_V1 + b"\r\n" + base64.urlsafe_b64encode(salt_c) + b"\r\n" + tok_c + b"\r\n"
    assert decrypt(tkn1_crlf, pw).plaintext == b"legacy-crlf", "TKN1 CRLF 解密失敗"

    # (6) bad magic — raise
    try:
        read_metadata(b"XXXX\nabc\ntoken")
    except ValueError:
        pass
    else:
        raise AssertionError("bad magic 沒有 raise")

    print("[OK] ucl_secrets_crypto self-test passed "
          f"({len(cases)} round-trip cases + TKN1 compat + hint cap/newline + bad-magic)")


if __name__ == "__main__":
    _run_selftest()
