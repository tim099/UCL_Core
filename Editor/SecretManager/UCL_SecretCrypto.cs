// 區塊職責：secret 對稱加解密的 C# native 實作（UCLS1 格式）— 取代 python ucl_secret.py + cryptography 套件
// 物理意義：Tim 2026-07-22 拍板「全切 C#」— SecretManagerPage 不再 shell-out python / 不需裝 cryptography 插件。
//          純 .NET BCL（System.Security.Cryptography）：PBKDF2-SHA256 導 key + AES-256-CBC 加密 +
//          HMAC-SHA256 Encrypt-then-MAC 防竄改。Unity 6（.NET Standard 2.1）原生支援，零外部相依。
// 設計取捨：
//   - 不重現 python Fernet/TKN2（Tim 明示「不需重現、免 migration、明文都在本機」）→ 用自訂 UCLS1 容器；
//     舊 python-Fernet .enc 本 lib 讀不了（ReadMetadata 丟 FormatException，頁面標舊格式請重加密）。
//   - Encrypt-then-MAC：先驗 HMAC 才解密 → 密碼錯/密文竄改在 AES 前就擋下（不洩 padding oracle）。
//   - metadata（hint/label/created）走明文行、不參與 KDF → passphrase-free 可讀（保留 TKN2 失憶救援設計）。
//   - HMAC 涵蓋除 M 行外的整份序列化字串 → 竄改任一 metadata / iv / 密文都會驗失敗。
// 數值影響：KDF 200k iter（OWASP 下限+margin）；salt/iv 各 16 byte 每次隨機；key 材料 64 byte 拆
//          enc(32, AES-256) + mac(32, HMAC-SHA256)。密碼錯 → CryptographicException（不落地明文）。
// UCLS1 格式（\n 分隔的文字行；base64 為標準非 urlsafe）：
//   UCLS1
//   S:<salt_b64>        <- PBKDF2 salt (16 byte)
//   N:<iterations>      <- KDF 輪數 (存檔內, 未來可調不破舊檔)
//   H:<hint>            <- 明文提示 (passphrase-free; 不可含換行)
//   C:<created_iso>     <- 建立時間 ISO8601 UTC
//   L:<label>           <- 明文標籤 (不可含換行)
//   V:<iv_b64>          <- AES-CBC IV (16 byte)
//   M:<mac_b64>         <- HMAC-SHA256 (涵蓋上列除本行外的整份 + 密文行)
//   <ciphertext_b64>    <- AES-256-CBC(PKCS7) 密文 (最後一行)
#if UNITY_EDITOR
using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace UCL.Core.EditorLib.SecretManager
{
    /// <summary>passphrase-free 讀回的 metadata（不需密碼）。</summary>
    public class UCL_SecretMeta
    {
        public string Hint = "";
        public string Label = "";
        public string CreatedAt = "";
        public int FormatVersion = 0;   // 3 = UCLS1（C# native）；1/2 = 舊 python TKN1/TKN2（本 lib 不解）
    }

    /// <summary>
    /// Secret 對稱加解密（C# native, UCLS1）。純 System.Security.Cryptography，無外部套件。
    /// 用法：Encrypt(明文, passphrase, hint, label) → 密文 bytes；Decrypt(密文, passphrase) → 明文 bytes；
    ///       ReadMetadata(密文) → hint/label/created（passphrase-free）。
    /// </summary>
    public static class UCL_SecretCrypto
    {
        // 區塊職責：格式常數（對齊檔頭格式說明）
        // 物理意義：MAGIC 區分本格式；KDF 輪數 / salt / iv 長度 = 安全參數；FORMAT_VERSION 進 metadata。
        // 數值影響：改 KDF_ITERATIONS 只影響「新加密」的檔（輪數存在 N 行，舊檔照存的輪數解）。
        public const string Magic = "UCLS1";
        public const int FormatVersion = 3;          // 接續 python TKN1(1)/TKN2(2)，C# native = 3
        public const int KdfIterations = 200_000;
        const int SaltLen = 16;
        const int IvLen = 16;
        const int AesKeyLen = 32;                     // AES-256
        const int MacKeyLen = 32;                     // HMAC-SHA256
        public const int HintMaxLen = 256;

        const string PrefixSalt = "S:";
        const string PrefixIter = "N:";
        const string PrefixHint = "H:";
        const string PrefixCreated = "C:";
        const string PrefixLabel = "L:";
        const string PrefixIv = "V:";
        const string PrefixMac = "M:";

        // ===========================================================
        // 區塊職責：passphrase → (encKey, macKey) — PBKDF2-HMAC-SHA256
        // 物理意義：同 salt + 同 passphrase + 同 iter → 同 64 byte，前 32 給 AES-256、後 32 給 HMAC。
        // 數值影響：iterations 越高越慢越難 brute-force；salt 每檔隨機 → 同 passphrase 兩檔 key 不同。
        // ===========================================================
        static (byte[] encKey, byte[] macKey) DeriveKeys(string passphrase, byte[] salt, int iterations)
        {
            // .NET Standard 2.1：Rfc2898DeriveBytes 帶 HashAlgorithmName overload（預設是 SHA1，這裡明指 SHA256）
            using (var kdf = new Rfc2898DeriveBytes(
                Encoding.UTF8.GetBytes(passphrase ?? ""), salt, iterations, HashAlgorithmName.SHA256))
            {
                byte[] material = kdf.GetBytes(AesKeyLen + MacKeyLen);
                byte[] encKey = new byte[AesKeyLen];
                byte[] macKey = new byte[MacKeyLen];
                Buffer.BlockCopy(material, 0, encKey, 0, AesKeyLen);
                Buffer.BlockCopy(material, AesKeyLen, macKey, 0, MacKeyLen);
                return (encKey, macKey);
            }
        }

        // 區塊職責：組「除 M 行外」的整份序列化字串 — HMAC 的輸入 + 檔案內容的骨架
        // 物理意義：加密時算此字串 → HMAC → 插入 M 行成最終檔；解密時用解析出的欄位重組同字串 → 重算 HMAC 比對。
        //          竄改任一欄位（含 iv / metadata / 密文）都會讓重組字串變動 → HMAC 不符 → 驗失敗。
        static string BuildSignedBody(string saltB64, int iter, string hint, string created, string label, string ivB64, string ctB64)
        {
            var sb = new StringBuilder();
            sb.Append(Magic).Append('\n');
            sb.Append(PrefixSalt).Append(saltB64).Append('\n');
            sb.Append(PrefixIter).Append(iter.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append(PrefixHint).Append(hint).Append('\n');
            sb.Append(PrefixCreated).Append(created).Append('\n');
            sb.Append(PrefixLabel).Append(label).Append('\n');
            sb.Append(PrefixIv).Append(ivB64).Append('\n');
            sb.Append(ctB64);   // 密文行為最後（無尾換行）
            return sb.ToString();
        }

        static void ValidateSingleLine(string value, string field)
        {
            if (value != null && (value.IndexOf('\n') >= 0 || value.IndexOf('\r') >= 0))
                throw new ArgumentException($"{field} 不可含換行字元（會破壞 UCLS1 行格式）");
        }

        // ===========================================================
        // 區塊職責：加密 — 明文 bytes → UCLS1 密文 bytes
        // 物理意義：隨機 salt+iv → 導 key → AES-256-CBC(PKCS7) 加密 → HMAC over signed body → 組檔。
        // 數值影響：每次加密 salt/iv 都重生 → 同明文同 passphrase 兩次輸出不同密文（語意安全）。
        // ===========================================================
        public static byte[] Encrypt(byte[] plaintext, string passphrase, string hint = "", string label = "", DateTime? createdAt = null)
        {
            if (plaintext == null) throw new ArgumentNullException(nameof(plaintext));
            if (string.IsNullOrEmpty(passphrase)) throw new ArgumentException("passphrase 不可為空");
            hint = hint ?? "";
            label = label ?? "";
            ValidateSingleLine(hint, "hint");
            ValidateSingleLine(label, "label");
            if (hint.Length > HintMaxLen)
                throw new ArgumentException($"hint 超過 {HintMaxLen} char 上限（目前 {hint.Length}）");

            byte[] salt = new byte[SaltLen];
            byte[] iv = new byte[IvLen];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(salt);
                rng.GetBytes(iv);
            }

            var (encKey, macKey) = DeriveKeys(passphrase, salt, KdfIterations);

            byte[] ct;
            using (var aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = encKey;
                aes.IV = iv;
                using (var enc = aes.CreateEncryptor())
                {
                    ct = enc.TransformFinalBlock(plaintext, 0, plaintext.Length);
                }
            }

            string saltB64 = Convert.ToBase64String(salt);
            string ivB64 = Convert.ToBase64String(iv);
            string ctB64 = Convert.ToBase64String(ct);
            string created = (createdAt ?? DateTime.UtcNow).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

            string signedBody = BuildSignedBody(saltB64, KdfIterations, hint, created, label, ivB64, ctB64);
            byte[] mac;
            using (var hmac = new HMACSHA256(macKey))
            {
                mac = hmac.ComputeHash(Encoding.UTF8.GetBytes(signedBody));
            }
            string macB64 = Convert.ToBase64String(mac);

            // 最終檔 = magic..V 行 + M 行 + 密文行（M 插在 V 與 ct 之間）
            var sb = new StringBuilder();
            sb.Append(Magic).Append('\n');
            sb.Append(PrefixSalt).Append(saltB64).Append('\n');
            sb.Append(PrefixIter).Append(KdfIterations.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append(PrefixHint).Append(hint).Append('\n');
            sb.Append(PrefixCreated).Append(created).Append('\n');
            sb.Append(PrefixLabel).Append(label).Append('\n');
            sb.Append(PrefixIv).Append(ivB64).Append('\n');
            sb.Append(PrefixMac).Append(macB64).Append('\n');
            sb.Append(ctB64);
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        // 區塊職責：解析 UCLS1 檔 → 各欄位（純結構, 不需 passphrase）
        // 物理意義：\n 切行、驗 magic、抽 S/N/H/C/L/V/M 前綴 + 密文行。malformed / 非 UCLS1 → FormatException。
        static Parsed ParseFile(byte[] ciphertext)
        {
            if (ciphertext == null || ciphertext.Length == 0)
                throw new FormatException("空密文");
            // 正規化 CRLF（git autocrlf / Windows 編輯器）→ 不破壞 base64（絕不含 \r/\n）與已驗證無換行的 metadata
            string text = Encoding.UTF8.GetString(ciphertext).Replace("\r\n", "\n").TrimEnd('\n', '\r');
            string[] lines = text.Split('\n');
            if (lines.Length < 9 || lines[0] != Magic)
                throw new FormatException($"非 {Magic} 格式（可能是舊 python TKN1/TKN2，本 lib 不解；請用明文重加密）");

            var p = new Parsed
            {
                SaltB64 = StripPrefix(lines[1], PrefixSalt, "S"),
                IterStr = StripPrefix(lines[2], PrefixIter, "N"),
                Hint = StripPrefix(lines[3], PrefixHint, "H"),
                Created = StripPrefix(lines[4], PrefixCreated, "C"),
                Label = StripPrefix(lines[5], PrefixLabel, "L"),
                IvB64 = StripPrefix(lines[6], PrefixIv, "V"),
                MacB64 = StripPrefix(lines[7], PrefixMac, "M"),
                CtB64 = lines[8],   // 密文為第 9 行（base64 無換行，恰一行）
            };
            if (!int.TryParse(p.IterStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out p.Iter) || p.Iter <= 0)
                throw new FormatException("N: 輪數欄非正整數");
            return p;
        }

        class Parsed
        {
            public string SaltB64, IterStr, Hint, Created, Label, IvB64, MacB64, CtB64;
            public int Iter;
        }

        static string StripPrefix(string line, string prefix, string field)
        {
            if (line == null || !line.StartsWith(prefix, StringComparison.Ordinal))
                throw new FormatException($"{field} 行格式異常（缺 {prefix} 前綴）");
            return line.Substring(prefix.Length);
        }

        // ===========================================================
        // 區塊職責：passphrase-free 讀 metadata（hint/label/created）— 失憶救援 + listing 用
        // 物理意義：只解析 header 行、不驗 HMAC / 不解密 → 密碼對錯都讀得到。非 UCLS1 → FormatException。
        // ===========================================================
        public static UCL_SecretMeta ReadMetadata(byte[] ciphertext)
        {
            var p = ParseFile(ciphertext);
            return new UCL_SecretMeta
            {
                Hint = p.Hint,
                Label = p.Label,
                CreatedAt = p.Created,
                FormatVersion = FormatVersion,
            };
        }

        /// <summary>快速判斷 bytes 是否為本 lib 的 UCLS1 格式（不丟例外）。</summary>
        public static bool IsUclsFormat(byte[] ciphertext)
        {
            if (ciphertext == null || ciphertext.Length < 6) return false;
            // 比對開頭 "UCLS1\n"（容 CRLF）
            try
            {
                string head = Encoding.UTF8.GetString(ciphertext, 0, Math.Min(8, ciphertext.Length));
                return head.StartsWith(Magic + "\n", StringComparison.Ordinal)
                    || head.StartsWith(Magic + "\r\n", StringComparison.Ordinal);
            }
            catch { return false; }
        }

        // ===========================================================
        // 區塊職責：解密 — UCLS1 密文 + passphrase → 明文 bytes
        // 物理意義：導 key → 重算 HMAC over signed body → FixedTimeEquals 比對（密碼錯/竄改則不符）→ 通過才 AES 解密。
        // 數值影響：Encrypt-then-MAC 順序保證：密碼錯在 HMAC 階段就 throw，AES 解密只在 MAC 通過後跑
        //          → 不洩 padding oracle、不對錯密碼回傳半調子明文。
        // ===========================================================
        public static byte[] Decrypt(byte[] ciphertext, string passphrase)
        {
            if (string.IsNullOrEmpty(passphrase)) throw new ArgumentException("passphrase 不可為空");
            var p = ParseFile(ciphertext);

            byte[] salt = Convert.FromBase64String(p.SaltB64);
            byte[] iv = Convert.FromBase64String(p.IvB64);
            byte[] expectedMac = Convert.FromBase64String(p.MacB64);
            byte[] ct = Convert.FromBase64String(p.CtB64);

            var (encKey, macKey) = DeriveKeys(passphrase, salt, p.Iter);

            // 先驗 HMAC（用檔內存的欄位重組 signed body）— 密碼錯 → macKey 錯 → HMAC 不符
            string signedBody = BuildSignedBody(p.SaltB64, p.Iter, p.Hint, p.Created, p.Label, p.IvB64, p.CtB64);
            byte[] actualMac;
            using (var hmac = new HMACSHA256(macKey))
            {
                actualMac = hmac.ComputeHash(Encoding.UTF8.GetBytes(signedBody));
            }
            if (!CryptographicOperations.FixedTimeEquals(actualMac, expectedMac))
                throw new CryptographicException("HMAC 驗證失敗 — passphrase 錯誤或密文已被竄改");

            using (var aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = encKey;
                aes.IV = iv;
                using (var dec = aes.CreateDecryptor())
                {
                    return dec.TransformFinalBlock(ct, 0, ct.Length);
                }
            }
        }

        // ===========================================================
        // 區塊職責：自測 — round-trip 多案例 + 錯密碼拒絕 + 竄改偵測（對齊 python _run_selftest）
        // 物理意義：可經 Cmd_Invoke（reflection）在 Editor 內觸發驗真正的 C# 實作，不必另寫測試場景。
        //          呼叫：run_cmd.py run Invoke --arg type=UCL.Core.EditorLib.SecretManager.UCL_SecretCrypto --arg member=SelfTest
        // 數值影響：全過回摘要字串；任一斷言失敗 throw（Cmd 標 Failed，agent 端可見）。
        // ===========================================================
        public static string SelfTest()
        {
            const string pass = "self-test-pass-2026";
            var cases = new (byte[] plain, string hint, string label)[]
            {
                (Encoding.UTF8.GetBytes(""), "", ""),
                (Encoding.UTF8.GetBytes("hello-secret"), "生日後三碼", "EOV Token"),
                (Encoding.UTF8.GetBytes("多位元組·祕密🔐"), new string('x', HintMaxLen), "Unicode Case"),
                (MakePattern(512), "", "Binary Case"),
            };
            int n = 0;
            foreach (var c in cases)
            {
                byte[] enc = Encrypt(c.plain, pass, c.hint, c.label);
                var meta = ReadMetadata(enc);   // passphrase-free
                if (meta.Hint != c.hint) throw new Exception($"case {n}: hint 不符 metadata");
                if (meta.Label != c.label) throw new Exception($"case {n}: label 不符 metadata");
                byte[] dec = Decrypt(enc, pass);
                if (!ByteEq(dec, c.plain)) throw new Exception($"case {n}: round-trip 明文不符 (len={c.plain.Length})");
                n++;
            }

            // 錯密碼 → 必 throw CryptographicException（HMAC 擋）
            byte[] e2 = Encrypt(Encoding.UTF8.GetBytes("secret"), pass, "h", "L");
            bool rejected = false;
            try { Decrypt(e2, "wrong-passphrase"); }
            catch (CryptographicException) { rejected = true; }
            if (!rejected) throw new Exception("錯密碼未被拒絕（HMAC 驗證失效）");

            // 竄改密文 → 必 throw（翻最後一個 byte）
            byte[] tampered = (byte[])e2.Clone();
            tampered[tampered.Length - 1] ^= 0x01;
            bool tamperCaught = false;
            try { Decrypt(tampered, pass); }
            catch (Exception) { tamperCaught = true; }
            if (!tamperCaught) throw new Exception("密文竄改未被偵測");

            return $"OK: UCLS1 self-test passed ({cases.Length} round-trip cases + wrong-pass rejected + tamper detected)";
        }

        static byte[] MakePattern(int len)
        {
            byte[] b = new byte[len];
            for (int i = 0; i < len; i++) b[i] = (byte)(i * 31 + 7);
            return b;
        }

        static bool ByteEq(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
#endif
