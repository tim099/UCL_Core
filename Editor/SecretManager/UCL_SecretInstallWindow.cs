// 區塊職責：通用 secret 解密安裝視窗 — 偵測 .enc 存在但本機明文缺時引導輸入 passphrase 解密落地
// 物理意義：抽 EOV 端 RCG_DiscordTokenInstallWindow 的硬編碼路徑為 UCL_SecretEntry 參數化，
//          跨機器同步時 .enc 跟 repo 走、明文 .txt 永遠 gitignored，新 clone 必須跑一次解密還原。
// 數值影響：解密與 hint 顯示都走 C# native（UCL_SecretCrypto.Decrypt / ReadMetadata，passphrase-free 讀 metadata）；
//          開資料夾走 EditorUtility.RevealInFinder。passphrase 只在記憶體，不進 argv 也不落檔。

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
            bool plainExists = File.Exists(Abs(entry.PlainPath));
            bool encExists = File.Exists(Abs(entry.EncPath));
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
            string plainPath = Abs(m_Entry.PlainPath);
            string encPath = Abs(m_Entry.EncPath);
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
                    "找不到 .enc 加密檔。請先在本機建明文，再用 Secret Manager 頁的『🔐 明文加密』功能加密產出 .enc（C# native，不需 python），然後 commit 該 .enc。", MessageType.Warning);
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
        // 開資料夾 — ⭐ 路徑 B 救援（連 hint 都救不回時，手動貼明文）
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
                "  加密用 PBKDF2 200k 輪 + AES-256-CBC + HMAC, 設計上無法 brute-force, 只能靠提示喚回。\n\n" +
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
        // 讀 hint metadata — passphrase-free（C# native UCL_SecretCrypto.ReadMetadata，2026-07-22 全切 C#）
        // ===========================================================
        void LoadMetadata()
        {
            m_Hint = "";
            m_MetaLabel = "";
            m_FormatVersion = 0;
            m_MetaLoaded = false;

            string encPath = Abs(m_Entry.EncPath);
            if (!File.Exists(encPath)) { m_MetaLoaded = true; return; }

            try
            {
                var meta = UCL_SecretCrypto.ReadMetadata(File.ReadAllBytes(encPath));
                m_Hint = meta.Hint;
                m_MetaLabel = meta.Label;
                m_FormatVersion = meta.FormatVersion;
            }
            catch (Exception)
            {
                // 非 UCLS1（舊 python TKN1/TKN2）或壞檔 → metadata 讀不到，維持空 + version 0（UI 會標舊格式）
                m_FormatVersion = 0;
            }
            m_MetaLoaded = true;
        }

        // ===========================================================
        // Decrypt action — C# native（UCL_SecretCrypto.Decrypt，無 python / 無 cryptography 套件）
        // ===========================================================
        void DoDecrypt(string root, string plainPath, string encPath)
        {
            m_Working = true;
            Repaint();
            try
            {
                byte[] enc = File.ReadAllBytes(encPath);
                byte[] plain;
                try
                {
                    plain = UCL_SecretCrypto.Decrypt(enc, m_Passphrase);
                }
                catch (System.Security.Cryptography.CryptographicException)
                {
                    // HMAC 驗失敗 = passphrase 錯或密文竄改
                    SetStatus("✗ Passphrase 錯誤（或密文損壞）。再試一次，或用「忘記 passphrase?」走手動貼上。", MessageType.Error);
                    return;
                }
                catch (FormatException fe)
                {
                    // 非 UCLS1（舊 python 格式）→ C# 解不了，引導重建
                    SetStatus($"✗ 此 .enc 非 C# native（UCLS1）格式：{fe.Message}\n舊 python 加密檔請用 SecretManagerPage 的『明文加密』對 .txt 重建。", MessageType.Error);
                    return;
                }

                // 確保目錄存在再寫明文
                string dir = Path.GetDirectoryName(plainPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllBytes(plainPath, plain);

                SetStatus($"✓ 解密成功，已寫入 {plainPath}\n  size={plain.Length} bytes", MessageType.Info);
                m_Passphrase = "";
                EditorPrefs.DeleteKey(DismissKey(m_Entry.EncPath));
                m_DontAskAgain = false;
                m_Entry.OnInstalled?.Invoke();
                EditorApplication.delayCall += () => { if (this != null) Close(); };
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

        // 區塊職責：repo-relative 路徑 → 絕對路徑（走 canonical DataRoot 解析，submodule/搬遷 aware）
        static string Abs(string repoRel) => UCL_AgentCommandsPath.ResolveData(repoRel);

        void SetStatus(string msg, MessageType t)
        {
            m_StatusMsg = msg;
            m_StatusType = t;
        }

        // ===========================================================
        // Helpers — 路徑解析
        // ===========================================================

    }
}
#endif
