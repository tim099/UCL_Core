// 區塊職責：Plurk 後台管理 Page —— 目前只做**帳號**（Tim 2026-08-21：「先處理帳號相關部分即可」）。
// 物理意義：兩件事在這一頁看得完：① 共用（公用）帳號是哪一份 secret ② 每個 persona 實際會用到哪一份。
//          解析邏輯**不在本頁**，在 `UCL_PlurkAccounts.Resolve`（單一解析點）——
//          頁面只負責顯示與寫入，判準留在解析器裡，兩邊才不會各說各話。
// 數值影響：
//   · **本頁不顯示、不讀取任何 token**。它只處理 secret **id**；憑證本體住 `UCL_SecretManagerPage`。
//   · 掃描與讀檔只在 `Reload()`（Init／重新整理／寫入後）—— **Draw 裡零 IO**
//     （IMGUI 的 Layout 與 Repaint 是兩個 pass，Draw 裡碰磁碟會讓兩趟看到不同的東西）。
//   · persona 寫入走 `UCL_PlurkAccounts.SetPersonaAccount` → `UCL_PersonaProfile.SetField`
//     （actor/reason 必填、有審計）。本頁不自己組 profile 路徑。
// 設計取捨：
//   · 體例參考 `UCL_SecretManagerPage`（掃檔 → 表格 → per-row 按鈕）與 `UCL_LLMModelAdminPage`
//     （TopBar 互跳別的頁）。**不另造第三種版面。**
//   · 入口走 `ShowInPageMenu`（頁面選單的下拉用反射掃得到本頁）——
//     Tim 2026-08-21：「目前 page 有下拉選單透過 reflection 可以選取」⇒ 不必動 `UCL_ToolBoxPage`
//     （它住 `UCL_Core` 組件，看不到 `Editor/` 這側，硬接會生出字串型別名那族的坑）。
// @doc-sync: Assets/Plugins/UCL_Core/Docs~/zh-Hant/UCL_EditorPage/UCL_PlurkAdminPage.md（本頁的操作與判準）
// @doc-sync: Assets/Plugins/UCL_Core/Docs~/zh-Hant/Workflows/Plurk_Maintenance.md（§4 帳號與憑證）
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UCL.Core.EditorLib;
using UCL.Core.EditorLib.AgentCommands;
using UCL.Core.EditorLib.Page;
using UCL.Core.EditorLib.SecretManager;
using UCL.Core.JsonLib;
using UCL.Core.UI;
using UnityEditor;
using UnityEngine;

namespace UCL.Core.EditorLib.Plurk
{
    /// <summary>
    /// Plurk 帳號管理：共用（公用）帳號指到哪一份 secret、每個 persona 用個人帳號還是共用。
    /// 憑證本體不在這裡 —— 走 TopBar 的「🔐 Secret 管理」。
    /// </summary>
    [HelpURL("ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_PlurkAdminPage.md")]
    public class UCL_PlurkAdminPage : UCL_CommonEditorPage
    {
        public override string WindowName => "Plurk 帳號";
        public override bool ShowInPageMenu => true;

        public static UCL_PlurkAdminPage Create() => UCL_EditorPage.Create<UCL_PlurkAdminPage>();

        const string NoneOption = "(未設定)";

        // 區塊職責：一列 = 一個 persona 的解析結果
        // 物理意義：`Resolution` 帶 `Source`，所以「個人／共用」不必另存欄位 —— 由它推導。
        class Row
        {
            public string Persona = "";
            public UCL_PlurkAccountResolution Res;
            public UCL_SecretInfo Secret;      // null ＝ 解析到的 id 沒有對應的 .enc
        }

        UCL_PlurkAccountsConfig m_Config = new UCL_PlurkAccountsConfig();
        string m_ConfigError = "";
        readonly List<string> m_SecretIds = new List<string>();
        readonly List<string> m_Options = new List<string>();   // [0] = NoneOption，其後為 secret id
        int m_SharedIdx = 0;
        readonly List<Row> m_Rows = new List<Row>();
        readonly UCL_ObjectDictionary m_Dic = new UCL_ObjectDictionary();
        string m_Status = "";
        MessageType m_StatusType = MessageType.None;

