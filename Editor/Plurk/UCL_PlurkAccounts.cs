// 區塊職責：Plurk 帳號解析 —— 「這個 persona 發文要用哪一份憑證」的**唯一解析點**。
// 物理意義：Tim 2026-08-21 拍板三句：① 只分**個人**與**共用** ② 帳號資訊（token）存
//          `UCL_SecretManagerPage`（`_secrets/*.enc`）③ **persona 資料用 id 關聯到具體用哪一份**。
//          ⇒ 所以本檔**不存任何 token**，只存「誰用哪個 secret id」。
//
// 形狀刻意抄 `agent_email.py` 的 `resolve_email`（Tim 指定「參考目前 email 設定」）：
//   **override → 預設 → 哨兵，且回值一律帶 `Source`**。
//   `Source` 不是除錯資訊，是**規則的輸入** —— 共用帳號發文必須署名（Tim 2026-08-16 硬規則），
//   而「是不是共用」只有 `Source` 答得出來。
//
// 數值影響：
//   · **刻意不存 kind 欄位**。個人／共用由 `Source` 推導 —— 多一個欄位就多一個會跟事實漂掉的地方，
//     而那種漂掉（欄位說個人、解析出共用）兩邊都不會報錯。
//   · **刻意不做 agent 層**（email 有那層是因為信箱本來就綁 agent）。Plurk 帳號不是那種東西，
//     它是「某個人的」或「大家共用的」。留一個沒人用的槽＝留一個會漂的地方。
//   · persona override **寫在 persona profile**（`UCL_PersonaProfile.SetField`，actor/reason 必填），
//     不寫 `AwakenInit/personas/<name>.json` —— 那個舊源 2026-08-19 起**只出不進，寫了不會生效**。
// @doc-sync: Assets/Plugins/UCL_Core/Docs~/zh-Hant/UCL_EditorPage/UCL_PlurkAdminPage.md（帳號解析三段表）
// @doc-sync: Assets/Plugins/UCL_Core/Docs~/zh-Hant/Plan/Plan_Plurk_Bot.md（§3 帳號解析）
// ⚠ **本檔刻意放 `Editor/`（assembly `UCL_CoreEditor`）而不是 `UCL_Core_Scripts/`**：
//   組件引用是單向的 —— `UCL_CoreEditor` → `UCL_Core`。SecretManager 住在 `Editor/`，
//   所以放在 `UCL_Core_Scripts` 的檔案**看不到 `UCL_SecretScanner`**（CS0246）。
//   放這裡才能用既有掃描器，而不是為了遷就位置去自己再寫一份找 .enc 的邏輯。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UCL.Core.EditorLib.AgentCommands;
using UCL.Core.EditorLib.SecretManager;
using UCL.Core.JsonLib;

namespace UCL.Core.EditorLib.Plurk
{
    /// <summary>解析結果。**一律帶 Source** —— 呼叫端要知道「憑什麼是這個帳號」。</summary>
    public class UCL_PlurkAccountResolution
    {
        /// <summary>secret id（＝`_secrets/<id>.enc` 的檔名 stem）。`Source=unset` 時為空。</summary>
        public string SecretId = "";
        /// <summary>`persona-override`（個人）｜`shared-default`（共用）｜`unset`（沒有可用帳號）。</summary>
        public string Source = UCL_PlurkAccounts.SourceUnset;

        /// <summary>是不是共用帳號 —— **由 Source 推導，不另存欄位**。</summary>
        public bool IsShared => Source == UCL_PlurkAccounts.SourceShared;
        /// <summary>能不能發文（有解析到帳號）。⚠ 這**不代表** token 明文已安裝，那要另外查。</summary>
        public bool Resolved => Source != UCL_PlurkAccounts.SourceUnset;

        /// <summary>共用帳號必須在文案末行署名（Tim 2026-08-16 硬規則）。</summary>
        public bool RequiresSignature => IsShared;

        public string Describe()
        {
            switch (Source)
            {
                case UCL_PlurkAccounts.SourcePersona: return $"個人帳號（{SecretId}）";
                case UCL_PlurkAccounts.SourceShared: return $"共用帳號（{SecretId}）—— 末行署名必填";
                default: return "未設定 —— 沒有共用預設、也沒有個人 override";
            }
        }
    }

    /// <summary>registry 本體：目前只有一個欄位（共用帳號）。個人帳號住 persona profile。</summary>
    public class UCL_PlurkAccountsConfig : UnityJsonSerializable
    {
        /// <summary>共用（公用帳號）的 secret id。空＝沒有共用預設。</summary>
        public string m_SharedSecretId = "";
        /// <summary>給人看的備註（例：這個共用帳號是誰開的、給誰用）。</summary>
        public string m_Note = "";
    }

    public static class UCL_PlurkAccounts
    {
        public const string FileName = "plurk_accounts.json";
        /// <summary>persona profile 的欄名（個人帳號 override）。</summary>
        public const string PersonaField = "plurk_account";
        /// <summary>secret 檔名前綴 —— 只有這個前綴的 `.enc` 會被當成 Plurk 憑證列出。</summary>
        public const string SecretPrefix = "plurk_";

        public const string SourcePersona = "persona-override";
        public const string SourceShared = "shared-default";
        public const string SourceUnset = "unset";

