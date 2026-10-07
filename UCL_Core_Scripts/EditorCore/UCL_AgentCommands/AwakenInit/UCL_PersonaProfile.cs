// 區塊職責：persona 身分／路由欄位的**唯一讀取入口**（C# 端；對側 = python _lib/persona_profile.py）。
// 物理意義：欄位分住 `profile/` 與 `bank/`，消費端若各自解析就會有第二個解析器 ——
//          而兩個解析器對同一個人給不同答案時，兩邊都不會報錯。讀取收斂在本檔，
//          所以欄位再拆家也只改這裡。
// 數值影響：資料源＝`letters/<p>/profile/`（identity 欄）＋ `letters/<p>/bank/<區域>.md`（帳號歸屬）。
//          PoolNames 帶 dir-mtime 快取（沿 UCL_ChatTavernIO 舊實作 —— 每筆 post 都會查白名單）。
//          壞檔略過但 LogWarning（靜默跳過會讓「檔壞了」跟「沒這個人」同形）。
// ⛔ 本檔**只讀**（TASK-0361）：persona 檔的寫入（身分欄／綁定／審計）一律走 Senate
//   `senate cmd persona-profile`（Editor 端經 `UCL_PersonaProfileSenateBridge`）。⛔ 不要在這裡長回寫入方法。
//   （`WriteSnapshot` 寫的是衍生快取 `_persona_profile_snapshot.json`，不是 persona 資料。）
// ⚠ 活體欄（status / last_active / wake_count…）刻意不在本接縫 —— 真相源是 lock 與 wakes/；
//   在線名單走 UCL_ActivePersonaLocks（presence 唯一掃描實作）。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UCL.Core.JsonLib;

namespace UCL.Core.EditorLib.AgentCommands
{
    public static class UCL_PersonaProfile
    {
        // ⛔ `PersonasDir` 已退場（2026-08-21）：資料住 letters/<persona>/（profile/ ＋ bank/）。

        // 欄位分類（§8.3 拍板）—— 與 python _lib/persona_profile.py 的同名常數**兩端同步義務**。
        // 讓分類在兩端都是可被編譯器／搜尋找到的東西，不是註解裡的約定（紅隊 seq 12274 洞①）。
        // email 歸 identity：個人信箱是人的署名不是專案的路由（Tim §8.3 二輪拍板；紅隊題①對出初版錯置）。
        // plurk_account 歸 identity（Tim 2026-08-21）：「這個人用哪一份 Plurk 憑證」跟信箱同型 ——
        //   跟著人走、不綁專案。⚠ 加進本清單才會生效：`WriteProfileField` 只收 identity 欄，
        //   非 identity 欄的 `SetField` 會 patch 回 legacy —— 而 `UCL_PlurkAccounts` 的檔頭
        //   早就宣告「不寫 AwakenInit/personas」。清單缺一格 ⇒ 那句宣告是假的（說法比實作大）。
        // 📌 2026-08-21 起 `personas/<p>.json` 退場，persona 資料整合到 `letters/<persona>/`（Tim 拍板）：
        //    · `agent` **不再是儲存欄**，改由 `bank/<本專案區域>.md` 推導（帳號 id ＝ agent id；
        //      實測 21/21 與舊 registry 的 agent 欄逐字相同，所以這不是換語意，是拿掉重複的那一份）。
        //    · `model` / `actual_agent` 從 routing 轉進 identity ⇒ 改住 `profile/`。
        //    · `wake_count` / `status` / `last_active` / `last_consolidated_*` 是**推導欄**（見 BuildPersonaRaw），
        //      不儲存、不接受寫入 —— 搬一個快取過來只是多一個會落後的地方（BUG-4 的家）。
        public static readonly string[] ROUTING_FIELDS = { "agent", "model", "actual_agent" };
        public static readonly string[] IDENTITY_FIELDS = { "layer_role", "forked_from", "fork_lineage",
            "forked_at", "created_at", "identity_vector", "vector_history", "email", "plurk_account",
            "model", "actual_agent" };

        static HashSet<string> s_NamesCache;
        static long s_NamesCacheMtime = -1;