        // 區塊職責：憑證輸入欄（產出 .enc 用）。
        // 物理意義：**這些欄位裡的值是秘密** —— 產出成功後由 ClearCredFields 清空，
        //          不留在頁面狀態裡（頁面 instance 活著的期間都算殘留面）。
        // 數值影響：`m_CredId` / hint / label 不是秘密，刻意**不清**（重複建立時省事）。
        string m_CredId = "plurk_shared";
        string m_CredConsumerKey = "";
        string m_CredConsumerSecret = "";
        string m_CredAccessToken = "";
        string m_CredAccessSecret = "";
        string m_CredNote = "";
        string m_CredPass = "";
        string m_CredPassConfirm = "";
        string m_CredHint = "";
        string m_CredLabel = "";
        bool m_CredAllowOverwrite = false;

        GUIStyle m_WrapLabel;
        GUIStyle WrapLabel => m_WrapLabel ??= new GUIStyle(UCL_GUIStyle.LabelStyle) { wordWrap = true };
        GUIStyle m_MonoLabel;
        GUIStyle MonoLabel => m_MonoLabel ??= new GUIStyle(UCL_GUIStyle.LabelStyle) { richText = false };

        public override void Init(UCL_GUIPageController p_Controller)
        {
            base.Init(p_Controller);
            Reload();
        }

        // 區塊職責：所有 IO 都集中在這裡（Draw 不碰磁碟）
        void Reload()
        {
            m_ConfigError = "";
            try { m_Config = UCL_PlurkAccounts.Load() ?? new UCL_PlurkAccountsConfig(); }
            catch (Exception e)
            {
                // 「registry 壞了」與「還沒設共用帳號」必須可分辨 —— 所以壞掉要說出來。
                m_Config = new UCL_PlurkAccountsConfig();
                m_ConfigError = e.Message;
            }

            m_SecretIds.Clear();
            m_SecretIds.AddRange(UCL_PlurkAccounts.ListSecretIds());

            m_Options.Clear();
            m_Options.Add(NoneOption);
            m_Options.AddRange(m_SecretIds);

            string aShared = (m_Config.m_SharedSecretId ?? "").Trim();
            m_SharedIdx = string.IsNullOrEmpty(aShared) ? 0 : Mathf.Max(0, m_Options.IndexOf(aShared));

            m_Rows.Clear();
            var aPersonas = new List<string>(UCL_PersonaProfile.PoolNames());
            aPersonas.Sort(StringComparer.Ordinal);
            foreach (string aPersona in aPersonas)
            {
                var aRes = UCL_PlurkAccounts.Resolve(aPersona);
                m_Rows.Add(new Row
                {
                    Persona = aPersona,
                    Res = aRes,
                    Secret = aRes.Resolved ? UCL_PlurkAccounts.FindSecret(aRes.SecretId) : null,
                });
            }
        }

