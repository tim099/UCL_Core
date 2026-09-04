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
// @doc-sync: Assets/Plugins/UCL_Core/Docs~/zh-Hant/Workflows/Plurk_Maintenance.md（§4 帳號與憑證）
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

        /// <summary>是不是**回落到共用預設**（Source 層的事實）。
        /// ⚠ 這**不等於**「這個帳號被幾個人用」—— 見 <see cref="PersonaCount"/>。</summary>
        public bool IsShared => Source == UCL_PlurkAccounts.SourceShared;

        /// <summary>這個 secret id 目前解析得到幾個 persona（由 <see cref="UCL_PlurkAccounts.Resolve"/> 填）。
        /// 0 ＝ 沒算過或 unset。</summary>
        public int PersonaCount = 0;

        /// <summary>能不能發文（有解析到帳號）。⚠ 這**不代表** token 明文已安裝，那要另外查。</summary>
        public bool Resolved => Source != UCL_PlurkAccounts.SourceUnset;

        // 區塊職責：一個帳號「被幾個人用」——共用與否的**唯一判準**。
        // 🩸 血證（summit 2026-09-03）：原本這格是 `IsShared`＝`Source == shared-default`，
        //   而 calli / gura / kiara **各自 override 到同一個 `plurk_myth`** ⇒ Source 是 persona-override
        //   ⇒ 三份回傳檔都印「**個人帳號（plurk_myth）／署名必填: 否**」，而那個帳號有三個人在用。
        //   共用與否不是「我怎麼解析到它」的性質，是「**有幾個人落在同一個帳號上**」的性質。
        //   ⇒ 改成數人頭。仍然是推導、仍然不存 kind 欄位（原設計意圖不變），只是換一個對的量。
        //   ⚠ 而署名正是收件端 persona 路由的第一手資料（TASK-0111）——
        //     這格錯著的時候，最需要署名的那個帳號剛好不必署名。
        /// <summary>這個帳號被多於一人使用（回落共用預設，或多個 persona override 到同一份憑證）。</summary>
        public bool IsMultiPersona => IsShared || PersonaCount > 1;

        /// <summary>多人共用的帳號必須在文案末行署名（Tim 2026-08-16 硬規則）。</summary>
        public bool RequiresSignature => IsMultiPersona;

        public string Describe()
        {
            switch (Source)
            {
                case UCL_PlurkAccounts.SourcePersona:
                    // 多人 override 到同一份憑證時，它**不是**個人帳號 —— 印出人數，別讓字面比事實小
                    return PersonaCount > 1
                        ? $"共用帳號（{SecretId}，{PersonaCount} 位 persona 各自 override 到它）—— 末行署名必填"
                        : $"個人帳號（{SecretId}）";
                case UCL_PlurkAccounts.SourceShared: return $"共用帳號（{SecretId}）—— 末行署名必填";
                default: return "未設定 —— 沒有共用預設、也沒有個人 override";
            }
        }
    }

    // 區塊職責：一份憑證 ↔ 它在 Plurk 上的 nick。
    // 物理意義：**nick 是 @ 唯一會連過去的東西**。顯示名（`cc@basecamp`）可以改、persona 名
    //          （`basecamp`）根本不是 Plurk 上的東西 —— 只有 nick 會被 linkify。
    // 🩸 血證（summit 2026-09-03）：我們一直在文案裡寫 `@summit` / `@basecamp` 以為是在點名同事。
    //   Plurk 把它 linkify 成 `plurk.com/summit`，而那些 nick **都是真實存在的第三方帳號**
    //   （`summit`=id 3905812 休眠／`Calli`=id 3369366 karma 94.97 的活人）。
    //   ⇒ 對內從沒送達（我的 nick 是 `zeta_summit`），對外公開標注陌生人。而 lint 一次都沒叫。
    // 數值影響：本表是 lint 與發文轉換的**唯一對照來源**。空的時候一律**擋下**不猜
    //          （猜一個 nick 出來就是替使用者決定收件人）。
    /// <summary>secret id ↔ Plurk nick（`op=resolve` 會把當前帳號的 nick 寫回來）。</summary>
    public class UCL_PlurkNickEntry : UnityJsonSerializable
    {
        /// <summary>secret id（＝`plurk_*.enc` 的檔名 stem）。</summary>
        public string m_SecretId = "";
        /// <summary>該帳號在 Plurk 上的 nick（`/APP/Users/me` 的 `nick_name`，不是顯示名）。</summary>
        public string m_Nick = "";
        /// <summary>該帳號在 Plurk 上的 **user id**（`/APP/Users/me` 的 `id`）。
        /// <para>nick 會被改名、user id 不會 ⇒ **它才是「這是同一個帳號」的穩定鍵**。
        /// 有了它，`user_id` 同而 `nick` 變 ＝ 對方改名；`user_id` 變 ＝ 這份憑證換綁到**別的帳號**了。
        /// 沒有它的時候那兩件事在表上同形。</para>
        /// <para>⚠ 空字串 ＝ 這一筆是加這欄之前寫的，**不是**「查不到」。</para></summary>
        public string m_PlurkUserId = "";
        /// <summary>這一筆是誰寫的：`secret-scan`（發文路徑自動補齊）／`whoami`（單帳號診斷）／`manual`。
        /// <para>🩸 存在的理由（2026-09-04）：`plurk_meadow` 那筆的時戳晚於當天所有 lint ⇒ 不是自動補的，
        /// 而表上沒有任何欄位答得出它是誰寫的。回傳檔印過來源，但回傳檔會被下一次覆寫。</para>
        /// <para>⚠ 空字串 ＝ 加這欄之前寫的 ⇒ 顯示成 `unknown`，**不回頭猜**。</para></summary>
        public string m_Source = "";
        /// <summary>這一筆是什麼時候讀回來的（UTC ISO）—— 讓「舊資料」看得出自己舊。</summary>
        public string m_FetchedAtUtc = "";
    }

    /// <summary>registry 本體：共用帳號 ＋ nick 對照表。個人帳號住 persona profile。</summary>
    public class UCL_PlurkAccountsConfig : UnityJsonSerializable
    {
        /// <summary>共用（公用帳號）的 secret id。空＝沒有共用預設。</summary>
        public string m_SharedSecretId = "";
        /// <summary>給人看的備註（例：這個共用帳號是誰開的、給誰用）。</summary>
        public string m_Note = "";
        /// <summary>secret id → Plurk nick。⚠ **不手打** —— 由 `op=resolve` 從 `/APP/Users/me` 寫回。</summary>
        public List<UCL_PlurkNickEntry> m_Nicks = new List<UCL_PlurkNickEntry>();
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
                    // ⚠ 這一格**不能省**：`plurk_myth` 正是走這條分支的（三個人各自 override 到它）。
                    //   漏填 ⇒ PersonaCount=0 ⇒ IsMultiPersona=false ⇒ 修法對它完全無效，
                    //   而它是唯一真的需要這個修法的帳號。
                    aResult.PersonaCount = PersonasOn(aResult.SecretId).Count;
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
                aResult.PersonaCount = PersonasOn(aResult.SecretId).Count;
                return aResult;
            }
            return aResult;   // unset
        }

        // ===========================================================
        // 區塊職責：某份憑證目前被哪些 persona 使用 —— 「共用與否」的那個量。
        // 物理意義：走 `PoolNames()` ✕ `Resolve()`，**不另存一份名單**。
        //          名單存起來就會跟事實漂掉，而漂掉的時候兩邊都不報錯。
        // 數值影響：成本 ≈ 21 個 persona 的 profile 欄位讀取。相對於本 Cmd 的 HTTP 往返可忽略；
        //          ⚠ 但**不要在迴圈裡呼叫它**（那會變成 N²）。
        // ===========================================================
        /// <summary>解析到 <paramref name="iSecretId"/> 的 persona 名單（排序、去重）。</summary>
        public static List<string> PersonasOn(string iSecretId)
        {
            var aOut = new List<string>();
            if (string.IsNullOrWhiteSpace(iSecretId)) return aOut;
            string aShared = "";
            try { aShared = (Load().m_SharedSecretId ?? "").Trim(); } catch (Exception) { }
            foreach (string aName in UCL_PersonaProfile.PoolNames())
            {
                if (string.IsNullOrWhiteSpace(aName)) continue;
                string aOwn = (UCL_PersonaProfile.GetString(aName, PersonaField, "") ?? "").Trim();
                string aHit = !string.IsNullOrEmpty(aOwn) ? aOwn : aShared;
                if (aHit == iSecretId && !aOut.Contains(aName)) aOut.Add(aName);
            }
            aOut.Sort(StringComparer.Ordinal);
            return aOut;
        }

        /// <summary>某份憑證的 Plurk nick。**查不到回空字串** —— 呼叫端必須把「查不到」跟「查到空的」分開處置。</summary>
        public static string NickOf(string iSecretId)
        {
            if (string.IsNullOrWhiteSpace(iSecretId)) return "";
            try
            {
                foreach (var aRow in Load().m_Nicks ?? new List<UCL_PlurkNickEntry>())
                    if (aRow != null && aRow.m_SecretId == iSecretId) return (aRow.m_Nick ?? "").Trim();
            }
            catch (Exception) { }
            return "";
        }

        /// <summary>某份憑證上一次讀回的 Plurk user id。**查不到回空字串**
        /// —— 而「這一筆是加這欄之前寫的」跟「這個帳號沒有 id」都是空，呼叫端不可拿它當否定證據。</summary>
        public static string UserIdOf(string iSecretId)
        {
            if (string.IsNullOrWhiteSpace(iSecretId)) return "";
            try
            {
                foreach (var aRow in Load().m_Nicks ?? new List<UCL_PlurkNickEntry>())
                    if (aRow != null && aRow.m_SecretId == iSecretId) return (aRow.m_PlurkUserId ?? "").Trim();
            }
            catch (Exception) { }
            return "";
        }

        /// <summary>把某份憑證的身分寫回 registry。空 nick 不寫入 —— 空值會讓
        /// 「還沒讀過」跟「讀到空的」同形。
        /// <para><paramref name="iSource"/> ＝ 這一筆是誰寫的（`secret-scan` / `whoami` / `manual`）；
        /// <paramref name="iUserId"/> 空字串時**保留既有值不清掉** —— 呼叫端拿不到 id 不代表它不存在。</para></summary>
        public static void SetNick(string iSecretId, string iNick, string iUserId = "", string iSource = "")
        {
            if (string.IsNullOrWhiteSpace(iSecretId) || string.IsNullOrWhiteSpace(iNick)) return;
            var aConfig = Load();
            if (aConfig.m_Nicks == null) aConfig.m_Nicks = new List<UCL_PlurkNickEntry>();
            string aNow = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            string aUid = (iUserId ?? "").Trim();
            string aSrc = (iSource ?? "").Trim();
            foreach (var aRow in aConfig.m_Nicks)
            {
                if (aRow == null || aRow.m_SecretId != iSecretId) continue;
                aRow.m_Nick = iNick.Trim();
                // ⚠ 拿不到 id 就**不動既有值** —— 用空字串覆蓋等於把「我們知道它是誰」擦成「不知道」，
                //   而擦掉之後跟「從來沒讀過」長得一模一樣。
                if (aUid.Length > 0) aRow.m_PlurkUserId = aUid;
                if (aSrc.Length > 0) aRow.m_Source = aSrc;
                aRow.m_FetchedAtUtc = aNow;
                Save(aConfig);
                return;
            }
            aConfig.m_Nicks.Add(new UCL_PlurkNickEntry
            {
                m_SecretId = iSecretId, m_Nick = iNick.Trim(),
                m_PlurkUserId = aUid, m_Source = aSrc, m_FetchedAtUtc = aNow,
            });
            Save(aConfig);
        }

        // ===========================================================
        // 區塊職責：文案裡的 `@名字` 該被改寫成什麼。
        // 物理意義：Plurk 的 @ 只認 **nick**。persona 名不是 Plurk 上的東西 ——
        //          寫 `@gura` 不會通知 gura，它會連到 `plurk.com/gura`（而那個帳號真的存在）。
        // 判準（Tim 2026-09-03 拍板）：
        //   · 1:1 帳號 → `@<nick>`，**不加標記**（nick 已唯一，加了對外人是純噪音）
        //   · 多人帳號 → `@<nick>→<persona>`（`→` 讀作「給誰」；分隔符實測見 TASK-0111）
        //   · 查不到 → **不猜，回 Problem 讓 lint 擋下**。猜一個 nick 就是替使用者決定收件人。
        // 數值影響：nick 沒登記時回 Problem 而不是回空字串當沒事 ——
        //          「查不到」與「查到空的」不得同形。
        // ===========================================================
        /// <summary>persona 標記分隔符。`→` 語意直接（「給誰」），實測不會被 Plurk 吃掉，
        /// 且不撞既有 lint 守衛（半角括號會撞「疑似編輯註記」）。</summary>
        public const string PersonaTagSep = "→";

        /// <summary>一個 `@名字` 的改寫判定。</summary>
        public class UCL_PlurkMentionRewrite
        {
            /// <summary>原字面（不含 `@`）。</summary>
            public string Raw = "";
            /// <summary>命中的 persona 名（空＝不是我們的人）。</summary>
            public string Persona = "";
            /// <summary>解析出的 nick（空＝查不到）。</summary>
            public string Nick = "";
            /// <summary>該帳號被幾個人用。</summary>
            public int PersonaCount = 0;
            /// <summary>建議的替換字串（含 `@`）。<see cref="Problem"/> 非空時無意義。</summary>
            public string Replacement = "";
            /// <summary>非空 ⇒ **擋下**，這一行就是要印給人看的理由。</summary>
            public string Problem = "";
            /// <summary>要不要動它（不是我們的人、或本來就寫對了 ⇒ false）。</summary>
            public bool NeedsRewrite => string.IsNullOrEmpty(Problem)
                && !string.IsNullOrEmpty(Replacement)
                && Replacement != "@" + Raw;
        }

        /// <summary>判定文案裡的 `@<iName>` 該怎麼處理。<paramref name="iName"/> 不含 `@`。</summary>
        public static UCL_PlurkMentionRewrite ResolveMention(string iName)
        {
            var aOut = new UCL_PlurkMentionRewrite { Raw = (iName ?? "").Trim() };
            if (string.IsNullOrEmpty(aOut.Raw)) return aOut;

            // ① 已經是我們某個帳號的 nick ⇒ 本來就寫對了，不動
            try
            {
                foreach (var aRow in Load().m_Nicks ?? new List<UCL_PlurkNickEntry>())
                    if (aRow != null && string.Equals(aRow.m_Nick, aOut.Raw, StringComparison.OrdinalIgnoreCase))
                        return aOut;
            }
            catch (Exception) { }

            // ② 是不是我們的 persona 名（大小寫不敏感 —— 寧可多攔一個同事，也不要漏放一個陌生人）
            string aPersona = "";
            foreach (string aName in UCL_PersonaProfile.PoolNames())
            {
                if (string.IsNullOrWhiteSpace(aName)) continue;
                if (string.Equals(aName, aOut.Raw, StringComparison.OrdinalIgnoreCase)) { aPersona = aName; break; }
            }
            if (string.IsNullOrEmpty(aPersona)) return aOut;   // 不是我們的人 ⇒ 不動（可能是外面的真 nick）
            aOut.Persona = aPersona;

            // ③ 這個 persona 用哪個帳號
            var aRes = Resolve(aPersona);
            if (!aRes.Resolved)
            {
                aOut.Problem = $"`@{aOut.Raw}` 是我們的 persona，但**它沒有對應的 Plurk 帳號**"
                    + $"（沒有 override、也沒有共用預設）⇒ 送出去會連到 `plurk.com/{aOut.Raw}`，那不是他";
                return aOut;
            }
            aOut.PersonaCount = aRes.PersonaCount;

            // ④ 那個帳號的 nick
            aOut.Nick = NickOf(aRes.SecretId);
            if (string.IsNullOrEmpty(aOut.Nick))
            {
                // 🩸 首版這行寫「先跑 op=resolve」——**而真正會寫 nick 的是 `op=whoami`**
                //   （它才呼叫 `/APP/Users/me`）。我自己剛蓋好的指路牌就指錯路，今天第三次同族。
                //   ⇒ 指路要指到**真的會做那件事的那一支**，而不是名字聽起來像的那一支。
                // 🩸 第二層（2026-09-04，Tim 指示後改）：舊訊息說「只有那支憑證問得到自己的 nick」
                //   而把它實作成「請那個人上線跑一次」—— **憑證是檔案不是人**，工具自己問得到。
                //   發文三路（lint／preview／post）在轉換前已經跑過 `EnsureNicksAsync` 補齊，
                //   ⇒ **走到這裡代表補不到**（憑證不在這台／已失效／回應沒有 nick），
                //   所以訊息要講那句當下為真的話，而不是退回去叫人跑一支補不進這棵樹的指令。
                aOut.Problem = $"`@{aOut.Raw}` → 帳號 `{aRes.SecretId}`，"
                    + "而**自動補齊已經試過、拿不到它的 nick**（回傳檔的「nick 自動補齊」那節有逐筆理由）"
                    + $"⇒ 多半是這台機器上沒有 `{aRes.SecretId}` 的可用憑證，或那份憑證已失效。"
                    + $"（該帳號目前掛著：{string.Join(" / ", PersonasOn(aRes.SecretId))}）"
                    + "⛔ 這裡刻意不猜一個 nick —— 猜錯就是公開標注陌生人";
                return aOut;
            }

            aOut.Replacement = aRes.IsMultiPersona
                ? $"@{aOut.Nick}{PersonaTagSep}{aPersona}"
                : $"@{aOut.Nick}";
            return aOut;
        }

        // ===========================================================
        // 區塊職責：讀取端 —— 一段文字裡的 `@<nick>` 到底有沒有點名到「我」。
        // 物理意義：Plurk 的通知是**帳號層**的，而共用帳號有多個人。
        //   判準（Tim 2026-09-03 拍板）：
        //     · `@<nick>→<我>`      ⇒ 指名我
        //     · `@<nick>→<別人>`    ⇒ 指名別人（我看得到，但不算我未回）
        //     · `@<nick>` 沒帶標記  ⇒ **視為 @ 該帳號內所有人** ⇒ 算我
        // 📌 最後那條是 Tim 的版本，比我原本的「列出全部由人決定」好：
        //   **誰收到不該靠社交判斷**（那會變成人人以為別人會回），誰回才是社交決定。
        // 數值影響：純字串判定、零 IO。標記字元見 PersonaTagSep。
        // ===========================================================
        /// <summary>一段文字裡 `@nick` 的點名判定。</summary>
        public class UCL_PlurkMentionHit
        {
            /// <summary>文字裡有沒有出現 `@nick`（不分大小寫）。</summary>
            public bool Found = false;
            /// <summary>算不算點名我（含「沒帶標記 ⇒ 全體」那條）。</summary>
            public bool HitsMe = false;
            /// <summary>出現過的標記（`→` 後面那串），依出現順序、去重。</summary>
            public List<string> Tags = new List<string>();
            /// <summary>有沒有出現「沒帶標記」的 `@nick`（那條就是 Tim 的全體規則）。</summary>
            public bool HasUntagged = false;
        }

        /// <summary>判斷 <paramref name="iText"/> 裡的 `@<iNick>` 有沒有點名 <paramref name="iMyPersona"/>。</summary>
        public static UCL_PlurkMentionHit ClassifyMention(string iText, string iNick, string iMyPersona)
        {
            var aOut = new UCL_PlurkMentionHit();
            if (string.IsNullOrEmpty(iText) || string.IsNullOrEmpty(iNick)) return aOut;
            string aNeedle = "@" + iNick;
            int aFrom = 0;
            while (true)
            {
                int aAt = iText.IndexOf(aNeedle, aFrom, StringComparison.OrdinalIgnoreCase);
                if (aAt < 0) break;
                aOut.Found = true;
                int aAfter = aAt + aNeedle.Length;
                string aTag = "";
                if (aAfter + PersonaTagSep.Length <= iText.Length &&
                    string.CompareOrdinal(iText, aAfter, PersonaTagSep, 0, PersonaTagSep.Length) == 0)
                {
                    int aP = aAfter + PersonaTagSep.Length;
                    int aQ = aP;
                    while (aQ < iText.Length &&
                           (char.IsLetterOrDigit(iText[aQ]) || iText[aQ] == '_' || iText[aQ] == '-')) aQ++;
                    aTag = iText.Substring(aP, aQ - aP);
                }
                if (string.IsNullOrEmpty(aTag))
                {
                    // 沒帶標記 ⇒ 視為 @ 該帳號內所有人（Tim 2026-09-03）
                    aOut.HasUntagged = true;
                    aOut.HitsMe = true;
                }
                else
                {
                    if (!aOut.Tags.Contains(aTag)) aOut.Tags.Add(aTag);
                    if (!string.IsNullOrEmpty(iMyPersona) &&
                        string.Equals(aTag, iMyPersona, StringComparison.OrdinalIgnoreCase)) aOut.HitsMe = true;
                }
                aFrom = aAfter;
            }
            return aOut;
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
