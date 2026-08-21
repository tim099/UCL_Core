// 區塊職責：加密檔 (secret) 集中管理 Page — 列 _secrets/*.enc + metadata + per-row 操作按鈕
// 物理意義：取代「散落的 install window + 要記 CLI 才查 secret 狀態」。掃描走 UCL_SecretScanner
//          （C# native 掃檔 + UCL_SecretCrypto.ReadMetadata，passphrase-free），操作重用 UCL_SecretInstallWindow + reveal。
// 數值影響：UI 顯示純 read；Decrypt 開既有 install window；Rotate 印 CLI 指令 (互動 passphrase 不適合 IMGUI)。
// 設計取捨：參考 UCL_LoginStatusPage 範式 (掃檔 + 表格 + per-row 按鈕 + ScreenStreamGuard)。

#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UCL.Core.EditorLib;
using UCL.Core.EditorLib.AgentCommands;
using UCL.Core.EditorLib.Page;
using UCL.Core.UI;
using UnityEditor;
using UnityEngine;

namespace UCL.Core.EditorLib.SecretManager
{
    /// <summary>
    /// 加密檔管理 Page。列出 consumer project 的 _secrets/*.enc，顯示 label/hint/created/格式版本/明文是否存在，
    /// 每列提供：開資料夾 (路徑B手動貼上) / 解密安裝 / 顯示提示 / rotate 指令。
    /// </summary>
    [HelpURL("ucl_core:Docs~/{lang}/Workflows/Secret_Manager_Workflow.md")]
    public class UCL_SecretManagerPage : UCL_CommonEditorPage
    {
        public override string WindowName => "Secret Manager";
        public override bool ShowInPageMenu => true;
        public override string SensitiveContentReason => "Contains secret install information (passphrase / plaintext path)";

        public static UCL_SecretManagerPage Create() => UCL_EditorPage.Create<UCL_SecretManagerPage>();

        // 區塊職責：掃描結果快取 + UI 狀態
        List<UCL_SecretInfo> m_Secrets = new List<UCL_SecretInfo>();
        string m_SecretsDir = UCL_SecretScanner.DefaultSecretsDir;
        // 編輯中的資料夾名（跟已生效的分開存 —— 相同才代表已套用，否則「改了沒按」跟「按了沒生效」分不出來）
        string m_SecretsDirEdit = UCL_SecretsPath.DirName;
        string m_StatusMsg = "";
        MessageType m_StatusType = MessageType.None;
        bool m_Loaded = false;

        // ==== 明文加密面板狀態（Tim 2026-07-22 — C# native 加密，SecretManager 新增「從明文加密」）====
        // 物理意義：列 _secrets 下的 .txt 明文供選、填 passphrase/hint/label → UCL_SecretCrypto.Encrypt 產出同名 .enc。
        List<string> m_PlainTxtAbs = new List<string>();   // _secrets 下 .txt 絕對路徑清單（加密來源）
        List<string> m_PlainTxtDisp = new List<string>();  // 對應顯示名（檔名）
        int m_EncSourceIdx = 0;
        string m_EncPass = "";
        string m_EncPassConfirm = "";
        string m_EncHint = "";
        string m_EncLabel = "";
        UCL_ObjectDictionary m_Dic = new();
        GUIStyle m_WrapLabel;
        GUIStyle WrapLabel => m_WrapLabel ??= new GUIStyle(UCL_GUIStyle.LabelStyle) { wordWrap = true };

        public override void Init(UCL_GUIPageController p_Controller)
        {
            base.Init(p_Controller);
            Reload();
        }