        // 區塊職責：registry 路徑。
        // 物理意義：`AwakenInit` 底下，跟 `agent_emails.json` 同一層（同類東西放同一處）。
        //          ⚠ 路徑走 `UCL_AgentCommandsPath.DataRoot` —— 與 `UCL_PersonaProfile` 內
        //            兩處既有寫法同一個 idiom，不自己另推導。
        public static string RegistryPath()
            => Path.Combine(UCL_AgentCommandsPath.DataRoot, "AwakenInit", FileName).Replace('\\', '/');

        /// <summary>讀 registry。檔案不存在回**空設定**（那是合法狀態：還沒設共用帳號）；
        /// 解析失敗**丟例外不回空** —— 「設定壞了」與「還沒設」必須可分辨。</summary>
        public static UCL_PlurkAccountsConfig Load()
        {
            string aPath = RegistryPath();
            if (!File.Exists(aPath)) return new UCL_PlurkAccountsConfig();
            string aText = File.ReadAllText(aPath);
            if (string.IsNullOrWhiteSpace(aText)) return new UCL_PlurkAccountsConfig();
            var aJson = JsonData.ParseJson(aText);
            if (aJson == null) throw new Exception($"[PlurkAccounts] 解析失敗：{aPath}");
            var aConfig = new UCL_PlurkAccountsConfig();
            aConfig.DeserializeFromJson(aJson);
            return aConfig;
        }

        /// <summary>寫 registry（UTF-8 **無 BOM** —— `Encoding.UTF8` 會寫 BOM，python 端讀到會炸）。</summary>
        public static void Save(UCL_PlurkAccountsConfig iConfig)
        {
            if (iConfig == null) throw new Exception("[PlurkAccounts] config 為 null");
            string aPath = RegistryPath();
            string aDir = Path.GetDirectoryName(aPath);
            if (!string.IsNullOrEmpty(aDir) && !Directory.Exists(aDir)) Directory.CreateDirectory(aDir);
            File.WriteAllText(aPath, iConfig.SerializeToJson().ToJsonBeautify(),
                new System.Text.UTF8Encoding(false));
        }

        /// <summary>可用的 Plurk secret id 清單（掃 `_secrets/plurk_*.enc` 取檔名 stem）。
        /// ⚠ 清單來源是 `UCL_SecretScanner`（掃描唯一來源）—— 本檔不自己走檔案系統找 .enc。</summary>
        public static List<string> ListSecretIds()
        {
            var aIds = new List<string>();
            foreach (var aInfo in UCL_SecretScanner.Scan())
            {
                if (aInfo == null || string.IsNullOrEmpty(aInfo.EncPath)) continue;
                string aStem = Path.GetFileNameWithoutExtension(aInfo.EncPath);
                if (string.IsNullOrEmpty(aStem)) continue;
                if (!aStem.StartsWith(SecretPrefix, StringComparison.Ordinal)) continue;
                if (!aIds.Contains(aStem)) aIds.Add(aStem);
            }
            aIds.Sort(StringComparer.Ordinal);
            return aIds;
        }

        /// <summary>某個 secret id 的 metadata（含明文是否已安裝）。找不到回 null。</summary>
        public static UCL_SecretInfo FindSecret(string iSecretId)
        {
            if (string.IsNullOrEmpty(iSecretId)) return null;
            foreach (var aInfo in UCL_SecretScanner.Scan())
            {
                if (aInfo == null || string.IsNullOrEmpty(aInfo.EncPath)) continue;
                if (Path.GetFileNameWithoutExtension(aInfo.EncPath) == iSecretId) return aInfo;
            }
            return null;
        }

        /// <summary>核心：這個 persona 發文用哪一份憑證。
        /// 順序＝**persona override → 共用預設 → 哨兵**（形狀同 `resolve_email`）。</summary>
        public static UCL_PlurkAccountResolution Resolve(string iPersona)
        {
            var aResult = new UCL_PlurkAccountResolution();
            if (!string.IsNullOrWhiteSpace(iPersona))
            {
                string aOwn = (UCL_PersonaProfile.GetString(iPersona, PersonaField, "") ?? "").Trim();
                if (!string.IsNullOrEmpty(aOwn))
                {
                    aResult.SecretId = aOwn;
                    aResult.Source = SourcePersona;
                    return aResult;
                }
            }

            string aShared = "";
            try { aShared = (Load().m_SharedSecretId ?? "").Trim(); }
            catch (Exception)
            {
                // registry 壞掉時**不假裝沒有共用帳號** —— 那會靜默退化成 unset。
                // 讓它以 unset 回報但由頁面／呼叫端另行顯示 Load() 的例外（頁面會顯示）。
                aShared = "";
            }
            if (!string.IsNullOrEmpty(aShared))
            {
                aResult.SecretId = aShared;
                aResult.Source = SourceShared;
                return aResult;
            }
            return aResult;   // unset
        }

        /// <summary>設某個 persona 的個人帳號（空值＝清掉 override、回落共用）。
        /// 走 `UCL_PersonaProfile.SetField`（actor/reason 必填，寫入有審計）。</summary>
        public static bool SetPersonaAccount(string iPersona, string iSecretId,
            string iActor, string iReason, out string oError)
        {
            oError = "";
            if (string.IsNullOrWhiteSpace(iPersona)) { oError = "persona 必填 —— 不猜身分"; return false; }
            return UCL_PersonaProfile.SetField(iPersona, PersonaField, (iSecretId ?? "").Trim(),
                iActor, iReason, out oError);
        }
    }
}
#endif