        protected override void TopBarButtons()
        {
            base.TopBarButtons();
            if (GUILayout.Button("🔄 重新整理", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                Reload();
                GUI.FocusControl(null);
            }
            // 設完帳號的下一個動作十之八九是去裝／查那份憑證 —— 兩頁互跳，不必繞回頁面選單
            if (GUILayout.Button("🔐 Secret 管理", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                UCL_SecretManagerPage.Create();
            }
        }

        protected override void ContentOnGUI()
        {
            GUILayout.Label("🐦 Plurk 帳號管理", WrapLabel);
            EditorGUILayout.HelpBox(
                "只分兩種：**共用（公用帳號）** 與 **個人**。\n"
                + "· 帳號憑證（consumer key/secret ＋ access token/secret）存在 Secret Manager，"
                + "本頁只處理「誰用哪一份」的 id 對應，不顯示也不讀取任何 token。\n"
                + "· persona 沒設個人帳號 ⇒ 自動回落共用。共用帳號發文**末行必須署名**"
                + "（Tim 2026-08-16 硬規則），因為時間軸上讀者只看得到帳號、看不到是誰寫的。",
                MessageType.Info);

            if (!string.IsNullOrEmpty(m_ConfigError))
            {
                EditorGUILayout.HelpBox(
                    $"⛔ 設定檔讀取失敗：{m_ConfigError}\n"
                    + $"路徑：{UCL_PlurkAccounts.RegistryPath()}\n"
                    + "**不會自動覆蓋壞檔** —— 覆蓋掉的是別人寫的設定，而那筆改動沒有地方留得住。",
                    MessageType.Error);
            }

            DrawShared();
            DrawCredentialPanel();
            DrawPersonas();
            DrawStatus();
        }

        // ===========================================================
        // 共用（公用）帳號
        // ===========================================================
        void DrawShared()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label("🤝 共用帳號（公用）", UCL_GUIStyle.LabelStyle);
                GUILayout.Label($"設定檔：{UCL_PlurkAccounts.RegistryPath()}", MonoLabel);

                // ⚠ 選項永遠 ≥ 1（第一項是「(未設定)」）—— PopupSearchCache 空清單會 LogError
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("secret id", UCL_GUIStyle.LabelStyle,GUILayout.ExpandWidth(false));
                    m_SharedIdx = UCL_GUILayout.PopupSearchCache(Mathf.Clamp(m_SharedIdx, 0, m_Options.Count - 1), m_Options, m_Dic, "SharedPicker");
                }

                if (m_SecretIds.Count == 0)
                {
                    EditorGUILayout.HelpBox(
                        $"`_secrets/` 底下沒有任何 `{UCL_PlurkAccounts.SecretPrefix}*.enc`。\n"
                        + $"先到「🔐 Secret 管理」把憑證加密成 `{UCL_PlurkAccounts.SecretPrefix}<名字>.enc`，"
                        + "本頁的清單來源就是那些檔案的檔名。",
                        MessageType.Warning);
                }

                DrawSecretState(SelectedSharedId());

                using (new GUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("💾 存檔", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        SaveShared();
                    }
                    if (GUILayout.Button("↩ 放棄改動", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        Reload();
                        GUI.FocusControl(null);
                    }
                    GUILayout.FlexibleSpace();
                }
            }
        }

        string SelectedSharedId()
        {
            if (m_SharedIdx <= 0 || m_SharedIdx >= m_Options.Count) return "";
            return m_Options[m_SharedIdx];
        }

        void SaveShared()
        {
            try
            {
                m_Config.m_SharedSecretId = SelectedSharedId();
                UCL_PlurkAccounts.Save(m_Config);
                SetStatus($"✅ 已寫入共用帳號：{(string.IsNullOrEmpty(m_Config.m_SharedSecretId) ? "(未設定)" : m_Config.m_SharedSecretId)}",
                    MessageType.Info);
                Reload();
            }
            catch (Exception e) { SetStatus($"⛔ 寫入失敗：{e.Message}", MessageType.Error); }
            GUI.FocusControl(null);
        }

        // 區塊職責：一份 secret 的可用狀態。
        // 物理意義：**「有 .enc」跟「明文已安裝」是兩件事** —— 只有後者才真的能發文，
        //          而前者存在時看起來已經好了。所以兩個狀態分開講，不合併成一個綠燈。
        void DrawSecretState(string iSecretId)
        {
            if (string.IsNullOrEmpty(iSecretId))
            {
                GUILayout.Label("（未指定 secret —— 所有沒設個人帳號的 persona 都會解析成 unset）", WrapLabel);
                return;
            }
            var aInfo = UCL_PlurkAccounts.FindSecret(iSecretId);
            if (aInfo == null)
            {
                GUILayout.Label($"⛔ 找不到 `{iSecretId}.enc` —— 這個 id 指向一份不存在的憑證", WrapLabel);
                return;
            }
            string aLabel = string.IsNullOrEmpty(aInfo.Label) ? "(無 label)" : aInfo.Label;
            GUILayout.Label($"　.enc：✅ 有　｜　明文：{(aInfo.PlainExists ? "✅ 已安裝" : "⚠ 未安裝（到 Secret 管理解密）")}"
                + $"　｜　label：{aLabel}", WrapLabel);
            if (!string.IsNullOrEmpty(aInfo.Error))
            {
                GUILayout.Label($"　⚠ metadata 讀取異常：{aInfo.Error}", WrapLabel);
            }
        }

