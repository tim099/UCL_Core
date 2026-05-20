// 區塊職責：通用 secret 解密安裝視窗 — 偵測 .enc 存在但本機明文缺時引導輸入 passphrase 解密落地
// 物理意義：抽 EOV 端 RCG_DiscordTokenInstallWindow 的硬編碼路徑為 UCL_SecretEntry 參數化，
//          跨機器同步時 .enc 跟 repo 走、明文 .txt 永遠 gitignored，新 clone 必須跑一次解密還原。
// 數值影響：解密走 ucl_secret.py decrypt --stdin-passphrase (passphrase 經 stdin pipe 不留 argv)；
//          hint 顯示走 ucl_secret.py show-hint --json (passphrase-free)；開資料夾走 EditorUtility.RevealInFinder。

#if UNITY_EDITOR
using System;
using System.Diagnostics;
using System.IO;
using UCL.Core.EditorLib;
using UCL.Core.JsonLib;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace UCL.Core.EditorLib.SecretManager
{
    // 區塊職責：單一 secret 的描述資料 — 給 window / daemon / registry 共用
    // 物理意義：PlainPath/EncPath 是 repo-relative 路徑 (相對 git root)；Label 人類可讀；
    //          HelpUrl 取得該 token 的官方頁面；OnInstalled/OnDismissed 是可選 callback
    [Serializable]
    public class UCL_SecretEntry
    {
        public string PlainPath = "";   // e.g. "AgentCommands/_secrets/discord_bot_token.txt"
        public string EncPath = "";     // e.g. "AgentCommands/_secrets/discord_bot_token.enc"
        public string Label = "";       // e.g. "Discord Inbound Bot Token"
        public string HelpUrl = "";     // 取得 token 的官方 URL (忘記 passphrase 時 reset 用)
        [NonSerialized] public Action OnInstalled;   // 解密完成 callback (e.g. restart daemon)
        [NonSerialized] public Action OnDismissed;   // 使用者勾「稍後再說」callback
    }

    /// <summary>
    /// 通用 secret 解密安裝視窗 (passphrase 介面)。
    ///
    /// 三種出現方式 (對齊 RCG_DiscordTokenInstallWindow):
    /// <list type="number">
    ///   <item>daemon / registry tick 偵測到 .enc 存在 + 明文缺 + 本 session 沒彈過 → MaybeAutoPopup</item>
    ///   <item>SecretManagerPage / 選單手動 ShowFor</item>
    ///   <item>使用者按「稍後再說」勾 → EditorPrefs 記版本, 不再 auto popup</item>
    /// </list>
    ///
    /// 失憶救援雙路徑:
    /// <list type="bullet">
    ///   <item>路徑 A — hint 顯示框 (passphrase-free) 喚回密碼</item>
    ///   <item>路徑 B — 「📂 開啟資料夾」手動把原始 token 貼進 .txt</item>
    /// </list>
    /// </summary>
    public class UCL_SecretInstallWindow : EditorWindow
    {
        // 區塊職責：當前 window 綁定的 secret entry
        UCL_SecretEntry m_Entry;

        // 區塊職責：session-once guard 防同 session 重複 auto popup (per EncPath)
        static System.Collections.Generic.HashSet<string> s_AutoPoppedPaths = new System.Collections.Generic.HashSet<string>();

        // 區塊職責：EditorPrefs 跨 session「稍後再說」狀態 (per project fingerprint + EncPath)
        const string CurrentDismissVersion = "1";
        static string s_ProjectFingerprint;
        static string ProjectFingerprint =>
            s_ProjectFingerprint ??= Application.dataPath.GetHashCode().ToString("X");
        static string DismissKey(string encPath) =>
            $"UCL.SecretInstall.Dismissed@{ProjectFingerprint}:{encPath}";

        // ===========================================================
        // UI 狀態
        // ===========================================================
        string m_Passphrase = "";
        string m_StatusMsg = "";
        MessageType m_StatusType = MessageType.None;
        bool m_Working = false;
        bool m_DontAskAgain = false;
        // hint metadata 快取 (passphrase-free, 開窗時讀一次)
        string m_Hint = "";
        string m_MetaLabel = "";
        int m_FormatVersion = 0;
        bool m_MetaLoaded = false;

        // ===========================================================
        // 入口
        // ===========================================================

        /// <summary>顯式開啟 (SecretManagerPage / 選單手動).</summary>
        public static UCL_SecretInstallWindow ShowFor(UCL_SecretEntry entry)
        {
            var w = GetWindow<UCL_SecretInstallWindow>(true, "Secret Install", true);
            w.minSize = new Vector2(460, 300);
            w.maxSize = new Vector2(820, 420);
            w.Bind(entry);
            w.Focus();
            return w;
        }

        /// <summary>daemon / registry tick 用的條件彈窗. 回傳 true 表本次有實際 push window.</summary>
        public static bool MaybeAutoPopup(UCL_SecretEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.EncPath)) return false;
            // (1) 本 session 已彈過此檔 → 不再彈
            if (s_AutoPoppedPaths.Contains(entry.EncPath)) return false;
            // (2) 使用者勾過稍後再說當前版本 → 不再彈
            if (EditorPrefs.GetString(DismissKey(entry.EncPath), "") == CurrentDismissVersion) return false;
            // (3) 偵測檔案狀態 — 只有 enc 存在但明文缺才彈 (= cross-machine 第一次 clone)
            if (!ShouldShow(entry)) return false;

            s_AutoPoppedPaths.Add(entry.EncPath);
            // delayCall 排到下個 idle tick 才實際 push, 避免 daemon tick 內 GetWindow 重入
            EditorApplication.delayCall += () => ShowFor(entry);
            return true;
        }

        static bool ShouldShow(UCL_SecretEntry entry)
        {
            string root = UCL_RepoPath.RepoRoot;
            if (string.IsNullOrEmpty(root)) return false;
            bool plainExists = File.Exists(Path.Combine(root, entry.PlainPath));
            bool encExists = File.Exists(Path.Combine(root, entry.EncPath));
            return encExists && !plainExists;
        }

        void Bind(UCL_SecretEntry entry)
        {
            m_Entry = entry;
            m_Passphrase = "";
            m_StatusMsg = "";
            m_StatusType = MessageType.None;
            m_MetaLoaded = false;
            LoadMetadata();
        }

        // ===========================================================
        // OnGUI
        // ===========================================================
        void OnGUI()
        {
            if (m_Entry == null)
            {
                EditorGUILayout.HelpBox("No secret entry bound.", MessageType.Warning);
                return;
            }
            string root = UCL_RepoPath.RepoRoot;
            string plainPath = Path.Combine(root ?? "", m_Entry.PlainPath);
            string encPath = Path.Combine(root ?? "", m_Entry.EncPath);
            bool plainExists = File.Exists(plainPath);
            bool encExists = File.Exists(encPath);

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField(string.IsNullOrEmpty(m_Entry.Label) ? "Secret — Install" : $"{m_Entry.Label} — Install",
                                       EditorStyles.boldLabel);
            EditorGUILayout.Space(2);

            // 區塊職責：狀態列 — 三種情境提示
            if (plainExists)
            {
                EditorGUILayout.HelpBox("本機明文已存在; daemon 應該已能使用. 再按 Decrypt 會覆蓋現有明文.", MessageType.Info);
            }
            else if (!encExists)
            {
                EditorGUILayout.HelpBox(
                    "找不到 .enc 加密檔. 請先在本機建明文後跑:\n" +
                    $"  python <UCL_Core>/Tools~/AgentCommands/ucl_secret.py encrypt {m_Entry.PlainPath} --hint \"...\"\n" +
                    "然後 commit 產出的 .enc.", MessageType.Warning);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "本機沒有明文 (gitignored), 但 .enc 已 commit 進 repo.\n" +
                    "輸入加密時的 passphrase 解密 → 寫進明文檔.", MessageType.Info);
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Plaintext path", m_Entry.PlainPath);
            EditorGUILayout.LabelField("Encrypted path", m_Entry.EncPath);

            // 區塊職責：⭐ hint 顯示框 (失憶救援路徑 A) — passphrase-free, 開窗時已讀
            EditorGUILayout.Space(4);
            using (new GUILayout.VerticalScope("box"))
            {
                if (!m_MetaLoaded)
                {
                    EditorGUILayout.LabelField("提示", "(讀取中…)");
                }
                else if (m_FormatVersion <= 1)
                {
                    EditorGUILayout.LabelField("提示", "(舊格式 TKN1, 無提示 — rotate 一次可補)");
                }
                else
                {
                    EditorGUILayout.LabelField("提示 (hint)", string.IsNullOrEmpty(m_Hint) ? "(無提示)" : m_Hint);
                }
            }

            EditorGUILayout.Space(6);

            // 區塊職責：passphrase 輸入欄 — PasswordField 黑點遮罩
            using (new EditorGUI.DisabledScope(m_Working || !encExists))
            {
                m_Passphrase = EditorGUILayout.PasswordField("Passphrase", m_Passphrase);
            }
            EditorGUILayout.Space(4);

            // 區塊職責：動作按鈕列 — Decrypt / 開資料夾 / 忘記passphrase / Cancel
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(m_Working || !encExists || string.IsNullOrEmpty(m_Passphrase)))
                {
                    if (GUILayout.Button("Decrypt & Install", GUILayout.Height(26)))
                    {
                        DoDecrypt(root, plainPath, encPath);
                    }
                }
                // ⭐ 開資料夾 (失憶救援路徑 B) — 一鍵定位明文該落地的資料夾, 手動貼上
                if (GUILayout.Button("📂 開啟資料夾", GUILayout.Height(26), GUILayout.Width(120)))
                {
                    RevealPlainFolder(plainPath);
                }
                if (GUILayout.Button("忘記 passphrase?", GUILayout.Height(26), GUILayout.Width(130)))
                {
                    ShowForgotHelp(plainPath);
                }
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Cancel", GUILayout.Width(70), GUILayout.Height(26)))
                {
                    m_Passphrase = "";
                    Close();
                }
            }

            // 區塊職責：路徑 B 灰字提示
            EditorGUILayout.LabelField(
                "忘記密碼？把原始 token 存成上面的明文檔貼這裡即可 (明文 gitignored, daemon 一樣讀得到)。",
                EditorStyles.miniLabel);

            EditorGUILayout.Space(4);

            // 區塊職責：「稍後再說」勾選
            m_DontAskAgain = EditorGUILayout.ToggleLeft(
                "稍後再說 (本 secret 不再自動彈出, 直到下次手動開或重灌)", m_DontAskAgain);
            if (m_DontAskAgain)
                EditorPrefs.SetString(DismissKey(m_Entry.EncPath), CurrentDismissVersion);
            else
                EditorPrefs.DeleteKey(DismissKey(m_Entry.EncPath));

            EditorGUILayout.Space(6);
            if (!string.IsNullOrEmpty(m_StatusMsg))
            {
                EditorGUILayout.HelpBox(m_StatusMsg, m_StatusType);
            }
        }

        // ===========================================================
        // 開資料夾 — ⭐ 路徑 B 救援 (對齊 ucl_secret.py reveal)
        // 物理意義：RevealInFinder 對不存在的檔會定位到 parent 資料夾, 正是手動貼上場景所需
        // ===========================================================
        void RevealPlainFolder(string plainPath)
        {
            try
            {
                string folder = Path.GetDirectoryName(plainPath);
                if (!Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);   // 確保資料夾存在才好貼
                }
                EditorUtility.RevealInFinder(plainPath);
                SetStatus($"已開資料夾: {folder}\n把原始 token 存成 {Path.GetFileName(plainPath)} (純文字單行) 即可手動安裝。",
                          MessageType.Info);
            }
            catch (Exception e)
            {
                SetStatus($"開資料夾失敗: {e.Message}", MessageType.Error);
            }
        }

        // ===========================================================
        // 忘記 passphrase 說明 popup — 並列雙救援路徑
        // ===========================================================
        void ShowForgotHelp(string plainPath)
        {
            string hintLine = (m_FormatVersion >= 2)
                ? (string.IsNullOrEmpty(m_Hint) ? "(此 secret 沒設提示)" : m_Hint)
                : "(舊 TKN1 格式無提示)";
            string body =
                $"提示: {hintLine}\n\n" +
                "路徑 A — 想起密碼:\n" +
                "  加密用 PBKDF2 200k 輪 + Fernet, 設計上無法 brute-force, 只能靠提示喚回。\n\n" +
                "路徑 B — 手動貼上明文 (推薦, 若手邊有原始 token):\n" +
                $"  1. 從來源 reset / 取得 token (見下方 Help URL)\n" +
                $"  2. 按「📂 開啟資料夾」\n" +
                $"  3. 把 token 存成 {Path.GetFileName(plainPath)} (純文字單行)\n" +
                "  → daemon 下個 tick 即偵測到明文, 正常運作; .enc 之後再 rotate 補救。";

            bool openUrl = EditorUtility.DisplayDialog(
                $"{m_Entry.Label} — 忘記 passphrase?",
                body,
                string.IsNullOrEmpty(m_Entry.HelpUrl) ? "知道了" : "開啟取得 token 的頁面",
                "關閉");
            if (openUrl && !string.IsNullOrEmpty(m_Entry.HelpUrl))
            {
                Application.OpenURL(m_Entry.HelpUrl);
            }
        }

        // ===========================================================
        // 讀 hint metadata — passphrase-free (走 ucl_secret.py show-hint --json)
        // ===========================================================
        void LoadMetadata()
        {
            m_Hint = "";
            m_MetaLabel = "";
            m_FormatVersion = 0;
            m_MetaLoaded = false;

            string root = UCL_RepoPath.RepoRoot;
            string encPath = Path.Combine(root ?? "", m_Entry.EncPath);
            if (!File.Exists(encPath)) { m_MetaLoaded = true; return; }

            string cli = UclSecretPyPath();
            string python = ResolvePython();
            if (string.IsNullOrEmpty(cli) || !File.Exists(cli) || string.IsNullOrEmpty(python))
            {
                m_MetaLoaded = true;
                return;
            }
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = python,
                    Arguments = $"\"{cli}\" show-hint \"{encPath}\" --json",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = root,
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
                    if (proc.ExitCode == 0 && !string.IsNullOrEmpty(stdout))
                    {
                        var jd = JsonData.ParseJson(stdout.Trim());
                        if (jd != null && jd.IsObject)
                        {
                            m_Hint = jd.GetString("hint", "");
                            m_MetaLabel = jd.GetString("label", "");
                            m_FormatVersion = jd.GetInt("format_version", 0);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SecretInstall] show-hint 讀取失敗: {e.Message}");
            }
            m_MetaLoaded = true;
        }

        // ===========================================================
        // Decrypt action — 走 ucl_secret.py decrypt --stdin-passphrase
        // ===========================================================
        void DoDecrypt(string root, string plainPath, string encPath)
        {
            string cli = UclSecretPyPath();
            if (string.IsNullOrEmpty(cli) || !File.Exists(cli))
            {
                SetStatus($"找不到 CLI: {cli}", MessageType.Error);
                return;
            }
            string python = ResolvePython();
            if (string.IsNullOrEmpty(python))
            {
                SetStatus("找不到 python 可執行檔 (PATH 內沒 python.exe / py.exe / python3)", MessageType.Error);
                return;
            }

            m_Working = true;
            Repaint();
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = python,
                    Arguments = $"\"{cli}\" decrypt \"{encPath}\" --stdin-passphrase",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = root,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8,
                };
                psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";

                using (var proc = Process.Start(psi))
                {
                    proc.StandardInput.WriteLine(m_Passphrase);
                    proc.StandardInput.Close();
                    string stdout = proc.StandardOutput.ReadToEnd();
                    string stderr = proc.StandardError.ReadToEnd();
                    proc.WaitForExit(30000);
                    int code = proc.ExitCode;
                    if (code == 0 && File.Exists(plainPath))
                    {
                        SetStatus($"✓ 解密成功, 已寫入 {plainPath}\n  size={new FileInfo(plainPath).Length} bytes", MessageType.Info);
                        m_Passphrase = "";
                        EditorPrefs.DeleteKey(DismissKey(m_Entry.EncPath));
                        m_DontAskAgain = false;
                        m_Entry.OnInstalled?.Invoke();
                        EditorApplication.delayCall += () => { if (this != null) Close(); };
                    }
                    else
                    {
                        string msg = code == 5
                            ? "✗ Passphrase 錯誤 (或密文損壞). 再試一次, 或用「忘記 passphrase?」走手動貼上。"
                            : $"✗ 解密失敗 (exit={code}).\nstdout: {stdout.Trim()}\nstderr: {stderr.Trim()}";
                        SetStatus(msg, MessageType.Error);
                    }
                }
            }
            catch (Exception e)
            {
                SetStatus($"✗ 例外: {e.Message}", MessageType.Error);
            }
            finally
            {
                m_Working = false;
                Repaint();
            }
        }

        void SetStatus(string msg, MessageType t)
        {
            m_StatusMsg = msg;
            m_StatusType = t;
        }

        // ===========================================================
        // Helpers — 路徑解析
        // ===========================================================

        // 區塊職責：定位 UCL_Core/Tools~/AgentCommands/ucl_secret.py (對齊 LoginStatusPage 解析法)
        static string UclSecretPyPath()
        {
            string corePathRel = UCL_EditorPath.CorePath;
            if (string.IsNullOrEmpty(corePathRel)) return null;
            string corePath = Path.GetFullPath(Path.Combine(UCL_RepoPath.UnityProjectRoot, corePathRel));
            return Path.Combine(corePath, "Tools~", "AgentCommands", "ucl_secret.py");
        }

        static string ResolvePython()
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
            return "python";  // fallback: 讓 OS 自己找 (PATH 解析失敗時)
        }
    }
}
#endif
