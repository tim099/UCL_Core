// 區塊職責：掃描 secrets 資料夾的共用 helper — Page (T8) 跟 Daemon (T5) 共用單一掃描來源
// 物理意義：走 ucl_secret.py list --root <dir> --json subprocess 取每個 .enc 的 metadata
//          (label/hint/created/format_version/plain_exists)，passphrase-free。
//          de-scope UCL_Asset registry 後，.enc 的 TKN2 L:label 就是 single source of truth。
// 數值影響：純讀 (subprocess + JSON parse)，不改任何檔案。

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UCL.Core.JsonLib;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace UCL.Core.EditorLib.SecretManager
{
    // 區塊職責：單一 secret 的掃描結果 (對齊 ucl_secret.py show-hint --json 欄位)
    public class UCL_SecretInfo
    {
        public string EncPath = "";        // repo-relative
        public string PlainPath = "";      // repo-relative (.txt)
        public string Label = "";
        public string Hint = "";
        public string CreatedAt = "";
        public int FormatVersion = 0;
        public bool PlainExists = false;
        public string Error = "";          // 非空 = 該 .enc 解析失敗
    }

    /// <summary>
    /// 掃 secrets 資料夾的共用 helper。Page / Daemon 都走這裡，確保「掃描來源唯一」。
    /// 走 ucl_secret.py list --json，label/hint 直接讀 .enc metadata (passphrase-free)。
    /// </summary>
    public static class UCL_SecretScanner
    {
        // 區塊職責：consumer project 預設 secrets dir (repo-relative)
        public const string DefaultSecretsDir = "AgentCommands/_secrets";

        /// <summary>掃 rootDir (repo-relative) 下所有 .enc，回 metadata list。失敗回空 list + log。</summary>
        public static List<UCL_SecretInfo> Scan(string rootDirRelative = DefaultSecretsDir)
        {
            var result = new List<UCL_SecretInfo>();
            string repoRoot = UCL_RepoPath.RepoRoot;
            if (string.IsNullOrEmpty(repoRoot)) return result;

            string absRoot = Path.Combine(repoRoot, rootDirRelative);
            if (!Directory.Exists(absRoot)) return result;

            string cli = UclSecretPyPath();
            string python = ResolvePython();
            if (string.IsNullOrEmpty(cli) || !File.Exists(cli) || string.IsNullOrEmpty(python))
            {
                Debug.LogWarning("[SecretScanner] 找不到 ucl_secret.py 或 python，無法掃描");
                return result;
            }

            string stdout = RunCli(python, $"\"{cli}\" list --root \"{absRoot}\" --json", repoRoot);
            if (string.IsNullOrEmpty(stdout)) return result;

            try
            {
                var jd = JsonData.ParseJson(stdout.Trim());
                if (jd == null || !jd.IsArray) return result;
                for (int i = 0; i < jd.Count; i++)
                {
                    var item = jd[i];
                    if (item == null || !item.IsObject) continue;
                    var info = new UCL_SecretInfo
                    {
                        EncPath = ToRepoRelative(item.GetString("enc_path", ""), repoRoot),
                        Label = item.GetString("label", ""),
                        Hint = item.GetString("hint", ""),
                        CreatedAt = item.GetString("created_at", ""),
                        FormatVersion = item.GetInt("format_version", 0),
                        PlainExists = item.GetBool("plain_exists", false),
                        Error = item.GetString("error", ""),
                    };
                    info.PlainPath = DerivePlainPath(info.EncPath);
                    result.Add(info);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SecretScanner] JSON parse failed: {e.Message}");
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
            string r = repoRoot.Replace('\\', '/').TrimEnd('/') + "/";
            return a.StartsWith(r, StringComparison.OrdinalIgnoreCase) ? a.Substring(r.Length) : a;
        }

        static string DerivePlainPath(string encRel)
        {
            if (string.IsNullOrEmpty(encRel)) return encRel;
            return encRel.EndsWith(".enc") ? encRel.Substring(0, encRel.Length - 4) + ".txt" : encRel + ".txt";
        }

        internal static string UclSecretPyPath()
        {
            string corePathRel = UCL_EditorPath.CorePath;
            if (string.IsNullOrEmpty(corePathRel)) return null;
            string corePath = Path.GetFullPath(Path.Combine(UCL_RepoPath.UnityProjectRoot, corePathRel));
            return Path.Combine(corePath, "Tools~", "AgentCommands", "ucl_secret.py");
        }

        internal static string ResolvePython()
        {
            string envPy = Environment.GetEnvironmentVariable("PYTHON");
            if (!string.IsNullOrEmpty(envPy) && File.Exists(envPy)) return envPy;
#if UNITY_EDITOR_WIN
            string[] candidates = { "python.exe", "py.exe", "python3.exe" };
#else
            string[] candidates = { "python3", "python" };
#endif
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var c in candidates)
            {
                foreach (var p in path.Split(Path.PathSeparator))
                {
                    try
                    {
                        string full = Path.Combine(p.Trim(), c);
                        if (File.Exists(full)) return full;
                    }
                    catch { }
                }
            }
            return "python";
        }

        // 區塊職責：跑 CLI 抓 stdout (10s timeout, async 讀避免 redirect deadlock)
        internal static string RunCli(string python, string arguments, string workingDir)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                var psi = new ProcessStartInfo
                {
                    FileName = python,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = workingDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8,
                };
                psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                using (var proc = Process.Start(psi))
                {
                    string stdout = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(10000);
                    return stdout;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SecretScanner] CLI 執行失敗: {e.Message}");
                return null;
            }
        }
    }
}
#endif