        // ===========================================================
        // 產生憑證（填欄位 → 直接產出 .enc）
        // ===========================================================
        // 區塊職責：把「手動編明文 JSON → 存檔 → 去 Secret Manager 選檔加密」收成一個面板。
        // 物理意義：流程沿用 `UCL_SecretManagerPage.DoEncrypt`（`UCL_SecretCrypto.Encrypt`，
        //          C# native、PBKDF2 200k + AES-256），**不另造第二套加密**。
        // 數值影響（兩處刻意比原版嚴）：
        //   ① **明文不落地**：JSON 在記憶體組好直接加密，`_secrets/*.txt` 全程不產生。
        //      原版流程是「先有 .txt 再加密」，那份 .txt 會一直留在磁碟上；
        //      這裡少一份殘留就少一個外洩面（明文雖 gitignored，但 gitignored ≠ 不存在）。
        //      ⚠ 之後要真的發文仍需明文 —— 那由 Secret Manager 的「解密安裝」產生，
        //        時機由人決定，而不是在建立憑證的這一刻就攤在磁碟上。
        //   ② **覆蓋要顯式勾選**：`.enc` 已存在時按鈕停用，勾了「允許覆蓋」才放行。
        //      覆蓋掉的是一份**再也拿不回來**的憑證（passphrase 不可反推），
        //      而覆蓋成功跟第一次建立**看起來一模一樣**。
        //   · 成功後四個憑證欄與 passphrase 全部清空 —— 不在頁面狀態裡留 token。
        //   · **全程不 log 任何值**（Debug 只印檔名與長度）。
        void DrawCredentialPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label("🔑 產生憑證（填欄位 → 直接產出 .enc）", UCL_GUIStyle.LabelStyle);
                GUILayout.Label("OAuth 1.0a 四個值：前兩個認 app、後兩個認「以哪個帳號發文」。"
                    + "四欄到齊才發得出去 —— 只有 consumer key/secret 是不夠的。", WrapLabel);

                m_CredId = EditorGUILayout.TextField("secret id", m_CredId);
                if (!string.IsNullOrEmpty(m_CredId)
                    && !m_CredId.StartsWith(UCL_PlurkAccounts.SecretPrefix, StringComparison.Ordinal))
                {
                    // 前綴不是裝飾：本頁的帳號清單就是靠它篩出來的，命名錯了不會報錯，
                    // 只會「產出了 .enc 但下拉選單裡看不到」。
                    GUILayout.Label($"⚠ id 必須以 `{UCL_PlurkAccounts.SecretPrefix}` 開頭，"
                        + "否則本頁的帳號清單掃不到它（產出成功但選單裡沒有）。", WrapLabel);
                }

                m_CredConsumerKey = EditorGUILayout.TextField("consumer key", m_CredConsumerKey);
                m_CredConsumerSecret = EditorGUILayout.PasswordField("consumer secret", m_CredConsumerSecret);
                m_CredAccessToken = EditorGUILayout.TextField("access token", m_CredAccessToken);
                m_CredAccessSecret = EditorGUILayout.PasswordField("access token secret", m_CredAccessSecret);
                m_CredNote = EditorGUILayout.TextField("備註 (note, 選填)", m_CredNote);

                GUILayout.Space(4);
                m_CredPass = EditorGUILayout.PasswordField("Passphrase", m_CredPass);
                m_CredPassConfirm = EditorGUILayout.PasswordField("再次確認", m_CredPassConfirm);
                m_CredHint = EditorGUILayout.TextField("提示 (hint, 選填)", m_CredHint);
                m_CredLabel = EditorGUILayout.TextField("標籤 (label, 選填)", m_CredLabel);

                bool aIdOk = !string.IsNullOrWhiteSpace(m_CredId)
                    && m_CredId.StartsWith(UCL_PlurkAccounts.SecretPrefix, StringComparison.Ordinal);
                bool aFieldsOk = !string.IsNullOrWhiteSpace(m_CredConsumerKey)
                    && !string.IsNullOrWhiteSpace(m_CredConsumerSecret)
                    && !string.IsNullOrWhiteSpace(m_CredAccessToken)
                    && !string.IsNullOrWhiteSpace(m_CredAccessSecret);
                bool aPassOk = !string.IsNullOrEmpty(m_CredPass);
                bool aMatchOk = m_CredPass == m_CredPassConfirm;

