// 區塊職責：secret 加解密（UCLS1）的 **Editor 端門面** —— 實作已搬進 SCP_Core（TASK-0300，Tim 2026-09-25）。
// 物理意義：真正的加解密只有一份：`SCP.Core.Secret.SCP_SecretCrypto`（Unity 與 senate.exe 編同一份 ⇒ 兩邊的檔互通）。
//          本檔保留原本的公開介面（`UCL_SecretMeta`／`Encrypt`／`Decrypt`／`ReadMetadata`／`IsUclsFormat`／`SelfTest`），
//          讓 SecretManager 頁、Install 視窗、Plurk 後台等既有呼叫端一行都不必改。
//          ⛔ 不要在這裡加任何演算法 —— 兩份實作分岔的那天，兩邊各自都會通過自己的自測。
//          格式說明與沿革：見 SCP_SecretCrypto 檔頭；搬家前的原始實作見 UCL_Core git 歷史。
#if UNITY_EDITOR
using System;
using SCP.Core.Secret;

namespace UCL.Core.EditorLib.SecretManager
{
    /// <summary>passphrase-free 讀回的 metadata（不需密碼）。欄位與 <see cref="SCP_SecretMeta"/> 一一對應。</summary>
    public class UCL_SecretMeta
    {
        public string Hint = "";
        public string Label = "";
        public string CreatedAt = "";
        public int FormatVersion = 0;   // 3 = UCLS1；1/2 = 舊 python TKN1/TKN2（不解）
    }

    /// <summary>Secret 對稱加解密 —— 轉呼叫 <see cref="SCP_SecretCrypto"/>。</summary>
    public static class UCL_SecretCrypto
    {
        public const string Magic = SCP_SecretCrypto.Magic;
        public const int FormatVersion = SCP_SecretCrypto.FormatVersion;
        public const int KdfIterations = SCP_SecretCrypto.KdfIterations;
        public const int HintMaxLen = SCP_SecretCrypto.HintMaxLen;

        public static byte[] Encrypt(byte[] plaintext, string passphrase, string hint = "", string label = "", DateTime? createdAt = null)
            => SCP_SecretCrypto.Encrypt(plaintext, passphrase, hint, label, createdAt);

        public static byte[] Decrypt(byte[] ciphertext, string passphrase)
            => SCP_SecretCrypto.Decrypt(ciphertext, passphrase);

        public static UCL_SecretMeta ReadMetadata(byte[] ciphertext)
        {
            SCP_SecretMeta m = SCP_SecretCrypto.ReadMetadata(ciphertext);
            return new UCL_SecretMeta { Hint = m.Hint, Label = m.Label, CreatedAt = m.CreatedAt, FormatVersion = m.FormatVersion };
        }

        public static bool IsUclsFormat(byte[] ciphertext) => SCP_SecretCrypto.IsUclsFormat(ciphertext);

        /// <summary>自測（跑的是 SCP_Core 那份實作）。呼叫：`senate ucmd run Invoke --arg type=UCL.Core.EditorLib.SecretManager.UCL_SecretCrypto --arg member=SelfTest`</summary>
        public static string SelfTest() => SCP_SecretCrypto.SelfTest();
    }
}
#endif