        protected override void TopBarButtons()
        {
            base.TopBarButtons();
            if (GUILayout.Button("Refresh", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                Reload();
            }
            // 開 _secrets 資料夾（Tim 2026-07-22）— 走 canonical DataRoot 解析，缺目錄先建
            if (GUILayout.Button("📂 開啟 _secrets", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                OpenSecretsFolder();
            }
        }

        void Reload()
        {
            m_Secrets = UCL_SecretScanner.Scan(m_SecretsDir);
            RefreshPlainTxtList();
            m_Loaded = true;
        }

        // 列舉 _secrets 下的 .txt 明文檔（加密來源下拉）— DataRoot 解析
        void RefreshPlainTxtList()
        {
            m_PlainTxtAbs.Clear();
            m_PlainTxtDisp.Clear();
            m_EncSourceIdx = 0;
            try
            {
                string root = UCL_AgentCommandsPath.ResolveData(m_SecretsDir);
                if (!Directory.Exists(root)) return;
                foreach (var f in Directory.GetFiles(root, "*.txt", SearchOption.AllDirectories))
                {
                    m_PlainTxtAbs.Add(f);
                    m_PlainTxtDisp.Add(Path.GetFileName(f));
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[SecretManager] 列舉 .txt 失敗: {e.Message}");
            }
        }

        // 開 _secrets 資料夾（DataRoot 解析，submodule/搬遷 aware）；缺目錄先建再開
        void OpenSecretsFolder()
        {
            try
            {
                string dir = UCL_AgentCommandsPath.ResolveData(m_SecretsDir);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                EditorUtility.RevealInFinder(dir);
                SetStatus($"已開資料夾: {dir}", MessageType.Info);
            }
            catch (System.Exception e)
            {
                SetStatus($"開資料夾失敗: {e.Message}", MessageType.Error);
            }
        }

        protected override void ContentOnGUI()
        {
            // 敏感內容守門 (對齊 LoginStatusPage)
            if (UCL_ScreenStreamGuard.GuardPage(nameof(UCL_SecretManagerPage), SensitiveContentReason))
            {
                return;
            }

            DrawHeader();
            GUILayout.Space(8);
            DrawTable();
            GUILayout.Space(8);
            DrawEncryptPanel();
            GUILayout.Space(8);
            if (!string.IsNullOrEmpty(m_StatusMsg))
            {
                EditorGUILayout.HelpBox(m_StatusMsg, m_StatusType);
            }
        }

        void DrawHeader()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                var title = new GUIStyle(UCL_GUIStyle.LabelStyle)
                {
                    fontSize = 16,
                    fontStyle = FontStyle.Bold,
                };
                GUILayout.Label("🔐 Secret Manager — 加密檔管理", title);
                // 顯示解析後的絕對掃描路徑（走 DataRoot，submodule / override aware）— 比只印相對字串更好 debug
                GUILayout.Label($"掃描資料夾: {UCL_AgentCommandsPath.ResolveData(m_SecretsDir)}  (passphrase-free 讀 .enc metadata)", WrapLabel);
                GUILayout.Label("🔒=明文缺(待安裝) / ✅=明文已在 / ⚠=解析失敗。忘記密碼？用「📂開資料夾」手動貼明文。", WrapLabel);

                DrawSecretsDirRow();
            }
        }


        // 區塊職責：secrets 資料夾名稱（相對 DataRoot）的設定列。
        // 物理意義：Tim 2026-08-21：「路徑改為非硬編碼，相對路徑寫檔，Page 上可以改，預設 Secret」。
        //          真相源是 `UCL_SecretsPath`（設定檔 `secrets_config.json`），**C# 與 python 共讀同一份**。
        // 數值影響：
        //   · 改名**不搬檔** —— 這一欄只換「去哪裡找」，資料夾要自己搬（或先搬再改）。
        //     ⚠ 所以改完若掃不到東西，那不是壞掉，是指到了一個空的／不存在的位置。
        //   · 存檔後清 `UCL_SecretsPath` 快取並重掃，畫面上的掃描路徑立刻反映新值 ——
        //     「設定寫了但畫面沒變」會讓人以為沒生效。
        //   · **只收相對名**：絕對路徑由 `UCL_SecretsPath.Save` 擋下（DataRoot override 才是換位置的入口，
        //     這裡再開一條會變成兩個地方決定同一件事）。
        void DrawSecretsDirRow()
        {
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label("資料夾名稱 (相對 DataRoot)", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                m_SecretsDirEdit = EditorGUILayout.TextField(m_SecretsDirEdit);
                bool aChanged = m_SecretsDirEdit != UCL_SecretsPath.DirName;
                using (new EditorGUI.DisabledScope(!aChanged || string.IsNullOrWhiteSpace(m_SecretsDirEdit)))
                {
                    if (GUILayout.Button("💾 套用", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        ApplySecretsDir();
                    }
                }
                if (GUILayout.Button("↩", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    m_SecretsDirEdit = UCL_SecretsPath.DirName;
                    GUI.FocusControl(null);
                }
            }
            GUILayout.Label($"設定檔: {UCL_SecretsPath.ConfigPath}"
                + $"（缺席時預設 `{UCL_SecretsPath.DefaultDirName}`；此設定入版控，全機器一致）", WrapLabel);
        }

        void ApplySecretsDir()
        {
            try
            {
                UCL_SecretsPath.Save(m_SecretsDirEdit);
                m_SecretsDir = UCL_SecretScanner.DefaultSecretsDir;   // property → 讀到新值
                m_SecretsDirEdit = UCL_SecretsPath.DirName;
                Reload();
                SetStatus($"✓ 已套用資料夾名稱：{UCL_SecretsPath.DirName}"
                    + "（**沒有搬任何檔** —— 掃不到東西代表指到了空位置）", MessageType.Info);
            }
            catch (System.Exception e)
            {
                SetStatus($"✗ 套用失敗: {e.Message}", MessageType.Error);
            }
            GUI.FocusControl(null);
        }

        // ===========================================================
        // 區塊職責：明文加密面板（Tim 2026-07-22 — SecretManager 新增「從明文加密」，C# native）
        // 物理意義：選 _secrets 下的 .txt 明文 → 填 passphrase(+確認)/hint/label → UCL_SecretCrypto.Encrypt
        //          產出同名 .enc（AES-256-CBC+HMAC+PBKDF2，零 python/插件）。舊 python .enc 也靠這重建。
        // 數值影響：passphrase 設計上無法反推（PBKDF2 200k）；hint 只是提示、不參與 KDF；產出即 commit-able。
        // ===========================================================
        void DrawEncryptPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label("🔐 明文加密（產出 .enc）— C# native，不需 python / 插件",
                                new GUIStyle(UCL_GUIStyle.LabelStyle) { fontStyle = FontStyle.Bold });

                if (m_PlainTxtAbs.Count == 0)
                {
                    GUILayout.Label("(_secrets 下沒有 .txt 明文可加密。先用上方「📂 開啟 _secrets」放入明文檔，再按 Refresh。)", WrapLabel);
                    return;
                }

                int idx = Mathf.Clamp(m_EncSourceIdx, 0, m_PlainTxtDisp.Count - 1);

                using(new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("來源明文 (.txt)", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    m_EncSourceIdx = UCL_GUILayout.PopupAuto(m_EncSourceIdx, m_PlainTxtDisp, m_Dic, nameof(m_EncSourceIdx));
                }

                m_EncPass = EditorGUILayout.PasswordField("Passphrase", m_EncPass);
                m_EncPassConfirm = EditorGUILayout.PasswordField("再次確認", m_EncPassConfirm);
                m_EncHint = EditorGUILayout.TextField("提示 (hint, 選填)", m_EncHint);
                m_EncLabel = EditorGUILayout.TextField("標籤 (label, 選填)", m_EncLabel);

                bool passOk = !string.IsNullOrEmpty(m_EncPass);
                bool matchOk = m_EncPass == m_EncPassConfirm;
                if (passOk && !matchOk)
                    GUILayout.Label("<color=#ff8866>⚠ 兩次 passphrase 不一致</color>", WrapLabel);

                using (new EditorGUI.DisabledScope(!(passOk && matchOk)))
                {
                    if (GUILayout.Button("🔐 加密產出 .enc", UCL_GUIStyle.GetButtonStyle(new Color(0.5f, 1f, 0.5f)), GUILayout.ExpandWidth(false)))
                    {
                        DoEncrypt(Mathf.Clamp(m_EncSourceIdx, 0, m_PlainTxtAbs.Count - 1));
                    }
                }
                GUILayout.Label("提示：passphrase 請自己記牢——設計上無法反推（PBKDF2 200k + AES-256）；hint 只是喚回提示、不是密碼。產出的 .enc 可 commit（明文 .txt 保持 gitignored）。", WrapLabel);
            }
        }

        // 加密選定的 .txt → 同名 .enc（C# native）
        void DoEncrypt(int sourceIdx)
        {
            try
            {
                if (sourceIdx < 0 || sourceIdx >= m_PlainTxtAbs.Count) { SetStatus("來源無效", MessageType.Error); return; }
                string txtAbs = m_PlainTxtAbs[sourceIdx];
                if (!File.Exists(txtAbs)) { SetStatus($"明文檔不存在: {txtAbs}", MessageType.Error); return; }

                byte[] plain = File.ReadAllBytes(txtAbs);
                byte[] enc = UCL_SecretCrypto.Encrypt(plain, m_EncPass, m_EncHint ?? "", m_EncLabel ?? "");

                string encAbs = txtAbs.EndsWith(".txt", System.StringComparison.OrdinalIgnoreCase)
                    ? txtAbs.Substring(0, txtAbs.Length - 4) + ".enc"
                    : txtAbs + ".enc";
                File.WriteAllBytes(encAbs, enc);

                SetStatus($"✓ 已加密產出: {Path.GetFileName(encAbs)}（{enc.Length} bytes, UCLS1）。可 commit 此 .enc。", MessageType.Info);
                // 清 passphrase（不殘留）+ 重掃反映新 .enc metadata
                m_EncPass = ""; m_EncPassConfirm = "";
                Reload();
            }
            catch (System.ArgumentException ae)
            {
                SetStatus($"✗ 加密參數錯誤: {ae.Message}", MessageType.Error);
            }
            catch (System.Exception e)
            {
                SetStatus($"✗ 加密失敗: {e.Message}", MessageType.Error);
            }
        }

        void DrawTable()
        {
            GUILayout.Label($"Secrets ({m_Secrets.Count})", UCL_GUIStyle.LabelStyle);
            using (new GUILayout.VerticalScope("box"))
            {
                if (!m_Loaded)
                {
                    GUILayout.Label("(尚未掃描)", UCL_GUIStyle.LabelStyle);
                    return;
                }
                if (m_Secrets.Count == 0)
                {
                    GUILayout.Label($"(在 {m_SecretsDir} 下找不到 .enc — 用下方「🔐 明文加密」面板建立)", WrapLabel);
                    return;
                }

                // header row
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("狀態", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(60)));
                    GUILayout.Label("Label / 檔名", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(200)));
                    GUILayout.Label("提示 (hint)", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(200)));
                    GUILayout.Label("Ver", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(40)));
                    GUILayout.Label("操作", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(true));
                }