                if (aPassOk && !aMatchOk) GUILayout.Label("⚠ 兩次 passphrase 不一致", WrapLabel);
                if (!aFieldsOk) GUILayout.Label("⚠ 四個憑證欄都要填 —— 缺 access token 的症狀是"
                    + "「看起來設好了但發不出去」，而那時分不出缺的是哪一半。", WrapLabel);

                string aEncPath = CredEncPath();
                bool aExists = !string.IsNullOrEmpty(aEncPath) && File.Exists(aEncPath);
                if (aExists)
                {
                    GUILayout.Label($"⚠ `{Path.GetFileName(aEncPath)}` 已存在。覆蓋掉的憑證"
                        + "**拿不回來**（passphrase 不可反推），而覆蓋成功跟第一次建立看起來一樣。", WrapLabel);
                    using (new GUILayout.HorizontalScope())
                    {
                        m_CredAllowOverwrite = UCL_GUILayout.CheckBox(m_CredAllowOverwrite);
                        GUILayout.Label("我確定要覆蓋它", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                        GUILayout.FlexibleSpace();
                    }
                }

                bool aCanGo = aIdOk && aFieldsOk && aPassOk && aMatchOk && (!aExists || m_CredAllowOverwrite);
                using (new EditorGUI.DisabledScope(!aCanGo))
                {
                    if (GUILayout.Button("🔐 產出 .enc（明文不落地）",
                            UCL_GUIStyle.GetButtonStyle(new Color(0.5f, 1f, 0.5f)),
                            GUILayout.ExpandWidth(false)))
                    {
                        GenerateSecret();
                    }
                }
                GUILayout.Label("產出的 `.enc` 可 commit。**但它還不能用** —— 要到「🔐 Secret 管理」"
                    + "做一次解密安裝產生明文，本頁的 token 狀態才會從「未安裝」變成已安裝。", WrapLabel);
            }
        }

        string CredEncPath()
        {
            string aId = (m_CredId ?? "").Trim();
            if (string.IsNullOrEmpty(aId)) return "";
            return Path.Combine(UCL_SecretsPath.AbsoluteDir, aId + ".enc").Replace('\\', '/');
        }

        // 區塊職責：把四個欄位組成契約 JSON → 加密 → 落 .enc。
        // 物理意義：JSON **用 JsonData 組不用字串串接** —— 憑證裡若含引號或反斜線，
        //          串接會產出壞掉的 JSON，而那是「寫成功了但讀不回來」那一族
        //          （今天已經被跳脫層咬過五次，這裡不留第六次的位置）。
        void GenerateSecret()
        {
            try
            {
                var aJson = new JsonData();
                aJson["account"] = new JsonData((m_CredId ?? "").Trim());
                aJson["note"] = new JsonData(m_CredNote ?? "");
                aJson["consumer_key"] = new JsonData((m_CredConsumerKey ?? "").Trim());
                aJson["consumer_secret"] = new JsonData((m_CredConsumerSecret ?? "").Trim());
                aJson["access_token"] = new JsonData((m_CredAccessToken ?? "").Trim());
                aJson["access_token_secret"] = new JsonData((m_CredAccessSecret ?? "").Trim());

                byte[] aPlain = new System.Text.UTF8Encoding(false).GetBytes(aJson.ToJsonBeautify());
                byte[] aEnc = UCL_SecretCrypto.Encrypt(aPlain, m_CredPass,
                    m_CredHint ?? "", m_CredLabel ?? "");

                string aPath = CredEncPath();
                string aDir = Path.GetDirectoryName(aPath);
                if (!string.IsNullOrEmpty(aDir) && !Directory.Exists(aDir)) Directory.CreateDirectory(aDir);
                File.WriteAllBytes(aPath, aEnc);

                // ⚠ 只報檔名與長度，**不報任何值**
                SetStatus($"✅ 已產出 {Path.GetFileName(aPath)}（{aEnc.Length} bytes）。"
                    + "明文全程沒有落地；要真的能用還需到 Secret 管理做一次解密安裝。", MessageType.Info);
                ClearCredFields();
                Reload();
            }
            catch (ArgumentException ae) { SetStatus($"⛔ 加密參數錯誤：{ae.Message}", MessageType.Error); }
            catch (Exception e) { SetStatus($"⛔ 產出失敗：{e.Message}", MessageType.Error); }
            GUI.FocusControl(null);
        }