        // ===========================================================
        // 區塊職責：persona pool 名單 —— **判準是 `letters/<p>/profile/` 目錄存在**（Tim 2026-08-21 拍板）。
        // 物理意義：`AwakenInit/personas/*.json` 退場後，名單只能問 letters。而「掃 letters 目錄」
        //          本身**不能**當名單：實測 33 個目錄裡有 12 個是幽靈（GawrGura／Tim／apex／
        //          basecamp0512／tavern-keeper…＝改名或早期實驗的殘骸）。
        //          `profile/` 是接縫建立的 ⇒ 它的存在等於「這個人被當成 persona 讀寫過」。
        //          實測判準乾淨：**21/21 真人有、12/12 幽靈沒有**。
        // ⚠ 已知代價（Tim 選項 A 的明說代價）：letters submodule **沒 init** 時是空目錄 ⇒
        //   那個人會安靜地從名單上消失（錢與登入都查不到他，而沒有一格會報錯）。
        //   ⇒ 所以本函式**空名單一定出聲**：一個都掃不到幾乎不可能是真的。
        // 數值影響：純讀。dir-mtime 快取的鍵改成 letters 根目錄（新增／刪除 persona 目錄會動它；
        //          目錄內部改動不會 —— 而 pool 名單只關心有哪些目錄）。
        // ===========================================================

        public static HashSet<string> PoolNames()
        {
            // 判準與掃描本體在 SCP_Core（`SCP_PersonaProfile.PoolNames`）。
            // 留在這裡的只有 **dir-mtime 快取** —— 那是宿主層的效能決定（每筆酒館 post 都會查白名單），
            // 不是判準。⚠ 快取的鍵是 letters 根的 mtime：新增／刪除 persona 目錄會動它，
            // 目錄「內部」改動不會 —— 而 pool 名單只關心有哪些目錄。
            string dir = UCL_LettersPath.Root;
            long mtime;
            try { mtime = Directory.Exists(dir) ? Directory.GetLastWriteTimeUtc(dir).Ticks : -1L; }
            catch { mtime = -1L; }
            if (mtime == s_NamesCacheMtime && s_NamesCache != null) return s_NamesCache;

            var set = new HashSet<string>(
                SCP.Core.Letters.SCP_PersonaProfile.PoolNames(dir, w => Debug.LogError(w)));
            s_NamesCache = set;
            s_NamesCacheMtime = mtime;
            return set;
        }

