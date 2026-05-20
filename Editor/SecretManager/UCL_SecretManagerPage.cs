// 區塊職責：加密檔 (secret) 集中管理 Page — 列 _secrets/*.enc + metadata + per-row 操作按鈕
// 物理意義：取代「散落的 install window + 要記 CLI 才查 secret 狀態」。掃描走 UCL_SecretScanner
//          (ucl_secret.py list --json, passphrase-free)，操作重用 UCL_SecretInstallWindow + reveal。
// 數值影響：UI 顯示純 read；Decrypt 開既有 install window；Rotate 印 CLI 指令 (互動 passphrase 不適合 IMGUI)。
// 設計取捨：參考 UCL_LoginStatusPage 範式 (掃檔 + 表格 + per-row 按鈕 + ScreenStreamGuard)。

#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UCL.Core.EditorLib;
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
        string m_StatusMsg = "";
        MessageType m_StatusType = MessageType.None;
        bool m_Loaded = false;

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
        }

        void Reload()
        {
            m_Secrets = UCL_SecretScanner.Scan(m_SecretsDir);
            m_Loaded = true;
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
                GUILayout.Label($"掃描資料夾: {m_SecretsDir}  (passphrase-free 讀 .enc metadata)", WrapLabel);
                GUILayout.Label("🔒=明文缺(待安裝) / ✅=明文已在 / ⚠=解析失敗。忘記密碼？用「📂開資料夾」手動貼明文。", WrapLabel);
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
                    GUILayout.Label($"(在 {m_SecretsDir} 下找不到 .enc — 用 ucl_secret.py encrypt 建立)", WrapLabel);
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
                string repoRoot = UCL_RepoPath.RepoRoot;
                string plainAbs = Path.Combine(repoRoot, s.PlainPath);
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
            // rotate 需互動兩次 passphrase, 不適合 IMGUI — 印 CLI 指令 + 複製到剪貼簿
            string cmd = $"python <UCL_Core>/Tools~/AgentCommands/ucl_secret.py rotate {s.EncPath} --hint \"新提示\"";
            EditorGUIUtility.systemCopyBuffer = cmd;
            SetStatus($"rotate 需在終端機互動輸入舊/新 passphrase。指令已複製到剪貼簿:\n  {cmd}", MessageType.Info);
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
