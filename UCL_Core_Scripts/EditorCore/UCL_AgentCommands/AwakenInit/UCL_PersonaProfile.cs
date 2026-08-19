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
        /// <summary>persona 檔目錄 —— 委派給 UCL_AwakeningService（可 override 的 DataRoot）。</summary>
        public static string PersonasDir => Awakening.UCL_AwakeningService.PersonasDir;

        // 欄位分類（§8.3 拍板）—— 與 python _lib/persona_profile.py 的同名常數**兩端同步義務**。
        // 讓分類在兩端都是可被編譯器／搜尋找到的東西，不是註解裡的約定（紅隊 seq 12274 洞①）。
        // email 歸 identity：個人信箱是人的署名不是專案的路由（Tim §8.3 二輪拍板；紅隊題①對出初版錯置）。
        public static readonly string[] ROUTING_FIELDS = { "agent", "model", "actual_agent" };
        public static readonly string[] IDENTITY_FIELDS = { "layer_role", "forked_from", "fork_lineage",
            "forked_at", "created_at", "identity_vector", "vector_history", "email" };

        static HashSet<string> s_NamesCache;
        static long s_NamesCacheMtime = -1;

        /// <summary>
        /// persona pool 名單（檔名去副檔名；跳過 _ / . 前綴）。dir-mtime 快取。
        /// ⚠「有哪些 persona」目前的權威來源 —— 不要掃 letters/ 目錄（有幽靈目錄）也不要各自 glob。
        /// 目錄不存在或讀取失敗回空集合（呼叫端自行決定降級行為）。
        /// </summary>
        public static HashSet<string> PoolNames()
        {
            string dir = PersonasDir;
            long mtime;
            try { mtime = Directory.Exists(dir) ? Directory.GetLastWriteTimeUtc(dir).Ticks : -1L; }
            catch { mtime = -1L; }
            if (mtime == s_NamesCacheMtime && s_NamesCache != null) return s_NamesCache;

            var set = new HashSet<string>();
            try
            {
                if (Directory.Exists(dir))
                {
                    foreach (var f in Directory.GetFiles(dir, "*.json"))
                    {
                        string name = Path.GetFileNameWithoutExtension(f);
                        if (name.StartsWith("_") || name.StartsWith(".")) continue;
                        set.Add(name);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PersonaProfile] pool 掃描失敗：{e.Message}");
            }
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
               && File.Exists(Path.Combine(PersonasDir, iPersona + ".json"));

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
            var jd = ParseLegacy(iPersona);
            if (jd == null) return null;
            return MergeProfile(iPersona, jd, iAllowMigrate);
        }

        /// <summary>只讀 legacy 舊源（`AwakenInit/personas/<p>.json`），不疊 profile/、不遷移。</summary>
        static JsonData ParseLegacy(string iPersona)
        {
            if (string.IsNullOrEmpty(iPersona)) return null;
            string path = Path.Combine(PersonasDir, iPersona + ".json");
            if (!File.Exists(path)) return null;
            try
            {
                var jd = JsonData.ParseJson(File.ReadAllText(path));
                return (jd != null && jd.IsObject) ? jd : null;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PersonaProfile] {iPersona}.json 解析失敗：{e.Message}");
                return null;
            }
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
        static readonly HashSet<string> STRUCTURED_FIELDS =
            new HashSet<string> { "identity_vector", "vector_history", "fork_lineage" };
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
        // 區塊職責：遷移放行閘 —— **哪些 persona 現在可以被自動遷移**。
        // 物理意義：Tim 拍板的鐵律二是「每批功能先用 Template 實測，真人不當白老鼠」。
        //          而 `GetRaw` 是熱路徑：沒有這道閘，**第一次 domain reload 就會把 21 個真人全遷完** ——
        //          在任何人跑過一次 Template 驗收之前。那不是「快」，那是把測試階段跳過去。
        // ⚠ 這道閘刻意是**明擺著的靜態欄位**而不是設定檔：要放行真人得有人手動翻，
        //   而翻的人看得到自己在翻什麼（搜 MigrateAllPersonas 就找得到所有現場）。
        // 📌 副作用（已在酒館 seq 12454 對 summit 講明）：§8.4 的「legacy 欄數歸零」
        //   不會靠快照 sweep 自己歸零 ⇒ Phase 3 之前需要一次**顯式 sweep**
        //   （或那時把 WriteSnapshot 翻成允許遷移）。選這條的理由是少寫、可逆、不違反鐵律二。
        // ===========================================================
        public static bool MigrateAllPersonas = false;

        static readonly HashSet<string> MIGRATION_ALLOWLIST = new HashSet<string> { "Template" };

        /// <summary>這個 persona 現在可不可以被自動遷移。</summary>
        public static bool MayMigrate(string iPersona)
            => !string.IsNullOrEmpty(iPersona)
               && (MigrateAllPersonas || MIGRATION_ALLOWLIST.Contains(iPersona));

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
        // 區塊職責：讓 legacy 舊源在 Phase 1 之後**只出不進**（§8.4 鐵則）。
        // 物理意義：`WriteRaw` 的呼叫端（morning patch-write／goodnight ×2）形狀都是
        //          `GetRaw → 改活體欄 → WriteRaw 整檔`。合併層上線後那個「整檔」裡的
        //          identity 欄**已經是 profile/ 的值** ⇒ 原樣寫回去就是回寫舊源，
        //          而且完全靜默（兩邊都變成活的，BUG-6 的形狀換個位置重演）。
        // ⇒ 由接縫強制：寫 legacy 之前把 identity 欄按**磁碟上的 legacy 原值**釘回，
        //   legacy 沒有那個 key 就從 payload 移除。**不靠呼叫端記得**（記得是會過期的）。
        // 📌 建人（onDisk == null）原樣放行 —— 那是 legacy 檔的誕生，Phase 1 不改建人路徑
        //   （routing 表同步登記留給 Phase 2，summit 拍板）。
        // 數值影響：多一次 legacy 檔解析（只在寫入路徑，不在熱讀路徑）。
        //          同時剝掉衍生欄 `_field_sources` —— 它是合併層算出來的，不該落地。
        // ===========================================================
        static void FreezeLegacyIdentity(string iPersona, JsonData iFull)
        {
            iFull.Remove(FIELD_SOURCES_KEY);

            var aOnDisk = ParseLegacy(iPersona);
            if (aOnDisk == null) return;                         // 建人：legacy 檔還不存在，原樣放行
            foreach (var f in IDENTITY_FIELDS)
            {
                if (aOnDisk.Contains(f)) iFull[f] = aOnDisk[f];
                else iFull.Remove(f);
            }
        }

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

        /// <summary>
        /// 整檔寫入（建人也走這裡：目標檔不存在＝新建）。actor / reason 必填，空值不寫直接回錯。
        /// <paramref name="iChangedFields"/>＝這次動了哪些欄（審計用；建檔傳 "create"）。
        /// </summary>
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
            FreezeLegacyIdentity(iPersona, iFull);
            try
            {
                string path = Path.Combine(PersonasDir, iPersona + ".json");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, iFull.ToJsonBeautify(), new System.Text.UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                AppendAudit(iPersona, iChangedFields ?? "", iActor, iReason);
                WriteSnapshot();
                return true;
            }
            catch (Exception e)
            {
                oError = e.Message;
                return false;
            }
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
            if (ParseLegacy(iPersona) == null)
            { oError = $"persona 檔不存在或解析失敗：{iPersona}"; return false; }

            if (IsIdentityField(iField))
                return WriteProfileField(iPersona, iField, new JsonData(iValue ?? ""),
                    iActor, iReason, true, out oError);

            var jd = ParseLegacy(iPersona);                  // 非 identity 欄：patch legacy（不疊 profile/，避免把合併值寫回去）
            jd[iField] = new JsonData(iValue ?? "");
            return WriteRaw(iPersona, jd, iActor, iReason, iField, out oError);
        }
    }
}
#endif
