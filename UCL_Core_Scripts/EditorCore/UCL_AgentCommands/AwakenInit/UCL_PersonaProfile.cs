// 區塊職責：persona 身分／路由欄位的**唯一讀取入口**（C# 端；對側 = python _lib/persona_profile.py）。
// 物理意義：退場案（Plan_Persona_Registry_Retirement §4 Phase 0）要把欄位拆家 —— 消費端若各自
//          讀 AwakenInit/personas/*.json，每動一次家 32 支都要改；先把讀取收斂到這裡，
//          之後每一期（含 Phase 1 read-through lazy migration）都只改本檔。
// 數值影響：現階段資料源仍是 AwakenInit/personas/<p>.json（唯讀，不寫；寫入端仍是
//          UCL_PersonaAgentAdminPage / UCL_AwakeningService 的 patch-write）。
//          PoolNames 帶 dir-mtime 快取（沿 UCL_ChatTavernIO 舊實作 —— 每筆 post 都會查白名單）。
//          壞檔略過但 LogWarning（靜默跳過會讓「檔壞了」跟「沒這個人」同形）。
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
            string dir = UCL_LettersPath.Root;
            long mtime;
            try { mtime = Directory.Exists(dir) ? Directory.GetLastWriteTimeUtc(dir).Ticks : -1L; }
            catch { mtime = -1L; }
            if (mtime == s_NamesCacheMtime && s_NamesCache != null) return s_NamesCache;

            var set = new HashSet<string>();
            try
            {
                if (Directory.Exists(dir))
                {
                    foreach (var d in Directory.GetDirectories(dir))
                    {
                        string name = Path.GetFileName(d);
                        if (name.StartsWith("_") || name.StartsWith(".")) continue;
                        if (!Directory.Exists(Path.Combine(d, UCL_LettersPath.ProfileDirName))) continue;
                        set.Add(name);
                    }
                }
                else Debug.LogError($"[PersonaProfile] letters 根目錄不存在：`{dir}` —— pool 名單會是空的");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PersonaProfile] pool 掃描失敗：{e.Message}");
            }
            if (set.Count == 0)
                Debug.LogError("[PersonaProfile] pool 名單掃到 **0 位** —— 幾乎不可能是真的："
                             + $"要嘛 letters 根路徑錯（`{dir}`），要嘛 submodule 沒 init（空目錄沒有 profile/）。"
                             + "此時錢與登入都會查無此人，而下游多半只會靜靜地少一個人。");
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
            => !string.IsNullOrEmpty(iPersona)
               && !iPersona.StartsWith("_") && !iPersona.StartsWith(".")
               && Directory.Exists(UCL_LettersPath.ProfileDir(iPersona));

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
            var jd = BuildPersonaRaw(iPersona);
            if (jd == null) return null;
            return MergeProfile(iPersona, jd, iAllowMigrate);
        }

        // ===========================================================
        // 區塊職責：persona 的**非 profile 欄**組裝 —— 真相源全部在 `letters/<persona>/`。
        // 物理意義：`AwakenInit/personas/<p>.json` 於 2026-08-21 退場（Tim 拍板：persona 相關資料
        //          整合到 letters）。本函式回的是「除了 profile/ 以外」那些欄，MergeProfile 再把
        //          profile/ 疊上去 ⇒ 30 幾個既有讀取端一行都不必改。
        //   · `agent`（＝帳號 id）← `bank/<本專案區域>.md`。實測 21/21 與舊 registry 的 agent 欄
        //     逐字相同 ⇒ 這不是換語意，是拿掉重複的那一份。⚠ 沒綁定要出聲：靜默回空會讓錢落央行。
        //   · `status` / `last_active` ← **lock**（`_session/_persona_<p>.json`）。
        //     舊 registry 的 status 欄是快取，而登入路徑早就寫著「registry 說 online 但查無 lock
        //     ⇒ 以 lock 為準」—— 既然結論永遠是 lock，那個欄位就不該存在。
        //   · `wake_count` ← `wakes/` 的收尾信數（在線＝本次還沒寫信 ⇒ +1）。
        //   · `last_consolidated_wake` / `_at` ← `longterm/wake_<a>-<b>.md` 檔名與 frontmatter
        //     （BUG-4 就是那個快取落後而磁碟沒落後）。
        // 數值影響：純唯讀。`profile/` 不存在 ⇒ 回 null（＝查無此人，與 Exists 同一套判準）。
        // ===========================================================
        static JsonData BuildPersonaRaw(string iPersona)
        {
            if (!Exists(iPersona)) return null;
            var jd = new JsonData();

            string aRegion = Treasury.UCL_CentralBankSettings.CurrencyId;
            string aAgent = GetBankAccount(iPersona, aRegion, out string aBankSrc, out string aBankNote);
            if (!string.IsNullOrEmpty(aAgent))
            {
                jd["agent"] = new JsonData(aAgent);
                if (!string.Equals(aBankSrc, aRegion, StringComparison.Ordinal))
                    Debug.LogWarning($"[PersonaProfile] {iPersona} 的 agent 借用了別區的綁定"
                                   + $"（本區 {aRegion} 沒有宣告，來源 {aBankSrc}）：{aBankNote}");
            }
            else
            {
                // 不填空字串頂替：下游 bank 解析拿到空 agent 會落央行，而那是一個看起來合理的處置
                // 掛在錯誤的原因上（真正的原因是「這個人沒有本區綁定」）。
                Debug.LogWarning($"[PersonaProfile] {iPersona} 在區域 {aRegion} 查無帳號綁定"
                                 + $"（bank/{aRegion}.md 不存在）—— agent 欄留缺席，呼叫端請攤給人看。");
            }

            var aLock = Awakening.UCL_AwakeningService.ReadLock(iPersona);
            jd["status"] = new JsonData(aLock != null ? "online" : "offline");
            if (aLock != null && !string.IsNullOrEmpty(aLock.locked_at))
                jd["last_active"] = new JsonData(aLock.locked_at);

            // wake_count：`wakes/` 信數是既成事實；「本次編號」由 lock 蓋章的 `wake_expected` 供給。
            // ⚠ 不可寫成「在線就 +1」：收尾信寫完之後信數已經追上期望，硬加 1 會讓顯示值多一歲，
            //   而 sleep 端的 letter 閘門就是拿這兩個數在對帳（2026-08-21 實測恆擋）。
            int aLetters = Awakening.UCL_AwakeningService.WakeLetterCount(iPersona);
            int aExpected = aLock?.wake_expected ?? 0;
            jd["wake_count"] = new JsonData(aExpected > aLetters ? aExpected : aLetters);

            var (aSpanEnd, aAt) = Awakening.UCL_AwakeningService.MaxDigestSpan(iPersona);
            if (aSpanEnd > 0)
            {
                jd["last_consolidated_wake"] = new JsonData(aSpanEnd);
                if (!string.IsNullOrEmpty(aAt)) jd["last_consolidated_at"] = new JsonData(aAt);
            }
            return jd;
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

        /// <summary>結構欄的期望 JSON 種類 —— 用來擋「parse 過了但形狀不對」。</summary>
        enum StructKind { NumberArray, ObjectArray, StringArray }

        static StructKind KindOf(string iField)
        {
            if (iField == "vector_history") return StructKind.ObjectArray;
            if (iField == "fork_lineage") return StructKind.StringArray;
            return StructKind.NumberArray;              // identity_vector
        }
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

        /// <summary>把 `profile/` 疊到 legacy 之上，補 `_field_sources`；必要且獲准時當場遷移。</summary>
        static JsonData MergeProfile(string iPersona, JsonData iLegacy, bool iAllowMigrate)
        {
            var aSources = new JsonData();
            foreach (var f in IDENTITY_FIELDS)
            {
                if (TryReadProfileField(iPersona, f, out var aVal))
                {
                    iLegacy[f] = aVal;                          // profile/ 為準
                    aSources[f] = new JsonData(SRC_PROFILE);
                    continue;
                }
                if (!iLegacy.Contains(f))
                {
                    // ⚠ legacy 也沒有這個 key ⇒ **缺席**，不是「空值」。
                    //   絕對不生一個空的 profile/ 檔：那會讓從來不存在的欄長出看似有資料的空檔
                    //   （Q5 拍板，酒館 seq 12452）。讓「沒有」自己有名字，不靠檔案不存在來暗示。
                    aSources[f] = new JsonData(SRC_ABSENT);
                    continue;
                }
                bool aMigrated = iAllowMigrate && MayMigrate(iPersona)
                    && WriteProfileField(iPersona, f, iLegacy[f], ACTOR_LAZY_MIGRATION,
                        "phase1 auto-migrate " + f + " from personas/" + iPersona + ".json", false, out _);
                aSources[f] = new JsonData(aMigrated ? SRC_PROFILE : SRC_LEGACY);
            }
            iLegacy[FIELD_SOURCES_KEY] = aSources;
            return iLegacy;
        }

        /// <summary>讀一個 `profile/&lt;field&gt;.md`。檔不存在或壞掉回 false（壞掉會警告 —— 靜默退 legacy 會讓壞檔跟未遷移同形）。</summary>
        static bool TryReadProfileField(string iPersona, string iField, out JsonData oValue)
        {
            oValue = null;
            string aPath = UCL_LettersPath.ProfileField(iPersona, iField);
            if (!File.Exists(aPath)) return false;
            string aText;
            try { aText = File.ReadAllText(aPath); }
            catch (Exception e)
            {
                Debug.LogWarning($"[PersonaProfile] profile/{iField}.md 讀取失敗（{iPersona}）：{e.Message}");
                return false;
            }
            aText = aText.TrimEnd('\r', '\n');

            if (STRUCTURED_FIELDS.Contains(iField))
            {
                try
                {
                    var jd = JsonData.ParseJson(aText);
                    if (jd == null)
                    {
                        Debug.LogWarning($"[PersonaProfile] profile/{iField}.md（{iPersona}）內文不是合法 JSON —— 退 legacy；請修那個檔");
                        return false;
                    }
                    oValue = jd;
                    return true;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[PersonaProfile] profile/{iField}.md（{iPersona}）JSON 解析失敗：{e.Message} —— 退 legacy");
                    return false;
                }
            }

            if (NULLABLE_SCALAR_FIELDS.Contains(iField) && aText.Length == 0)
            {
                oValue = new JsonData();                         // JsonType.None ⇒ 序列化成 null
                return true;
            }
            oValue = new JsonData(aText);
            return true;
        }

        // ===========================================================
        // 區塊職責：結構欄寫入值的解析與形狀驗證（§8.2 一欄一檔的 JSON 那三欄）。
        // 物理意義：`op=set` 收到的永遠是字串。結構欄要的是 JSON ——
        //          而「parse 失敗就當字串存起來」會讓 `identity_vector` 變成一個**長得像陣列的字串**，
        //          讀回來型別不對、下游拿它做數值運算才炸，離現場很遠。
        //          ⇒ **parse 失敗＝fail-loud；形狀不符＝fail-loud。絕不退存字串**（summit 拍板 A）。
        // ⚠ 空陣列 `[]` 是合法值 —— 實測 21 個 persona 的 `fork_lineage` 全是 `[]`，
        //   把空陣列當「沒填」擋掉會擋掉現存的多數資料（這正是 BUG-15 那種形狀，別再犯）。
        // 數值影響：純解析＋逐元素檢查，不碰 IO。
        // ===========================================================
        static bool ParseStructuredValue(string iField, string iValue, out JsonData oValue, out string oError)
        {
            oValue = null;
            oError = "";
            string aText = (iValue ?? "").Trim();
            if (aText.Length == 0)
            {
                oError = $"{iField} 是結構欄，值不能是空的 —— 空陣列請顯式給 `[]`"
                       + "（空字串與空陣列是兩件事，不猜）";
                return false;
            }

            JsonData aJd;
            try { aJd = JsonData.ParseJson(aText); }
            catch (Exception e)
            {
                oError = $"{iField} 是結構欄，值必須是合法 JSON —— parse 失敗：{e.Message}"
                       + "（不會退存成字串：那會變成一個長得像陣列的字串，讀回來才炸）";
                return false;
            }
            if (aJd == null || !aJd.IsArray)
            {
                oError = $"{iField} 是結構欄，值必須是 JSON **陣列**（收到的不是陣列）";
                return false;
            }

            var aKind = KindOf(iField);
            for (int i = 0; i < aJd.Count; i++)
            {
                var e = aJd[i];
                bool ok;
                string want;
                switch (aKind)
                {
                    case StructKind.ObjectArray: ok = e != null && e.IsObject; want = "物件"; break;
                    case StructKind.StringArray: ok = e != null && e.IsString; want = "字串"; break;
                    default:
                        ok = e != null && (e.IsInt || e.IsLong || e.IsDouble);
                        want = "數字";
                        break;
                }
                if (!ok)
                {
                    oError = $"{iField} 的第 {i} 個元素不是{want}"
                           + $"（本欄要求：陣列的每個元素都是{want}）";
                    return false;
                }
            }
            oValue = aJd;
            return true;
        }

        /// <summary>
        /// 寫一個 `profile/&lt;field&gt;.md`（原子寫）＋審計一行。actor / reason 必填（§8.6 同一條規矩）。
        /// <paramref name="iRefreshSnapshot"/>=false 給 <see cref="MergeProfile"/> 用 —— 它自己就跑在讀取路徑上，
        /// 在那裡刷快照會再繞回 GetRaw（每遷一欄就把全 pool 重解析一次）。
        /// </summary>
        public static bool WriteProfileField(string iPersona, string iField, JsonData iValue,
            string iActor, string iReason, bool iRefreshSnapshot, out string oError)
        {
            oError = "";
            if (string.IsNullOrWhiteSpace(iPersona)) { oError = "persona 必填"; return false; }
            if (string.IsNullOrWhiteSpace(iField)) { oError = "field 必填"; return false; }
            if (!IsIdentityField(iField))
            { oError = $"{iField} 不是 identity 欄 —— profile/ 只收身分欄（§8.3）"; return false; }
            if (string.IsNullOrWhiteSpace(iActor) || string.IsNullOrWhiteSpace(iReason))
            {
                oError = "actor 與 reason 必填（§8.6）—— 寫入要能回答「是誰、憑什麼」；匿名寫入不收";
                return false;
            }

            string aBody;
            if (STRUCTURED_FIELDS.Contains(iField))
                aBody = iValue == null ? "null" : iValue.ToJsonBeautify();
            else if (iValue == null || iValue.JsonType == JsonType.None)
                aBody = "";                                      // 空檔＝null（僅 NULLABLE_SCALAR_FIELDS 讀回為 null）
            else
                aBody = iValue.IsString ? iValue.GetString() : iValue.ToJson();

            try
            {
                string aPath = UCL_LettersPath.ProfileField(iPersona, iField);
                Directory.CreateDirectory(Path.GetDirectoryName(aPath));
                string aTmp = aPath + ".tmp";
                File.WriteAllText(aTmp, aBody + "\n", new System.Text.UTF8Encoding(false));
                if (File.Exists(aPath)) File.Delete(aPath);
                File.Move(aTmp, aPath);
            }
            catch (Exception e)
            {
                oError = e.Message;
                return false;
            }
            AppendAudit(iPersona, "profile/" + iField, iActor, iReason);
            if (iRefreshSnapshot) WriteSnapshot();
            return true;
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
        //          寫：**只准寫本區那一個檔**。不清理、不觸碰其他區域的檔 ——
        //          那是別的專案的綁定，刪掉的症狀是對方下次登入「沒有綁定」，而原因指不到這裡。
        // ⚠ `iCurrencyId` 由呼叫端提供（`UCL_CentralBankSettings.CurrencyId`），本接縫**不自己去問** ——
        //   低層接縫反向依賴 Treasury 設定會讓依賴方向反過來，而且測試時無法餵不同區域。
        // ===========================================================

        /// <summary>`GetBankAccount` 的來源標記：本區命中時＝該區域 ID；跨區借用時＝借出的區域 ID。</summary>
        public const string BankSourceAbsent = "absent";
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
        {
            oSource = BankSourceAbsent; oNote = "";
            if (string.IsNullOrWhiteSpace(iPersona) || string.IsNullOrWhiteSpace(iCurrencyId)) return "";

            // ① 本區
            string aOwn = ReadBankFile(UCL_LettersPath.BankField(iPersona, iCurrencyId));
            if (!string.IsNullOrEmpty(aOwn)) { oSource = iCurrencyId; return aOwn; }

            // ② 其他區域（跨區借用）
            string aDir = UCL_LettersPath.BankDir(iPersona);
            if (!Directory.Exists(aDir)) return "";
            var aHits = new List<KeyValuePair<string, string>>();
            string[] aFiles;
            try { aFiles = Directory.GetFiles(aDir, "*.md"); }
            catch (Exception e)
            {
                // 讀不到要出聲：靜默回空會把「讀取失敗」講成「沒有綁定」，
                // 而後者的處置是落央行 —— 一個看起來合理的錯誤處置，掛在錯誤的原因上。
                Debug.LogWarning($"[PersonaProfile] 掃 bank/ 失敗（{iPersona}）：{e.Message}");
                return "";
            }
            foreach (var f in aFiles)
            {
                string aRegion = Path.GetFileNameWithoutExtension(f);
                if (string.Equals(aRegion, iCurrencyId, StringComparison.Ordinal)) continue;
                string v = ReadBankFile(f);
                if (!string.IsNullOrEmpty(v)) aHits.Add(new KeyValuePair<string, string>(aRegion, v));
            }
            if (aHits.Count == 1)
            {
                oSource = aHits[0].Key;
                oNote = $"本區（{iCurrencyId}）無綁定，借用區域 `{aHits[0].Key}` 的帳號";
                return aHits[0].Value;
            }
            if (aHits.Count > 1)
            {
                // 不挑一個 —— 判準同 §8.1 撞名：這裡不替你挑。
                var aList = new List<string>();
                foreach (var kv in aHits) aList.Add($"{kv.Key}={kv.Value}");
                oSource = BankSourceAmbiguous;
                oNote = $"本區（{iCurrencyId}）無綁定，而其他區域有 {aHits.Count} 個候選："
                      + string.Join("／", aList.ToArray()) + " —— 拒絕挑選，請顯式指定";
            }
            return "";
        }

        /// <summary>讀一個綁定檔：裸值 ＋ 換行（同 profile/ 的格式）。缺檔／空檔回空字串。</summary>
        static string ReadBankFile(string iPath)
        {
            try
            {
                if (!File.Exists(iPath)) return "";
                return File.ReadAllText(iPath).Trim();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PersonaProfile] 讀綁定檔失敗（{iPath}）：{e.Message}");
                return "";
            }
        }

        /// <summary>
        /// 寫 persona 在**本區**的綁定（原子寫）＋審計一行。actor / reason 必填（§8.6 同一條規矩）。
        /// </summary>
        public static bool WriteBankAccount(string iPersona, string iCurrencyId, string iAccount,
            string iActor, string iReason, out string oError)
        {
            oError = "";
            if (string.IsNullOrWhiteSpace(iPersona)) { oError = "persona 必填"; return false; }
            if (string.IsNullOrWhiteSpace(iCurrencyId)) { oError = "currencyId 必填"; return false; }
            if (string.IsNullOrWhiteSpace(iAccount)) { oError = "account 必填 —— 要清空綁定請刪檔（同 BUG-16 的三態問題，不在本接縫解）"; return false; }
            if (string.IsNullOrWhiteSpace(iActor) || string.IsNullOrWhiteSpace(iReason))
            {
                oError = "actor 與 reason 必填（§8.6）—— 寫入要能回答「是誰、憑什麼」；匿名寫入不收";
                return false;
            }
            string aAccount = iAccount.Trim();
            if (aAccount.IndexOf('\n') >= 0 || aAccount.IndexOf('\r') >= 0)
            { oError = "account 不可含換行 —— 一檔一值"; return false; }

            try
            {
                string aPath = UCL_LettersPath.BankField(iPersona, iCurrencyId);
                Directory.CreateDirectory(Path.GetDirectoryName(aPath));
                string aTmp = aPath + ".tmp";
                File.WriteAllText(aTmp, aAccount + "\n", new System.Text.UTF8Encoding(false));
                if (File.Exists(aPath)) File.Delete(aPath);
                File.Move(aTmp, aPath);
            }
            catch (Exception e)
            {
                oError = e.Message;
                return false;
            }
            AppendAudit(iPersona, UCL_LettersPath.BankDirName + "/" + iCurrencyId, iActor, iReason);
            return true;
        }

        // ===========================================================
        // 區塊職責：**換區重綁** —— 區域 ID 改名時把全體綁定從舊區搬到新區。
        // 物理意義：Tim 2026-08-20 拍板 —— 後台改貨幣 ID 應**自動觸發**所有人重綁
        //          （原本綁在 Ducat 的帳號自動綁到 Florin），
        //          **除非新區已經有綁定 ⇒ 報錯**（那狀況要避免，不是要挑一個）。
        //   📌 這推翻了我先前寫在 UCL_AutoCommitPage／後台面板註解裡的邊界
        //     （「這裡不自動改名 letters 底下的檔」）—— Tim 的判準優先：
        //     改 ID 之後留下一地對不上鍵的檔，等於全員靜默落央行，那比批次寫入危險。
        // 數值影響：**刻意做成三段、且每一段之後的中間狀態都是可用的**（monotonic）：
        //          ① 預檢衝突（不寫） ② 複製到新區（舊檔還在 ⇒ 此刻兩邊都有，新區還沒生效）
        //          ③ 呼叫端翻設定 ④ 刪舊區。
        //          任何一段失敗都停在「至少有一個檔存在」的狀態，而跨區借用會接住它並出聲。
        //          ⇒ 最壞情況是「吵」，不是「查不到綁定」。
        // ⚠ 同值視為**已完成**而不是衝突：批次做一半之後必須能重跑，
        //   否則它自己成功的那一半會擋住自己的復原路。**不同值才是衝突。**
        // ===========================================================

        /// <summary>該 persona 在該區域**自己**有沒有綁定（跨區借用不算）。</summary>
        public static bool HasOwnBankBinding(string iPersona, string iCurrencyId)
            => !string.IsNullOrEmpty(ReadBankFile(UCL_LettersPath.BankField(iPersona, iCurrencyId)));

        /// <summary>刪一個區域的綁定檔（＋審計）。檔不存在視為成功（idempotent）。</summary>
        /// <remarks>
        /// 刪除是**唯一**能把綁定還原成「不存在」的手段（同 BUG-16 的三態問題）——
        /// 所以它必須有審計，否則「誰把某人的綁定弄掉了」這件事沒有任何紀錄。
        /// </remarks>
        // ==========================================================
        // 區塊職責：agent id 改名 —— 綁定檔與 persona.agent **同時**改，一邊都不能落單。
        // 物理意義：agent id 與帳號 id 合一（Tim 2026-08-20 拍板）的執行核心。
        //          綁定檔（letters/<p>/bank/<區>.md）與 registry 的 persona.agent 是同一件事的兩份記載
        //          （實測 2026-08-20 為 21/21 一致）—— 只改一邊就是親手製造第一筆不一致，
        //          而不一致的兩份記載**各自都能運作、都不報錯**。
        // 數值影響：不碰 ledger、不動任何一分錢。改的只是「綁定值叫什麼」。
        //          帳戶改名（錢要跟著搬）是 ledger transfer，是另一件事、另一個入口。
        // ⚠ 這支是 Cmd（op=rename_agent）與後台遷移頁**共用**的唯一實作 ——
        //   兩個入口各寫一份的話，就會出現「CLI 驗過了而 UI 走另一條路」的經典分裂。
        // 回傳：是否全數成功（failed==0）；oReport 為逐筆明細，兩個入口都直接顯示它。
        // ==========================================================
        public static bool RenameAgent(string iFrom, string iTo, string iCurrencyId,
            string iActor, string iReason, bool iDryRun,
            out int oHit, out int oRenamed, out int oFailed, out string oReport)
        {
            oHit = 0; oRenamed = 0; oFailed = 0;
            var sb = new System.Text.StringBuilder();
            string aFrom = (iFrom ?? "").Trim();
            string aTo = (iTo ?? "").Trim();
            var pool = PoolNamesSorted();
            sb.AppendLine($"rename_agent '{aFrom}' → '{aTo}' currency={iCurrencyId} "
                + $"dry_run={(iDryRun ? 1 : 0)} pool={pool.Count}");
            int skipped = 0;
            foreach (var p in pool)
            {
                string regAgent = GetString(p, "agent", "").Trim();
                string bound = GetBankAccount(p, iCurrencyId, out string boundSrc, out _);
                bool regHit = regAgent == aFrom;
                bool bindHit = bound == aFrom && boundSrc == iCurrencyId;
                if (!regHit && !bindHit) { skipped++; continue; }
                oHit++;
                // 兩份記載不一致 —— 不寫，讓它以「失敗」的形狀留在檯面上，不替它猜哪邊才對。
                if (regHit != bindHit)
                {
                    oFailed++;
                    sb.AppendLine($"  ✗ {p}：兩份記載不一致（registry.agent='{regAgent}'、綁定='{bound}'@{boundSrc}）—— 不寫。");
                    continue;
                }
                if (iDryRun)
                {
                    sb.AppendLine($"  → {p}：綁定 '{bound}'→'{aTo}'　registry.agent '{regAgent}'→'{aTo}'");
                    continue;
                }
                if (!WriteBankAccount(p, iCurrencyId, aTo, iActor, iReason, out string bindErr))
                { oFailed++; sb.AppendLine($"  ✗ {p}：綁定檔寫入失敗 —— {bindErr}"); continue; }
                if (!SetField(p, "agent", aTo, iActor, iReason, out string regErr))
                {
                    oFailed++;
                    sb.AppendLine($"  ✗ {p}：⚠ **綁定檔已改成 '{aTo}'、registry.agent 寫入失敗** —— {regErr}"
                        + "　這一筆現在是不一致狀態，要人工收尾。");
                    continue;
                }
                // 兩邊都讀回複驗 —— 寫入成功不等於讀得到同一個值。
                string backBind = GetBankAccount(p, iCurrencyId, out string backSrc, out _);
                string backReg = GetString(p, "agent", "").Trim();
                if (backBind != aTo || backSrc != iCurrencyId || backReg != aTo)
                {
                    oFailed++;
                    sb.AppendLine($"  ✗ {p}：寫入後讀回不符（綁定 '{backBind}'@{backSrc}、registry '{backReg}'，期望皆 '{aTo}'）");
                    continue;
                }
                oRenamed++;
                sb.AppendLine($"  ✓ {p}：綁定＋registry 皆為 '{aTo}'");
            }
            sb.AppendLine($"  ⇒ 命中 {oHit}／改名 {oRenamed}／跳過 {skipped}／失敗 {oFailed}");
            oReport = sb.ToString();
            return oFailed == 0;
        }

        public static bool DeleteBankBinding(string iPersona, string iCurrencyId,
            string iActor, string iReason, out string oError)
        {
            oError = "";
            if (string.IsNullOrWhiteSpace(iPersona)) { oError = "persona 必填"; return false; }
            if (string.IsNullOrWhiteSpace(iCurrencyId)) { oError = "currencyId 必填"; return false; }
            if (string.IsNullOrWhiteSpace(iActor) || string.IsNullOrWhiteSpace(iReason))
            { oError = "actor 與 reason 必填（§8.6）"; return false; }
            try
            {
                string aPath = UCL_LettersPath.BankField(iPersona, iCurrencyId);
                if (!File.Exists(aPath)) return true;
                File.Delete(aPath);
            }
            catch (Exception e) { oError = e.Message; return false; }
            AppendAudit(iPersona, UCL_LettersPath.BankDirName + "/" + iCurrencyId + " (deleted)",
                iActor, iReason);
            return true;
        }

        /// <summary>
        /// 把全 pool 的綁定從 <paramref name="iFrom"/> 區**複製**到 <paramref name="iTo"/> 區。
        /// 不刪舊檔（刪除是另一支，見 <see cref="DeleteBankRegionAll"/>）。
        /// </summary>
        /// <param name="iDryRun">true＝只算不寫（預檢用）。</param>
        /// <param name="oConflicts">新區已有**不同值**的人數 —— 大於 0 時呼叫端應中止。</param>
        public static string CopyBankRegionAll(string iFrom, string iTo, string iActor, string iReason,
            bool iDryRun, out int oCopied, out int oSkipped, out int oConflicts, out int oFailed)
        {
            oCopied = oSkipped = oConflicts = oFailed = 0;
            var aSb = new System.Text.StringBuilder();
            aSb.AppendLine($"[PersonaProfile] CopyBankRegionAll {iFrom} → {iTo}"
                + $"（dry_run={(iDryRun ? 1 : 0)}）");
            if (string.IsNullOrWhiteSpace(iFrom) || string.IsNullOrWhiteSpace(iTo)
                || iFrom == iTo)
            {
                aSb.AppendLine("  ⛔ from／to 必填且不可相同 —— 未動任何檔");
                oFailed = 1;
                return aSb.ToString();
            }
            foreach (var p in PoolNamesSorted())
            {
                string aOld = ReadBankFile(UCL_LettersPath.BankField(p, iFrom));
                string aNew = ReadBankFile(UCL_LettersPath.BankField(p, iTo));
                if (string.IsNullOrEmpty(aOld))
                {
                    // 舊區本來就沒有 ⇒ 沒有東西可搬。新區有值也不動它（那是別人設的）。
                    oSkipped++;
                    aSb.AppendLine($"  ・{p}：舊區（{iFrom}）無綁定 —— 跳過"
                        + (string.IsNullOrEmpty(aNew) ? "" : $"（新區已有 '{aNew}'，不動）"));
                    continue;
                }
                if (!string.IsNullOrEmpty(aNew))
                {
                    if (aNew == aOld)
                    {
                        oSkipped++;
                        aSb.AppendLine($"  ○ {p}：新區已是同值 '{aNew}' —— 視為已完成");
                        continue;
                    }
                    oConflicts++;
                    aSb.AppendLine($"  ⛔ {p}：**衝突** —— 舊區（{iFrom}）='{aOld}'、"
                        + $"新區（{iTo}）已有不同值 '{aNew}'。不覆寫、不挑一個。");
                    continue;
                }
                if (iDryRun)
                {
                    oCopied++;
                    aSb.AppendLine($"  → {p}：會寫入 '{aOld}'");
                    continue;
                }
                if (!WriteBankAccount(p, iTo, aOld, iActor, iReason, out string aErr))
                {
                    oFailed++;
                    aSb.AppendLine($"  ✗ {p}：寫入失敗 —— {aErr}");
                    continue;
                }
                string aBack = ReadBankFile(UCL_LettersPath.BankField(p, iTo));
                if (aBack != aOld)
                {
                    oFailed++;
                    aSb.AppendLine($"  ✗ {p}：寫入後讀回不符（期望 '{aOld}'、實際 '{aBack}'）");
                    continue;
                }
                oCopied++;
                aSb.AppendLine($"  ✓ {p}：'{aBack}'");
            }
            aSb.AppendLine($"  ⇒ 複製 {oCopied}／跳過 {oSkipped}／衝突 {oConflicts}／失敗 {oFailed}");
            return aSb.ToString();
        }

        /// <summary>刪掉全 pool 在某區域的綁定檔（換區的最後一段）。</summary>
        public static string DeleteBankRegionAll(string iRegion, string iActor, string iReason,
            out int oDeleted, out int oFailed)
        {
            oDeleted = oFailed = 0;
            var aSb = new System.Text.StringBuilder();
            aSb.AppendLine($"[PersonaProfile] DeleteBankRegionAll {iRegion}");
            foreach (var p in PoolNamesSorted())
            {
                if (!HasOwnBankBinding(p, iRegion)) continue;
                if (!DeleteBankBinding(p, iRegion, iActor, iReason, out string aErr))
                {
                    oFailed++;
                    // 刪不掉不致命：殘留的舊區檔對別人是「另一個區域的檔」，
                    // 而規矩就是不去清別人的檔 ⇒ 它只是噪音，不是壞掉。
                    aSb.AppendLine($"  ⚠ {p}：刪除失敗（殘留舊檔，不致命）—— {aErr}");
                    continue;
                }
                oDeleted++;
                aSb.AppendLine($"  ✓ {p}：已刪 {iRegion}");
            }
            aSb.AppendLine($"  ⇒ 刪除 {oDeleted}／失敗 {oFailed}");
            return aSb.ToString();
        }

        // ⛔ `FreezeLegacyIdentity` 已退場（2026-08-21）：它的職責是「寫 legacy 之前把 identity 欄
        //    按磁碟原值釘回」，而 **legacy 檔本身已經沒有了** —— persona 資料整合到 letters。
        //    留一支對著不存在的檔案做防護的函式，比沒有防護更糟：它看起來還在守。

        // ===========================================================
        // 區塊職責：profile 快照 —— python 端的唯一資料來源（§8.7 A＋B 拍板）。
        // 物理意義：解析單端化 —— python 不再碰原始 persona json，改讀本快照：
        //          Cmd_PersonaProfile 成功＝C# 剛解析完寫好（現場值，無標記）；
        //          Cmd 跑不通（Editor 未開）＝python 讀既有快照並**在回傳值上標記**
        //          `_source="snapshot"`＋`_snapshot_at`（Tim 五輪：標記長在值上不長在 log 裡）。
        // 數值影響：C# 只寫不讀（照路徑快照 .agentcommands_root.local 的成熟模式）；
        //          reload／每次 Cmd／寫入端動作後重寫；tmp+replace 原子寫、UTF-8 無 BOM。
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
                root["generated_at"] = Awakening.UCL_AwakeningService.NowIso();
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

        // ===========================================================
        // 區塊職責：寫入接縫（§8.6，紅隊 basecamp seq 12274 ④ 的規格）——
        //          persona 檔的每一筆寫入都要能回答「是誰、什麼時候、憑什麼寫的」。
        // 物理意義：建人／fork／換綁／欄位更新出錯時的症狀都是「資料看起來很正常」，
        //          沒有 actor 欄位就只能靠 git blame 猜是哪支工具寫的。
        //          ⇒ actor 與 reason 是**必填**（空值直接 fail-loud，不寫）；
        //          每筆寫入 append 一行審計 jsonl，並順手刷新 profile 快照（§8.7）。
        // 數值影響：WriteRaw＝整檔原子寫（呼叫端 parse→改欄→交回，patch 語意留在呼叫端）；
        //          SetField＝單一純量欄便捷包裝。審計檔 append-only，不入 gitignore（它就是給人查的）。
        // ===========================================================
        public static string AuditPath
            => Path.Combine(UCL_AgentCommandsPath.DataRoot, "AwakenInit", "_persona_write_audit.jsonl").Replace('\\', '/');

        static void AppendAudit(string iPersona, string iFields, string iActor, string iReason)
        {
            try
            {
                var line = new JsonData();
                line["ts"] = Awakening.UCL_AwakeningService.NowIso();
                line["persona"] = iPersona;
                line["fields"] = iFields;
                line["actor"] = iActor;
                line["reason"] = iReason;
                File.AppendAllText(AuditPath, line.ToJson() + "\n", new System.Text.UTF8Encoding(false));
            }
            catch (Exception e)
            {
                // 審計寫不進去要大聲 —— 但不擋主寫入（資料已落地，讓它看起來失敗會引發重試風暴）
                Debug.LogError($"[PersonaProfile] 審計 append 失敗（主寫入不受影響）：{e.Message}");
            }
        }

        // ===========================================================
        // 區塊職責：**整份 payload 的分流寫入** —— persona 資料整合到 letters 之後，
        //          「整檔寫」這個動作不存在了（沒有那個檔），所以本函式改成逐欄決定去哪。
        // 物理意義：三類欄，三種處置，**都要留痕**：
        //          · identity 欄（含 model / actual_agent）→ `profile/<field>.md`
        //          · 推導欄（wake_count / status / last_active / last_consolidated_*）→ **不寫**。
        //            它們的真相源是 wakes/ 信件數、lock、longterm/ 檔名 ⇒ 寫進來只會多一份會落後的快取
        //            （BUG-4 就是那個快取落後而磁碟沒落後，於是假 OVERDUE 逼人重做已完成的濃縮）。
        //          · `agent`（＝帳號 id）→ **拒收**，要改走 `op=set_bank`（一區一檔的綁定，有自己的審計）。
        //            這裡不代收：代收就是第二個寫入端，而錢的欄位不該有第二個寫入端。
        // 🩸 為什麼不靜默忽略推導欄：呼叫端（登入／晚安）現在的形狀是「GetRaw → 改活體欄 → WriteRaw」，
        //    它們**以為自己寫進去了**。靜默忽略會讓「寫了沒生效」與「寫成功」同形 ——
        //    那正是本專案這一族 bug 的標準長相。⇒ 跳過的欄位一律回報在 `oError`／審計裡（不擋主寫入）。
        // 數值影響：identity 欄逐欄原子寫 + 逐欄審計；全程沒有任何一個中央檔被建立。
        // ===========================================================
        public static bool WriteRaw(string iPersona, JsonData iFull, string iActor, string iReason,
            string iChangedFields, out string oError)
        {
            oError = "";
            if (string.IsNullOrWhiteSpace(iPersona)) { oError = "persona 必填"; return false; }
            if (string.IsNullOrWhiteSpace(iActor) || string.IsNullOrWhiteSpace(iReason))
            {
                oError = "actor 與 reason 必填（§8.6）—— 寫入要能回答「是誰、憑什麼」；匿名寫入不收";
                return false;
            }
            if (iFull == null || !iFull.IsObject) { oError = "內容必須是 JSON 物件"; return false; }

            iFull.Remove(FIELD_SOURCES_KEY);                 // 合併層算出來的衍生欄，不落地
            var aWritten = new List<string>();
            var aSkipped = new List<string>();
            var aRefused = new List<string>();

            foreach (var f in IDENTITY_FIELDS)
            {
                if (!iFull.Contains(f)) continue;
                if (!WriteProfileField(iPersona, f, iFull[f], iActor, iReason, false, out string aErr))
                { oError = $"profile/{f} 寫入失敗：{aErr}"; return false; }
                aWritten.Add(f);
            }
            foreach (var f in new List<string>(iFull.Keys))
            {
                if (IsIdentityField(f)) continue;
                if (string.Equals(f, "agent", StringComparison.Ordinal)) { aRefused.Add(f); continue; }
                aSkipped.Add(f);
            }

            AppendAudit(iPersona, "profile:[" + string.Join(",", aWritten) + "]"
                        + (aSkipped.Count > 0 ? " skipped(推導欄):[" + string.Join(",", aSkipped) + "]" : "")
                        + (aRefused.Count > 0 ? " refused(走 set_bank):[" + string.Join(",", aRefused) + "]" : ""),
                        iActor, iReason);
            WriteSnapshot();

            if (aRefused.Count > 0)
                oError = $"以下欄位**未寫入**（帳號欄要走 op=set_bank）：{string.Join(",", aRefused)}";
            else if (aWritten.Count == 0 && aSkipped.Count > 0)
                oError = $"沒有任何 identity 欄可寫；本次 payload 只含推導欄（不儲存）：{string.Join(",", aSkipped)}";
            return true;
        }

        /// <summary>
        /// 單一純量欄寫入（patch：其餘欄原樣保留）。persona 檔不存在＝錯（建人走 WriteRaw）。
        /// ⚠ **identity 欄（§8.3 不綁專案組）改寫 `profile/<field>.md`，不碰 legacy**（§8.4：舊源只出不進）。
        /// </summary>
        public static bool SetField(string iPersona, string iField, string iValue,
            string iActor, string iReason, out string oError)
        {
            oError = "";
            if (string.IsNullOrWhiteSpace(iField)) { oError = "field 必填"; return false; }
            if (!Exists(iPersona))
            { oError = $"查無此 persona（`letters/{iPersona}/profile/` 不存在）：{iPersona}"; return false; }

            if (IsIdentityField(iField))
            {
                // 型別**由欄名決定，不由值的長相決定**（summit 2026-08-19 拍板 A）：
                //   結構欄 ⇒ 必須 parse 成功且形狀相符，失敗就 fail-loud；
                //   純量欄 ⇒ 一律字面收，就算長得像 JSON 也不猜。
                // ⚠「看起來像 JSON 但被存成字串」是這條路唯一的死法 —— 焊死在這裡，不留退路。
                JsonData aVal;
                if (STRUCTURED_FIELDS.Contains(iField))
                {
                    if (!ParseStructuredValue(iField, iValue, out aVal, out oError)) return false;
                }
                else
                {
                    aVal = new JsonData(iValue ?? "");
                }
                return WriteProfileField(iPersona, iField, aVal, iActor, iReason, true, out oError);
            }

            // 非 identity 欄＝推導欄或帳號欄，**兩者都沒有儲存位置**（2026-08-21 整合到 letters 之後）。
            // ⇒ fail-loud。以前這裡會 patch 進中央 json，而那個檔已經不存在；靜默成功是最貴的回答。
            if (string.Equals(iField, "agent", StringComparison.Ordinal))
            {
                oError = "`agent`（＝帳號 id）不由本入口寫 —— 走 `Cmd_PersonaProfile op=set_bank`"
                       + "（一區一檔的綁定，有自己的審計與跨區借用判準）。";
                return false;
            }
            oError = $"`{iField}` 是推導欄（真相源在 wakes/ 信件數、lock、longterm/ 檔名）"
                   + "，不接受寫入 —— 要改就去改那個既成事實。";
            return false;
        }
    }
}
#endif
