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

        /// <summary>整份 persona 檔（過渡期＝舊檔內容）。不存在回 null；壞檔回 null 並警告。</summary>
        public static JsonData GetRaw(string iPersona)
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
                    var jd = GetRaw(name);
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