                foreach (var s in m_Secrets)
                {
                    DrawRow(s);
                }
            }
        }

        void DrawRow(UCL_SecretInfo s)
        {
            using (new GUILayout.HorizontalScope())
            {
                // 狀態 icon
                string icon = !string.IsNullOrEmpty(s.Error) ? "⚠"
                            : (s.PlainExists ? "<color=#66ff99>✅</color>" : "🔒");
                GUILayout.Label(icon, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(60)));

                // Label (空則用檔名)
                string display = string.IsNullOrEmpty(s.Label) ? Path.GetFileName(s.EncPath) : s.Label;
                GUILayout.Label(display, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(200)));

                // hint
                string hintDisp = !string.IsNullOrEmpty(s.Error) ? $"err: {s.Error}"
                                : (s.FormatVersion <= 1 ? "(TKN1 無提示)"
                                   : (string.IsNullOrEmpty(s.Hint) ? "(無提示)" : s.Hint));
                GUILayout.Label(Trunc(hintDisp, 28), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(200)));

                // 格式版本
                GUILayout.Label(s.FormatVersion > 0 ? $"v{s.FormatVersion}" : "?",
                                UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(40)));

                // 操作按鈕列
                if (GUILayout.Button("📂", UCL_GUIStyle.ButtonStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(34))))
                {
                    OpenFolder(s);
                }
                using (new EditorGUI.DisabledScope(!string.IsNullOrEmpty(s.Error)))
                {
                    if (GUILayout.Button("🔓 解密", UCL_GUIStyle.ButtonStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(70))))
                    {
                        OpenInstall(s);
                    }
                    if (GUILayout.Button("💡 提示", UCL_GUIStyle.ButtonStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(70))))
                    {
                        ShowHint(s);
                    }
                    if (GUILayout.Button("🔁 rotate", UCL_GUIStyle.ButtonStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80))))
                    {
                        ShowRotateCmd(s);
                    }
                }
            }
        }

        // ===========================================================
        // Row actions
        // ===========================================================

        void OpenFolder(UCL_SecretInfo s)
        {
            try
            {
                // 走 canonical DataRoot 解析（2026-07-22 basecamp）：與 Scanner 同源，AgentCommands 前綴
                // 映射到可 override 的資料根（submodule / 資料搬遷 aware）；預設模式 = RepoRoot/AgentCommands/...
                string plainAbs = UCL_AgentCommandsPath.ResolveData(s.PlainPath);
                string folder = Path.GetDirectoryName(plainAbs);
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
                EditorUtility.RevealInFinder(plainAbs);
                SetStatus($"已開資料夾。忘記密碼時把原始 token 存成 {Path.GetFileName(s.PlainPath)} (純文字單行) 即可。",
                          MessageType.Info);
            }
            catch (System.Exception e)
            {
                SetStatus($"開資料夾失敗: {e.Message}", MessageType.Error);
            }
        }

        void OpenInstall(UCL_SecretInfo s)
        {
            var entry = new UCL_SecretEntry
            {
                PlainPath = s.PlainPath,
                EncPath = s.EncPath,
                Label = string.IsNullOrEmpty(s.Label) ? Path.GetFileName(s.EncPath) : s.Label,
                OnInstalled = Reload,   // 解密完成後刷新表格
            };
            UCL_SecretInstallWindow.ShowFor(entry);
        }

        void ShowHint(UCL_SecretInfo s)
        {
            string hint = s.FormatVersion <= 1 ? "(TKN1 格式無提示, rotate 一次可補)"
                        : (string.IsNullOrEmpty(s.Hint) ? "(此 secret 沒設提示)" : s.Hint);
            string created = string.IsNullOrEmpty(s.CreatedAt) ? "unknown" : s.CreatedAt;
            SetStatus($"💡 {Path.GetFileName(s.EncPath)}\n  提示: {hint}\n  建立: {created}  格式: TKN{s.FormatVersion}",
                      MessageType.Info);
        }

        void ShowRotateCmd(UCL_SecretInfo s)
        {
            // 全切 C# 後 rotate = 換 passphrase 重加密：① 用「🔓 解密」把明文落地 .txt →
            // ② 下方「🔐 明文加密」對該 .txt 填新 passphrase 重產 .enc（覆蓋舊的）。無需 python / CLI。
            SetStatus(
                $"換 passphrase（rotate）步驟：\n"
                + $"  1. 先按「🔓 解密」把 {Path.GetFileName(s.PlainPath)} 明文還原到本機\n"
                + $"  2. 到下方「🔐 明文加密」選該 .txt、填新 passphrase → 加密產出 .enc（覆蓋舊檔）\n"
                + "全程 C# native，不需 python / 插件。", MessageType.Info);
        }

        void SetStatus(string msg, MessageType t)
        {
            m_StatusMsg = msg;
            m_StatusType = t;
        }

        static string Trunc(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length > max ? s.Substring(0, max) + "…" : s;
        }
    }
}
#endif