        /// <summary>排序後的 pool 名單（顯示用）。</summary>
        public static List<string> PoolNamesSorted()
        {
            var list = new List<string>(PoolNames());
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        /// <summary>
        /// 「有沒有這個人」與 PoolNames 同一套判準：檔案存在**且**不吃 _ / . 前綴 ——
        /// 兩個判準給不同答案是紅隊（seq 12274 洞②）點名的病理型（同一個問題兩個真相源）。
        /// </summary>
        public static bool Exists(string iPersona)
            => SCP.Core.Letters.SCP_PersonaProfile.Exists(UCL_LettersPath.Root, iPersona);

        /// <summary>路由欄（§8.3 綁專案組）。查無此人回 null。缺欄回空字串。</summary>
        public static Dictionary<string, string> GetRouting(string iPersona)
        {
            var jd = GetRaw(iPersona);
            if (jd == null) return null;
            var d = new Dictionary<string, string>();
            foreach (var f in ROUTING_FIELDS) d[f] = jd.GetString(f, "");
            return d;
        }

        /// <summary>身分欄（§8.3 不綁專案組；identity_vector / vector_history 是結構值故回 JsonData）。查無此人回 null。</summary>
        public static Dictionary<string, JsonData> GetIdentity(string iPersona)
        {
            var jd = GetRaw(iPersona);
            if (jd == null) return null;
            var d = new Dictionary<string, JsonData>();
            foreach (var f in IDENTITY_FIELDS)
                if (jd.Contains(f)) d[f] = jd[f];
            return d;
        }

        /// <summary>
        /// 整份 persona 資料 —— **legacy 檔疊上 `profile/` 覆蓋後的合併值**（Phase 1）。
        /// 不存在回 null；壞檔回 null 並警告。identity 欄缺 `profile/` 檔時**當場遷移**（見 MayMigrate 的閘）。
        /// </summary>
        public static JsonData GetRaw(string iPersona) => GetRaw(iPersona, true);

        /// <summary>
        /// 同上，但可關閉遷移（<paramref name="iAllowMigrate"/>=false ⇒ **只合併不寫任何檔**）。
        /// 批次匯出（<see cref="WriteSnapshot"/>）走 false —— 理由見 MayMigrate 區塊。
        /// </summary>
        public static JsonData GetRaw(string iPersona, bool iAllowMigrate)
        {
            // ⚠ 本體已收斂到 SCP_Core（`SCP_PersonaProfile.GetRaw`）——
            //   LY 掛了 SCP_Core 且 Unity 真的編它，所以 Editor 與 senate.exe 走**同一份實作**。
            //   ⛔ 這裡不准長出任何邏輯：一旦 facade「順手多做一點」，那就是第二份實作，
            //      而兩份實作對同一個 persona 給出不同答案時，不會有任何一層報錯。
            // ⚠ iAllowMigrate 目前無作用：lazy migration 已是死碼
            //   （實測 2026-08-31 本 repo 全庫 `_field_sources` legacy=0；SCP 側**只讀不遷**）。
            //   參數保留是為了不動既有呼叫端的簽章；兩個值都等於「只合併不寫任何檔」。
            var aScp = SCP.Core.Letters.SCP_PersonaProfile.GetRaw(
                UCL_LettersPath.Root, iPersona,
                Treasury.UCL_CentralBankSettings.CurrencyId,
                w => Debug.LogWarning(w));
            if (aScp == null) return null;
            // 型別邊界只有這一個點：SCP_JsonData → 文字 → JsonData。
            // 收成一個點的理由是**它可驗**（全庫 21 人 GetRaw 對拍）。
            return JsonData.ParseJson(aScp.ToJson(false));
        }

        public static string GetString(string iPersona, string iField, string iDefault = "")
        {
            var jd = GetRaw(iPersona);
            return jd == null ? iDefault : jd.GetString(iField, iDefault);
        }

        public static int GetInt(string iPersona, string iField, int iDefault = 0)
        {
            var jd = GetRaw(iPersona);
            return jd == null ? iDefault : jd.GetInt(iField, iDefault);
        }

        // ===========================================================
        // 區塊職責：Phase 1 —— `profile/` 合併層與 read-through lazy migration（§8.2／§8.4）。
        //
        // 物理意義：identity 欄的真相從 legacy 大檔搬到 `letters/<p>/profile/<field>.md`（一欄一檔）。
        //          規則（Tim §8.4 二輪拍板）：**有新讀新、缺新當場遷、絕不回寫舊源**。
        //          合併做在 `GetRaw` 底下，所以：
        //            ① 32 支消費端一支都不用改（Phase 0 蓋接縫就是為了這一刻）
        //            ② `WriteSnapshot` 走同一個入口 ⇒ python 端拿到的自動是合併值，
        //               **不需要知道 profile/ 存在**（summit 2026-08-19 拍板，酒館 seq 12448 Q1）
        //
        // ⚠ 型別由**欄名**決定，不由值決定（三類，見 STRUCTURED_FIELDS / NULLABLE_SCALAR_FIELDS）：
        //   看值猜型別在讀回時分不出字串 "null" 與真的 null。
        //   實測 21 個 persona 的型別分布支持這個切法：layer_role/created_at/email＝str、
        //   forked_from/forked_at＝str×14＋**null×7**、fork_lineage/identity_vector/vector_history＝list×21。
        //   而全庫**沒有任何一個空字串的 forked_from/forked_at** ⇒「空檔＝null」這個編碼與現存資料不衝突。
        //
        // ⚠ 尾端換行：寫檔一律補一個換行（否則每個檔都是 no-newline-at-EOF），讀回時 TrimEnd 掉。
        //   ⇒ **純量值尾端的換行不保留**。現存資料沒有這種值；真的需要保留就得改編碼（別默默 Trim 更多東西）。
        //
        // 數值影響：`GetRaw` 是熱路徑，本層每次呼叫會對 8 個 identity 欄各做一次 File.Exists。
        //          遷移只在「profile/ 缺、legacy 有」時發生一次；之後就走 ①，不再寫。
        // ===========================================================

        /// <summary>合併結果裡的**來源標記欄**（欄名 → profile／legacy／absent）。底線前綴＝衍生欄非本體欄。</summary>
        public const string FIELD_SOURCES_KEY = "_field_sources";
        public const string SRC_PROFILE = "profile";
        public const string SRC_LEGACY = "legacy";
        public const string SRC_ABSENT = "absent";

        /// <summary>lazy migration 的 actor —— 讓「自動遷移」與「人改的」在審計檔裡分得開（§8.4）。</summary>
        public const string ACTOR_LAZY_MIGRATION = "lazy-migration";

        // 結構值欄（內文＝JSON）／可為 null 的純量欄（空檔＝null）；其餘 identity 欄＝純字串（空檔＝空字串）。
        // ⚠ **本表是型別判準的唯一真相源**（summit 2026-08-19 拍板，酒館 seq 12478 A）——
        //   快照會帶出一份（`structured_fields`）給 python 端讀，**不准在對側另立一張表**。
        public static readonly string[] STRUCTURED_FIELDS_ORDER =
            { "identity_vector", "vector_history", "fork_lineage" };

        static readonly HashSet<string> STRUCTURED_FIELDS =
            new HashSet<string>(STRUCTURED_FIELDS_ORDER);

        static readonly HashSet<string> NULLABLE_SCALAR_FIELDS =
            new HashSet<string> { "forked_from", "forked_at" };

        /// <summary>這個欄名是不是 identity 組（§8.3 不綁專案組）。</summary>
        public static bool IsIdentityField(string iField)
        {
            if (string.IsNullOrEmpty(iField)) return false;
            foreach (var f in IDENTITY_FIELDS) if (f == iField) return true;
            return false;
        }

        // ===========================================================
        // 區塊職責：遷移放行判準 —— **存取舊資料就遷移**（Tim 2026-08-19 拍板）。
        // 物理意義：這就是 read-through lazy migration 的原意（§8.4）：
        //          「有新讀新、缺新當場遷、絕不回寫舊源」—— 觸發條件是**存取**，不是名單。
        //          ⇒ 白名單那道閘已拆除。它當初存在的理由是鐵律二（真人不當白老鼠），
        //            而那個階段已經走完：Template 全流程過、kiara（真人第一位）
        //            round-trip 8/8 無損、legacy sha1 未變、revert 演練過。
        //          放行前另做過**全庫預檢**：21 人 × 150 格 encode→decode 模擬，零損失。
        // ⚠ 仍然保留的一格：`WriteSnapshot` 走 `GetRaw(iAllowMigrate:false)`。
        //   理由不是「怕遷」，是**批次匯出不是消費端存取** ——
        //   domain reload 觸發的快照重寫沒有任何人在要那個值，讓它寫檔等於
        //   把「誰真的被用到」這個訊號抹掉，而 §8.4 的收斂判準
        //   （source=legacy 歸零＝活資料都遷完了）正是靠那個訊號。
        //   ⇒ 消費端讀到誰就遷誰；匯出只讀不寫。兩者不是同一件事。
        // 📌 可逆性（拿真人試的前提）：profile/ 是從 legacy 抄出來的，legacy 從不被回寫
        //   （見 FreezeLegacyIdentity）⇒ 砍掉 letters/<p>/profile/ 就回到遷移前，一個位元組不差。
        //   這句是演練過的，不是推論的。
        // ===========================================================

        /// <summary>
        /// 這個 persona 可不可以被自動遷移 —— **有名字就可以**（存取即遷移）。
        /// 留成一支具名方法而不是內聯 true：以後若要再長出例外（例如凍結某人），
        /// 這裡是唯一的落點，不必再去 MergeProfile 裡加條件。
        /// </summary>
        public static bool MayMigrate(string iPersona) => !string.IsNullOrEmpty(iPersona);

        /// <summary>某 persona 的 identity 欄來源總表（欄 → profile／legacy／absent）。**不觸發遷移。**</summary>
        public static Dictionary<string, string> GetFieldSources(string iPersona)
        {
            var jd = GetRaw(iPersona, false);
            if (jd == null) return null;
            var d = new Dictionary<string, string>();
            var src = jd.Contains(FIELD_SOURCES_KEY) ? jd[FIELD_SOURCES_KEY] : null;
            foreach (var f in IDENTITY_FIELDS)
                d[f] = (src != null && src.Contains(f)) ? src.GetString(f, SRC_ABSENT) : SRC_ABSENT;
            return d;
        }


        // ===========================================================
        // 區塊職責：persona 的**銀行綁定**讀寫（`letters/<persona>/bank/<currencyId>.md`）。
        // 物理意義：Tim 2026-08-20 拍板 —— 「這個 persona 在某個區域用哪個帳號」是**該區域的宣告**，
        //          而帳號 id ＝ agent id（「agent id」那套獨立命名空間退場）。
        //          檔案跟著 persona 走（letters repo），鍵是區域 ID ⇒ 同一份 letters 可以
        //          同時服務多個專案而不對撞（一區一檔的理由見 UCL_LettersPath.BankDirName 區塊）。
        // 數值影響：**讀寫刻意不對稱**（指示 ⑪）——
        //          讀：① 本區檔 ② 本區缺檔則退其他區域的檔（跨區借用，回傳 oSource 標明借自哪一區；
        //              多個候選**不挑**，回空並標 ambiguous）③ 都沒有 ⇒ 回空（央行＋ErrorLog 由呼叫端做，
        //              那是 Treasury 的職責，不是本接縫的）。
        //          寫：**不在本檔**（TASK-0361，Tim 2026-10-01「寫入端整合到 Senate，Unity 端不留」）——
        //          唯一的寫入端是 SCP_Core `SCP_PersonaProfileWrite`（`senate cmd persona-profile`），
        //          Editor 頁面經 `UCL_PersonaProfileSenateBridge` spawn 那支指令。
        // ⚠ `iCurrencyId` 由呼叫端提供（`UCL_CentralBankSettings.CurrencyId`），本接縫**不自己去問** ——
        //   低層接縫反向依賴 Treasury 設定會讓依賴方向反過來，而且測試時無法餵不同區域。
        // ===========================================================

        /// <summary>`GetBankAccount` 的來源標記：本區命中時＝該區域 ID；跨區借用時＝借出的區域 ID。</summary>
        public const string BankSourceAbsent = "absent";
        /// <summary>綁定檔這一瞬間讀不了（TASK-0265）—— ⛔ 不是 <see cref="BankSourceAbsent"/>。對映 SCP 同名常數。</summary>
        public const string BankSourceUnreadable = SCP.Core.Letters.SCP_PersonaProfile.BankSourceUnreadable;
        /// <summary>多個其他區域都有值 —— **不挑一個**，回空並由呼叫端處置。</summary>
        public const string BankSourceAmbiguous = "ambiguous";

        /// <summary>
        /// 讀 persona 在指定區域使用的帳號（＝agent id）。找不到回空字串。
        /// </summary>
        /// <param name="oSource">
        /// 命中的區域 ID（本區或借用來源）／<see cref="BankSourceAbsent"/>／<see cref="BankSourceAmbiguous"/>。
        /// ⚠ **`oSource != iCurrencyId` 就代表這不是本區的宣告** —— 呼叫端必須讓它可見，
        /// 否則「本區真的綁了」與「借用別區的」在輸出上同形，而前者才是收斂目標。
        /// </param>
        /// <param name="oNote">給人看的補充（借用哪一區／有哪幾個候選）。無事時為空字串。</param>
        public static string GetBankAccount(string iPersona, string iCurrencyId,
            out string oSource, out string oNote)
            // 讀綁定的本體在 SCP_Core（`SCP_PersonaProfile.GetBankAccount`）。
            // ⚠ **讀綁定不是動錢，寫綁定是**（Tim 2026-08-31 拍板）：這支讀的是
            //   `letters/<p>/bank/<region>.md` 一個純文字檔，不碰帳本也不碰餘額；
            //   而本檔底下那幾支 Write/Rename/Copy/Delete 會改「錢進哪個帳戶」，**沒有搬**。
            => SCP.Core.Letters.SCP_PersonaProfile.GetBankAccount(
                UCL_LettersPath.Root, iPersona, iCurrencyId,
                out oSource, out oNote, w => Debug.LogWarning(w));

        /// <summary>讀一個綁定檔：裸值 ＋ 換行（同 profile/ 的格式）。缺檔／空檔回空字串。</summary>
        static string ReadBankFile(string iPath) => ReadBankFile(iPath, out _);

        /// <summary>
        /// 同上，但分得出「沒有綁定」與「這一瞬間讀不了」（<paramref name="oBusy"/>）。
        /// <para>🔴 TASK-0265：綁定檔由 Unity 與 senate.exe（BankAdminPage）各自 Delete→Move 換檔 ⇒
        /// 舊版 `!File.Exists ⇒ ""` 會把換檔那一瞬間讀成「沒有綁定」，而 <see cref="CopyBankRegionAll"/>
        /// 拿「新區沒有綁定」當成可以寫入的依據 ⇒ **繞過衝突守衛、覆寫別人剛設的帳戶**（錢進哪個帳戶就換了）。</para>
        /// </summary>
        static string ReadBankFile(string iPath, out bool oBusy)
        {
            oBusy = false;
            if (UCL_AtomicFileRead.TryReadAllText(iPath, out string aText, out UCL_FileReadState aState))
                return aText.Trim();
            if (aState == UCL_FileReadState.Busy)
            {
                oBusy = true;
                Debug.LogWarning(UCL_AtomicFileRead.DescribeBusy(iPath) + " ⇒ 綁定讀成「不知道」，⛔ 不是「沒有綁定」。");
            }
            return "";
        }


        public static bool HasOwnBankBinding(string iPersona, string iCurrencyId)
            => SCP.Core.Letters.SCP_PersonaProfile.HasOwnBankBinding(
                UCL_LettersPath.Root, iPersona, iCurrencyId);


        // ⛔ `FreezeLegacyIdentity` 已退場（2026-08-21）：它的職責是「寫 legacy 之前把 identity 欄
        //    按磁碟原值釘回」，而 **legacy 檔本身已經沒有了** —— persona 資料整合到 letters。
        //    留一支對著不存在的檔案做防護的函式，比沒有防護更糟：它看起來還在守。

        // ===========================================================
        // 區塊職責：profile 快照 —— python 端的唯一資料來源（§8.7 A＋B 拍板）。
        // 物理意義：解析單端化 —— python 不再碰原始 persona json，改讀本快照：
        //          python 讀既有快照並**在回傳值上標記** `_source="snapshot"`＋`_snapshot_at`
        //          （Tim 五輪：標記長在值上不長在 log 裡）。
        //          ⚠ 2026-10-01（TASK-0354）`Cmd_PersonaProfile` 已退場、寫入搬到 `senate cmd persona-profile`，
        //            而 Senate 那側**不刷新**本快照 ⇒ 它只在 domain reload 與 Editor 內的寫入（後台頁）之後重寫。
        // 數值影響：C# 只寫不讀（照路徑快照 .agentcommands_root.local 的成熟模式）；
        //          reload／Editor 內寫入端動作後重寫；tmp+replace 原子寫、UTF-8 無 BOM。
        //          快照是衍生快取不入版控（AgentCommands .gitignore）。
        // ===========================================================
        public static string SnapshotPath
            => Path.Combine(UCL_AgentCommandsPath.DataRoot, "AwakenInit", "_persona_profile_snapshot.json").Replace('\\', '/');

        /// <summary>重寫快照。回（成功與否, persona 數, 錯誤訊息）—— 呼叫端決定要不要大聲。</summary>
        public static (bool ok, int count, string error) WriteSnapshot()
        {
            try
            {
                var root = new JsonData();
                root["generated_at"] = SCP.Core.Letters.SCP_Morning.NowIso();
                var rf = JsonData.ParseJson("[]");
                foreach (var f in ROUTING_FIELDS) rf.Add(new JsonData(f));
                root["routing_fields"] = rf;
                var idf = JsonData.ParseJson("[]");
                foreach (var f in IDENTITY_FIELDS) idf.Add(new JsonData(f));
                root["identity_fields"] = idf;
                // 結構欄清單也帶出去 —— 型別判準的真相源在 C#（summit 拍板），
                // python 端要判斷「這欄是不是 JSON」就讀這份，不要自己再列一張。
                var sf = JsonData.ParseJson("[]");
                foreach (var f in STRUCTURED_FIELDS_ORDER) sf.Add(new JsonData(f));
                root["structured_fields"] = sf;

                var pool = JsonData.ParseJson("[]");
                var personas = new JsonData();
                int n = 0;
                foreach (var name in PoolNamesSorted())
                {
                    // ⚠ iAllowMigrate:false —— 快照是**批次匯出**不是消費端讀取。
                    //   若這裡遷移，一次 domain reload 就會把全部真人 persona 遷完
                    //   ⇒ 直接違反「Template 先測、真人不當白老鼠」（Tim 拍板的鐵律二）。
                    var jd = GetRaw(name, false);
                    if (jd == null) continue;   // 壞檔 GetRaw 已警告；快照誠實少這一位而不是塞空殼
                    pool.Add(new JsonData(name));
                    personas[name] = jd;
                    n++;
                }
                root["pool"] = pool;
                root["personas"] = personas;

                string path = SnapshotPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, root.ToJsonBeautify(), new System.Text.UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                return (true, n, "");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PersonaProfile] 快照重寫失敗：{e.Message}");
                return (false, 0, e.Message);
            }
        }

        // domain reload 後重寫一次（延後到 delayCall —— reload 當下做 IO 會拖編輯器）。
        // 失敗只警告：快照是備援，寫不出來不該讓 reload 看起來壞掉。
        [UnityEditor.InitializeOnLoadMethod]
        static void RefreshSnapshotOnReload()
        {
            UnityEditor.EditorApplication.delayCall += () => { WriteSnapshot(); };
        }


    }
}
#endif