        // 成功後不在頁面狀態裡留任何憑證 —— id/hint/label 留著（那些不是秘密，重複建立時省事）
        void ClearCredFields()
        {
            m_CredConsumerKey = "";
            m_CredConsumerSecret = "";
            m_CredAccessToken = "";
            m_CredAccessSecret = "";
            m_CredPass = "";
            m_CredPassConfirm = "";
            m_CredAllowOverwrite = false;
        }

        // ===========================================================
        // persona 對照（個人帳號 override）
        // ===========================================================
        void DrawPersonas()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label($"🧑 persona 對照（{m_Rows.Count} 位）", UCL_GUIStyle.LabelStyle);
                GUILayout.Label("個人帳號寫在該 persona 的 profile（欄名 "
                    + UCL_PlurkAccounts.PersonaField
                    + "），走 PersonaProfile 寫入通道（actor/reason 必填、有審計）。"
                    + "設成「(未設定)」＝清掉 override、回落共用。", WrapLabel);

                foreach (var aRow in m_Rows) DrawPersonaRow(aRow);
            }
        }

        void DrawPersonaRow(Row iRow)
        {
            using (new GUILayout.HorizontalScope("box"))
            {
                GUILayout.Label(iRow.Persona, UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));

                string aKind = iRow.Res.Source == UCL_PlurkAccounts.SourcePersona ? "個人"
                    : iRow.Res.Source == UCL_PlurkAccounts.SourceShared ? "共用" : "—";
                GUILayout.Label(aKind, UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));

                // 解析結果一律連 Source 一起顯示 —— 「用哪個」跟「憑什麼」要一起看得到
                GUILayout.Label(iRow.Res.Describe(), WrapLabel, GUILayout.ExpandWidth(false));

                string aToken = !iRow.Res.Resolved ? ""
                    : iRow.Secret == null ? "⛔ 無 .enc"
                    : iRow.Secret.PlainExists ? "✅" : "⚠ 未安裝";
                GUILayout.Label(aToken, UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));

                int aCur = iRow.Res.Source == UCL_PlurkAccounts.SourcePersona
                    ? Mathf.Max(0, m_Options.IndexOf(iRow.Res.SecretId)) : 0;
                int aNext = UCL_GUILayout.PopupSearchCache(Mathf.Clamp(aCur, 0, m_Options.Count - 1), m_Options, m_Dic, "P_" + iRow.Persona);
                if (aNext != aCur)
                {
                    string aId = (aNext <= 0 || aNext >= m_Options.Count) ? "" : m_Options[aNext];
                    SetPersona(iRow.Persona, aId);
                }
            }
        }

        void SetPersona(string iPersona, string iSecretId)
        {
            string aReason = string.IsNullOrEmpty(iSecretId)
                ? "清除個人 Plurk 帳號 override（回落共用）"
                : $"設定個人 Plurk 帳號＝{iSecretId}";
            if (UCL_PlurkAccounts.SetPersonaAccount(iPersona, iSecretId,
                    nameof(UCL_PlurkAdminPage), aReason, out string aError))
            {
                SetStatus($"✅ {iPersona}：{aReason}", MessageType.Info);
                Reload();
            }
            else
            {
                SetStatus($"⛔ {iPersona} 寫入失敗：{aError}", MessageType.Error);
            }
            GUI.FocusControl(null);
        }

        void DrawStatus()
        {
            if (string.IsNullOrEmpty(m_Status)) return;
            EditorGUILayout.HelpBox(m_Status, m_StatusType);
        }

        void SetStatus(string iMsg, MessageType iType)
        {
            m_Status = iMsg;
            m_StatusType = iType;
        }
    }
}
#endif
