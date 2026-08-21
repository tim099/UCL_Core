// 區塊職責：掃描 secrets 資料夾的共用 helper — Page (T8) 跟 Daemon (T5) 共用單一掃描來源
// 物理意義：Tim 2026-07-22「全切 C#」後改純 C# — 直接列舉 _secrets 下 *.enc，走 UCL_SecretCrypto.ReadMetadata
//          讀 metadata（label/hint/created/format_version，passphrase-free），不再 shell-out python /
//          不需 cryptography 套件。舊 python-Fernet .enc（TKN1/TKN2）本 lib 讀不了 → 標記為舊格式待重建。
// 數值影響：純讀（File.ReadAllBytes + header 解析），不改任何檔案。
#if UNITY_EDITOR
using UCL.Core.EditorLib.AgentCommands;
using System;
using System.Collections.Generic;
using System.IO;

namespace UCL.Core.EditorLib.SecretManager
{
    // 區塊職責：單一 secret 的掃描結果
    public class UCL_SecretInfo
    {
        public string EncPath = "";        // repo-relative
        public string PlainPath = "";      // repo-relative (.txt)
        public string Label = "";
        public string Hint = "";
        public string CreatedAt = "";
        public int FormatVersion = 0;      // 3 = UCLS1(C# native)；0 = 舊格式/解析失敗（見 Error）
        public bool PlainExists = false;
        public string Error = "";          // 非空 = 該 .enc 非 UCLS1（舊 python 格式）或解析失敗
    }

    /// <summary>
    /// 掃 secrets 資料夾的共用 helper。Page / Daemon 都走這裡，確保「掃描來源唯一」。
    /// C# native（UCL_SecretCrypto.ReadMetadata）讀 .enc metadata，passphrase-free、零外部相依。
    /// </summary>
    public static class UCL_SecretScanner
    {
        // 區塊職責：consumer project 的 secrets dir（AgentCommands-relative；走 DataRoot 解析）
        // 物理意義：資料夾名 2026-08-21 起**由設定檔決定**（`UCL_SecretsPath`）——
        //          原本是寫死的 `"AgentCommands/_secrets"`，而那個字面值散在 7 處、改名要七處同步。
        // ⚠ 這裡從 `const` 變成 property，所以 `Scan` 的預設參數值必須改成 `null`
        //   （C# 的預設參數值必須是編譯期常數，property 不是）。`null` ⇒ 用當下設定。
        public static string DefaultSecretsDir => UCL_SecretsPath.AgentCommandsRelative;

        /// <summary>掃 rootDir（AgentCommands-relative；`null` ＝ 讀設定檔）下所有 .enc，回 metadata list。失敗回空 list。</summary>
        public static List<UCL_SecretInfo> Scan(string rootDirRelative = null)
        {
            var result = new List<UCL_SecretInfo>();
            if (string.IsNullOrEmpty(rootDirRelative)) rootDirRelative = UCL_SecretsPath.AgentCommandsRelative;

            // 走 canonical DataRoot 解析：_secrets 是持久狀態資料，AgentCommands 前綴映射到可 override 的
            // DataRoot（submodule / 資料搬遷 aware）；預設模式 = RepoRoot/AgentCommands/_secrets。
            string absRoot = UCL_AgentCommandsPath.ResolveData(rootDirRelative);
            if (!Directory.Exists(absRoot)) return result;

            string repoRoot = UCL_RepoPath.RepoRoot;

            string[] encFiles;
            try { encFiles = Directory.GetFiles(absRoot, "*.enc", SearchOption.AllDirectories); }
            catch { return result; }
            Array.Sort(encFiles, StringComparer.Ordinal);

            foreach (var encAbs in encFiles)
            {
                var info = new UCL_SecretInfo
                {
                    EncPath = ToRepoRelative(encAbs, repoRoot),
                };
                info.PlainPath = DerivePlainPath(info.EncPath);
                // 明文 .txt 是否已在本機（判 🔒 待安裝 / ✅ 已在）
                string plainAbs = encAbs.EndsWith(".enc", StringComparison.OrdinalIgnoreCase)
                    ? encAbs.Substring(0, encAbs.Length - 4) + ".txt"
                    : encAbs + ".txt";
                info.PlainExists = File.Exists(plainAbs);

                try
                {
                    byte[] bytes = File.ReadAllBytes(encAbs);
                    var meta = UCL_SecretCrypto.ReadMetadata(bytes);   // 非 UCLS1 → FormatException
                    info.Label = meta.Label;
                    info.Hint = meta.Hint;
                    info.CreatedAt = meta.CreatedAt;
                    info.FormatVersion = meta.FormatVersion;
                }
                catch (Exception e)
                {
                    // 非 UCLS1（多半是舊 python-Fernet TKN1/TKN2）或壞檔 → 標記，引導用「明文加密」重建
                    byte[] head = null;
                    try { head = File.ReadAllBytes(encAbs); } catch { }
                    bool isUcls = head != null && UCL_SecretCrypto.IsUclsFormat(head);
                    info.FormatVersion = 0;
                    info.Error = isUcls
                        ? $"UCLS1 解析失敗: {e.Message}"
                        : "舊 python 格式（TKN1/TKN2）或未知 — 用下方『明文加密』對 .txt 重建即可";
                }
                result.Add(info);
            }
            return result;
        }

        // ===========================================================
        // Helpers
        // ===========================================================

        static string ToRepoRelative(string abs, string repoRoot)
        {
            if (string.IsNullOrEmpty(abs)) return abs;
            string a = abs.Replace('\\', '/');
            if (string.IsNullOrEmpty(repoRoot)) return a;
            string r = repoRoot.Replace('\\', '/').TrimEnd('/') + "/";
            return a.StartsWith(r, StringComparison.OrdinalIgnoreCase) ? a.Substring(r.Length) : a;
        }

        static string DerivePlainPath(string encRel)
        {
            if (string.IsNullOrEmpty(encRel)) return encRel;
            return encRel.EndsWith(".enc") ? encRel.Substring(0, encRel.Length - 4) + ".txt" : encRel + ".txt";
        }
    }
}
#endif
