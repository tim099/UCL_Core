// 區塊職責：relationship 的磁碟層 —— 新結構讀寫、舊 affinity 讀取、遷移（含 dry run）。
// 物理意義：letters/<persona>/relationship/ 底下的唯一寫入端；後台頁與 CLI 都走這裡。
// 數值影響：純檔案 IO。遷移刻意**只新增不刪除、不覆寫既有檔** ⇒ 冪等，跑第二次寫 0 檔。
// 設計沿革：Plan_Relationship_System.md（Tim 2026-08-18 拍板）。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UCL.Core.EditorLib.AgentCommands.Awakening;
using UCL.Core.JsonLib;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.Relationship
{
    public static class UCL_RelationshipIO
    {
        public const string DIR_NAME = "relationship";
        public const string EVENTS = "events";
        public const string OPINIONS = "opinions";

        // ⚠ 路徑一律走既有解析器 —— 各專案掛載位置不同，自推導跨專案必壞而且是靜默的。
        public static string PersonaDir(string iPersona)
            => Path.Combine(UCL_AwakeningService.LettersDir, iPersona, DIR_NAME);
        public static string TargetDir(string iPersona, string iTarget)
            => Path.Combine(PersonaDir(iPersona), Sanitize(iTarget));
        public static string EventsDir(string iPersona, string iTarget)
            => Path.Combine(TargetDir(iPersona, iTarget), EVENTS);
        public static string OpinionsDir(string iPersona, string iTarget)
            => Path.Combine(TargetDir(iPersona, iTarget), OPINIONS);
        public static string CurrentPath(string iPersona, string iTarget)
            => Path.Combine(TargetDir(iPersona, iTarget), "_current.md");

        // target 名進檔案系統前要洗 —— 目前都是 persona 名（安全），但它來自資料不是常數。
        static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "_unknown";
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Trim();
        }

        // ===========================================================
        // 區塊職責：寫一筆事件 —— **檔案已存在就跳過**，這就是全部的去重邏輯。
        // 物理意義：檔名 = at + sha1(at+reason)，同一筆事件不論來自哪個專案都算出同一個名字
        //          ⇒ 去重是檔案系統的性質，不是一段要維護的比對程式碼（Plan §2.2）。
        // 數值影響：回 true = 真的寫了；false = 已存在（重複）。dry run 只問不寫。
        // ===========================================================
        public static bool WriteEvent(UCL_RelationshipEvent e, bool iDryRun, out string oPath)
        {
            oPath = Path.Combine(EventsDir(e.persona, e.target), e.FileName());
            if (File.Exists(oPath)) return false;
            if (iDryRun) return true;
            Directory.CreateDirectory(Path.GetDirectoryName(oPath));
            var sb = new StringBuilder();
            sb.Append("---\n");
            sb.Append($"at: {e.at}\n");
            sb.Append($"persona: {e.persona}\n");
            sb.Append($"target: {e.target}\n");
            sb.Append($"source: {e.source}\n");
            sb.Append("axis_deltas:\n");
            foreach (var kv in e.axis_deltas)
                sb.Append($"  {kv.Key}: {kv.Value.ToString("0.####", CultureInfo.InvariantCulture)}\n");
            sb.Append($"surface_score_after: {e.surface_score_after}   # 歷史註記，不是事實來源\n");
            sb.Append("---\n\n");
            sb.Append(e.reason).Append('\n');
            File.WriteAllText(oPath, sb.ToString(), new UTF8Encoding(false));
            return true;
        }

        // 區塊職責：寫一則看法。
        // ⚠ `at:` 一律**顯式輸出**，沒有時戳就寫 `null` —— 省略的話下一個工具會以為
        //   「這個欄位還沒被填」而去猜一個時間，而猜出來的時間看起來跟真的一模一樣。
        public static bool WriteOpinion(string iPersona, string iTarget, UCL_RelationshipOpinion o,
            bool iDryRun, out string oPath)
        {
            oPath = Path.Combine(OpinionsDir(iPersona, iTarget), o.FileName());
            if (File.Exists(oPath)) return false;
            if (iDryRun) return true;
            Directory.CreateDirectory(Path.GetDirectoryName(oPath));
            var sb = new StringBuilder();
            sb.Append("---\n");
            sb.Append($"at: {(string.IsNullOrEmpty(o.at) ? "null   # 舊資料沒有時戳，不是漏填" : o.at)}\n");
            sb.Append($"origin: [{string.Join(", ", o.origin)}]\n");
            if (!string.IsNullOrEmpty(o.migrated_at)) sb.Append($"migrated_at: {o.migrated_at}\n");
            sb.Append("---\n\n");
            sb.Append(o.text).Append('\n');
            File.WriteAllText(oPath, sb.ToString(), new UTF8Encoding(false));
            return true;
        }

        // ===========================================================
        // 區塊職責：由磁碟上的事件重算並寫出 `_current.md`。
        // 物理意義：**存值是投影不是事實** —— 所以它任何時候都可以被刪掉重建。
        // 數值影響：讀該 target 底下所有事件檔；`recomputable` 記錄「重算是否等於舊存值」。
        // ===========================================================
        public static UCL_RelationshipCurrent RebuildCurrent(string iPersona, string iTarget,
            Dictionary<string, float> iOpening, bool iDryRun)
        {
            var aEvents = LoadEvents(iPersona, iTarget);
            var aVec = UCL_RelationshipCurrent.Recompute(aEvents, iOpening);
            var aCur = new UCL_RelationshipCurrent
            {
                target = iTarget,
                emotion_vector = aVec,
                surface_score = UCL_RelationshipAxes.SurfaceScore(aVec),
                event_count = aEvents.Count,
                opinion_count = CountOpinions(iPersona, iTarget),
                last_updated = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                opening_balance = (iOpening != null && iOpening.Count > 0) ? iOpening : null,
                recomputable = true,
            };
            aCur.tier = UCL_RelationshipAxes.Tier(aCur.surface_score);
            if (iDryRun) return aCur;

            Directory.CreateDirectory(TargetDir(iPersona, iTarget));
            var sb = new StringBuilder();
            sb.Append("---\n");
            sb.Append($"target: {aCur.target}\n");
            sb.Append("emotion_vector:\n");
            foreach (var a in UCL_RelationshipAxes.Names)
                sb.Append($"  {a}: {aCur.emotion_vector[a].ToString("0.####", CultureInfo.InvariantCulture)}\n");
            sb.Append($"surface_score: {aCur.surface_score}\n");
            sb.Append($"tier: {aCur.tier}\n");
            sb.Append($"event_count: {aCur.event_count}\n");
            sb.Append($"opinion_count: {aCur.opinion_count}\n");
            sb.Append($"last_updated: {aCur.last_updated}\n");
            sb.Append($"recomputable: {(aCur.recomputable ? "true" : "false")}\n");
            if (aCur.opening_balance == null) sb.Append("opening_balance: null\n");
            else
            {
                sb.Append("opening_balance:      # ⚠ 這一段沒有事件紀錄，由遷移反推填入\n");
                foreach (var kv in aCur.opening_balance)
                    sb.Append($"  {kv.Key}: {kv.Value.ToString("0.####", CultureInfo.InvariantCulture)}\n");
            }
            sb.Append("generated: mechanical   # 事實來源是 events/；本檔可刪除重建\n");
            sb.Append("---\n\n");
            sb.Append($"# {iPersona} → {iTarget}\n\n");
            sb.Append($"`{aCur.tier}`　surface_score **{aCur.surface_score}**　"
                      + $"事件 {aCur.event_count} 筆　看法 {aCur.opinion_count} 則\n");
            File.WriteAllText(CurrentPath(iPersona, iTarget), sb.ToString(), new UTF8Encoding(false));
            return aCur;
        }

        public static List<UCL_RelationshipEvent> LoadEvents(string iPersona, string iTarget)
        {
            var aOut = new List<UCL_RelationshipEvent>();
            string d = EventsDir(iPersona, iTarget);
            if (!Directory.Exists(d)) return aOut;
            foreach (var f in Directory.GetFiles(d, "*.md"))
            {
                var e = new UCL_RelationshipEvent { persona = iPersona, target = iTarget };
                bool aIn = false, aInDeltas = false;
                var aBody = new StringBuilder();
                bool aPastFm = false;
                foreach (var ln in File.ReadAllLines(f, Encoding.UTF8))
                {
                    if (!aPastFm && ln.StartsWith("---", StringComparison.Ordinal))
                    {
                        if (!aIn) { aIn = true; continue; }
                        aPastFm = true; continue;
                    }
                    if (aPastFm) { aBody.Append(ln).Append('\n'); continue; }
                    if (ln.StartsWith("  ", StringComparison.Ordinal) && aInDeltas)
                    {
                        int ci = ln.IndexOf(':');
                        if (ci > 0 && float.TryParse(ln.Substring(ci + 1).Trim(),
                                NumberStyles.Float, CultureInfo.InvariantCulture, out float dv))
                            e.axis_deltas[ln.Substring(0, ci).Trim()] = dv;
                        continue;
                    }
                    aInDeltas = false;
                    int c = ln.IndexOf(':');
                    if (c <= 0) continue;
                    string k = ln.Substring(0, c).Trim();
                    string v = StripComment(ln.Substring(c + 1).Trim());
                    switch (k)
                    {
                        case "at": e.at = v; break;
                        case "source": e.source = v; break;
                        case "axis_deltas": aInDeltas = true; break;
                        case "surface_score_after": int.TryParse(v, out e.surface_score_after); break;
                    }
                }
                e.reason = aBody.ToString().Trim();
                aOut.Add(e);
            }
            return aOut;
        }

        public static int CountOpinions(string iPersona, string iTarget)
        {
            string d = OpinionsDir(iPersona, iTarget);
            return Directory.Exists(d) ? Directory.GetFiles(d, "*.md").Length : 0;
        }

        static string StripComment(string v)
        {
            int i = v.IndexOf('#');
            return i < 0 ? v : v.Substring(0, i).Trim();
        }

        // ===========================================================
        // 區塊職責：讀一個專案的舊 affinity 資料（`ChatTavern/affinity/<persona>/relations.json`）。
        // 物理意義：遷移的輸入。**只讀不寫** —— 舊資料是遷移正確性唯一的對照組。
        // 數值影響：解析失敗的檔跳過並警告（不中斷整批 —— 一個壞檔不該讓 34 個 persona 都遷不了）。
        // ===========================================================
        public class LegacyRecord
        {
            public string persona = "", target = "";
            public Dictionary<string, float> vector = new();
            public int surface_score = 0;
            public List<string> opinions = new();
            public List<UCL_RelationshipEvent> events = new();
        }

        public static List<LegacyRecord> LoadLegacy(string iProjectRoot, string iSourceTag)
        {
            var aOut = new List<LegacyRecord>();
            string aDir = Path.Combine(iProjectRoot, "AgentCommands", "ChatTavern", "affinity");
            if (!Directory.Exists(aDir))
            {
                Debug.LogWarning($"[Relationship] 找不到舊 affinity 目錄，略過此來源：{aDir}");
                return aOut;
            }
            foreach (var aPersonaDir in Directory.GetDirectories(aDir))
            {
                string aPersona = Path.GetFileName(aPersonaDir);
                string aFile = Path.Combine(aPersonaDir, "relations.json");
                if (!File.Exists(aFile)) continue;
                try
                {
                    var aJson = JsonData.ParseJson(File.ReadAllText(aFile, Encoding.UTF8));
                    var aRoot = aJson.GetJsonDic();
                    var aAxes = new List<string>();
                    if (aRoot.ContainsKey("_emotion_axes"))
                    {
                        var aAxArr = aRoot["_emotion_axes"];
                        for (int i = 0; i < aAxArr.Count; i++) aAxes.Add(aAxArr[i].GetString());
                    }
                    if (aAxes.Count == 0) aAxes.AddRange(UCL_RelationshipAxes.Names);

                    if (!aRoot.ContainsKey("targets")) continue;
                    foreach (var kv in aRoot["targets"].GetJsonDic())
                    {
                      // ⚠ try 掛在 **target 層**不是檔案層：一列壞資料只該吃掉那一列，
                      //   而不是連同這位 persona 其他 target 一起消失（實測踩過）。
                      try
                      {
                        var r = new LegacyRecord { persona = aPersona, target = kv.Key };
                        var v = kv.Value.GetJsonDic();
                        if (v.ContainsKey("emotion_vector"))
                        {
                            var arr = v["emotion_vector"];
                            for (int i = 0; i < arr.Count && i < aAxes.Count; i++)
                                r.vector[aAxes[i]] = (float)arr[i].GetDouble();
                        }
                        r.surface_score = SafeInt(v, "surface_score");
                        if (v.ContainsKey("opinions"))
                        {
                            var aOps = v["opinions"];
                            for (int i = 0; i < aOps.Count; i++)
                            {
                                string t = aOps[i].GetString();
                                if (!string.IsNullOrWhiteSpace(t)) r.opinions.Add(t.Trim());
                            }
                        }
                        if (v.ContainsKey("history"))
                        {
                            var aHist = v["history"];
                            for (int hi = 0; hi < aHist.Count; hi++)
                            {
                                var h = aHist[hi].GetJsonDic();
                                var e = new UCL_RelationshipEvent
                                {
                                    persona = aPersona, target = kv.Key, source = iSourceTag,
                                    at = SafeStr(h, "at"),
                                    reason = SafeStr(h, "reason").Trim(),
                                    surface_score_after = SafeInt(h, "surface_score_after"),
                                };
                                if (h.ContainsKey("axis_deltas"))
                                    foreach (var d in h["axis_deltas"].GetJsonDic())
                                        e.axis_deltas[d.Key] = (float)d.Value.GetDouble();
                                // ⚠ 沒有 at 的事件無法產生穩定檔名 ⇒ 跳過並喊，不要編一個時間給它
                                if (string.IsNullOrEmpty(e.at))
                                {
                                    Debug.LogError($"[Relationship] {aPersona}→{kv.Key} 有一筆事件沒有 at，"
                                        + $"無法產生穩定檔名 ⇒ 略過（reason: {Trunc(e.reason, 40)}）");
                                    continue;
                                }
                                r.events.Add(e);
                            }
                        }
                        aOut.Add(r);
                      }
                      catch (Exception exT)
                      {
                          Debug.LogError($"[Relationship] 解析失敗，略過 {aPersona}→{kv.Key}"
                              + $"（同檔其他 target 不受影響）：{exT.Message}");
                      }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[Relationship] 整份解析失敗，略過 {aFile}：{ex.Message}");
                }
            }
            return aOut;
        }

        // 區塊職責：null-safe 取值 —— 舊資料裡真的有 `"surface_score_after": null`。
        // 物理意義：JsonData 對 null 值回 null 物件，直接 GetInt() 會 NRE
        //          ⇒ 整份檔案在 catch 裡被略過，而且**只留一行 warning**。
        // 數值影響：缺鍵、null、型別不符一律回 iDefault，不丟例外。
        static int SafeInt(IDictionary<string, JsonData> iDic, string iKey, int iDefault = 0)
        {
            if (iDic == null || !iDic.ContainsKey(iKey)) return iDefault;
            var v = iDic[iKey];
            if (v == null) return iDefault;
            try { return v.GetInt(); } catch { return iDefault; }
        }

        static string SafeStr(IDictionary<string, JsonData> iDic, string iKey)
        {
            if (iDic == null || !iDic.ContainsKey(iKey)) return "";
            var v = iDic[iKey];
            if (v == null) return "";
            try { return v.GetString() ?? ""; } catch { return ""; }
        }

        static string Trunc(string s, int n) => string.IsNullOrEmpty(s) || s.Length <= n ? s : s.Substring(0, n) + "…";

        // ===========================================================
        // 區塊職責：遷移 —— 把 N 個來源的舊資料合併寫進本專案的新結構。
        // 物理意義：**只新增不覆寫** ⇒ 冪等；跑第二次應該寫 0 檔，而那本身就是驗收項。
        //          去重全靠檔名（見 WriteEvent），這裡沒有任何比對邏輯。
        // 數值影響：iDryRun=true 時一個位元組都不寫，只回報數字。
        // ===========================================================
        public class MigrateReport
        {
            public int sources, pairs, eventsWritten, eventsSkipped, opinionsWritten, opinionsSkipped;
            public int currentsWritten, openingBalances, eventsNoAt;
            public List<string> notes = new();
        }

        // ===========================================================
        // 區塊職責：字串進、字串出的遷移入口 —— 後台頁按鈕與 Cmd_Invoke 驗收共用同一支。
        // 物理意義：**兩套組裝邏輯 = 兩種行為**，而畫面上跑過的跟驗收時跑的不是同一條路，
        //          那種差異只會在出事的時候才被發現。收成一支。
        // 數值影響：iSources 格式 `<專案根>|<標籤>`，**一行一個**（也吃 `;` 相容）；空 = 只用本專案。
        // 🩸 分隔符不用 `;` 當主要寫法：經 Cmd_Invoke 傳時會撞到它自己的 arg 分隔符
        //    （`args=A;B;true` 被切成三個 ⇒ "too many args"）。換行既不撞 CLI，
        //    也正好是後台頁多行輸入框天然產生的東西。
        //          iDryRun=true 一個位元組都不寫。回傳人可讀的報告字串。
        // ===========================================================
        public static string Run(string iSources, bool iDryRun)
        {
            var aList = new List<(string root, string tag)>();
            if (string.IsNullOrWhiteSpace(iSources))
            {
                aList.Add((UCL_RepoPath.RepoRoot, "local"));
            }
            else
            {
                foreach (var aPart in iSources.Split(new[] { '\n', '\r', ';' },
                             StringSplitOptions.RemoveEmptyEntries))
                {
                    string t = aPart.Trim();
                    if (t.Length == 0) continue;
                    int bar = t.IndexOf('|');
                    if (bar > 0) aList.Add((t.Substring(0, bar).Trim(), t.Substring(bar + 1).Trim()));
                    else aList.Add((t, Path.GetFileName(t.TrimEnd('/', '\\'))));
                }
            }
            var r = Migrate(aList, iDryRun);
            var sb = new StringBuilder();
            sb.Append(iDryRun ? "【乾跑 — 什麼都沒寫】" : "【已執行遷移】").Append(NL_);
            sb.Append($"來源 {r.sources} 個｜(persona,target) 配對 {r.pairs}").Append(NL_);
            sb.Append($"事件：寫入 {r.eventsWritten}　跳過（已存在＝重複）{r.eventsSkipped}").Append(NL_);
            sb.Append($"看法：寫入 {r.opinionsWritten}　跳過 {r.opinionsSkipped}").Append(NL_);
            sb.Append($"_current.md 寫入 {r.currentsWritten}　期初餘額 {r.openingBalances} 筆").Append(NL_);
            foreach (var n in r.notes) sb.Append("⚠ ").Append(n).Append(NL_);

            // 落檔 —— 遷移是一次性的破壞性動作，GUI 上的數字關掉視窗就沒了。
            try
            {
                var aLog = new StringBuilder();
                aLog.Append($"# Relationship 遷移報告　ts=`{DateTime.Now:yyyy-MM-dd HH:mm:sszzz}`（本地時間）").Append(NL_);
                aLog.Append(NL_).Append($"- 模式：{(iDryRun ? "乾跑（未寫任何檔）" : "**已執行**")}").Append(NL_);
                aLog.Append("- 來源：").Append(NL_);
                foreach (var (root, tag) in aList) aLog.Append($"  - `{root}`　（標籤 {tag}）").Append(NL_);
                aLog.Append(NL_).Append(sb.ToString());
                Directory.CreateDirectory(UCL_AgentCommandsPath.DataRoot);
                File.WriteAllText(ReportPath, aLog.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                // 落檔失敗不該讓遷移看起來失敗 —— 但要大聲，不然沒人知道紀錄沒留下
                Debug.LogError($"[Relationship] 遷移報告落檔失敗（遷移本身不受影響）：{ex.Message}");
            }
            return sb.ToString();
        }

        /// <summary>遷移報告落點（每次覆蓋）。</summary>
        public static string ReportPath
            => Path.Combine(UCL_AgentCommandsPath.DataRoot, "_last_relationship_migrate.md");

        const string NL_ = "\n";

        public static MigrateReport Migrate(IEnumerable<(string root, string tag)> iSources, bool iDryRun)
        {
            var aRep = new MigrateReport();
            // key = persona|target
            var aMerged = new Dictionary<string, LegacyRecord>();
            foreach (var (root, tag) in iSources)
            {
                aRep.sources++;
                foreach (var r in LoadLegacy(root, tag))
                {
                    string k = r.persona + "|" + r.target;
                    if (!aMerged.TryGetValue(k, out var m))
                    {
                        aMerged[k] = r;
                        continue;
                    }
                    m.events.AddRange(r.events);             // 去重在寫檔那層做，這裡照收
                    foreach (var o in r.opinions) m.opinions.Add(o);
                    // ⚠ 兩邊的 surface_score / vector 都不遷移（Plan §4.4）——
                    //   它們只是各自分支後的局部結果，合併後唯一有意義的是重算值。
                    //   但**舊存值要留著當對照**，用來判斷 recomputable / 期初餘額：
                    //   取事件多的那一邊的存值（它比較接近合併後的全量）
                    if (r.events.Count > m.events.Count - r.events.Count)
                    {
                        m.vector = r.vector; m.surface_score = r.surface_score;
                    }
                }
            }

            string aNow = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            foreach (var kv in aMerged)
            {
                var r = kv.Value;
                aRep.pairs++;

                foreach (var e in r.events)
                {
                    if (WriteEvent(e, iDryRun, out _)) aRep.eventsWritten++;
                    else aRep.eventsSkipped++;
                }

                // opinions：內容雜湊去重；origin 只能記到「哪個來源的第幾則」
                var aSeen = new HashSet<string>();
                for (int i = 0; i < r.opinions.Count; i++)
                {
                    string txt = r.opinions[i];
                    string fp = UCL_RelationshipEvent.Sha1Hex(txt, 12);
                    if (!aSeen.Add(fp)) { aRep.opinionsSkipped++; continue; }
                    var o = new UCL_RelationshipOpinion { text = txt, at = "", migrated_at = aNow };
                    o.origin.Add($"legacy#{i}");
                    if (WriteOpinion(r.persona, r.target, o, iDryRun, out _)) aRep.opinionsWritten++;
                    else aRep.opinionsSkipped++;
                }

                // 期初餘額：舊存值減「由事件重算」的差；差到可忽略就不寫
                Dictionary<string, float> aOpening = null;
                if (r.vector.Count > 0)
                {
                    var aRecomp = UCL_RelationshipCurrent.Recompute(r.events, null);
                    var aDiff = new Dictionary<string, float>();
                    foreach (var a in UCL_RelationshipAxes.Names)
                    {
                        r.vector.TryGetValue(a, out float stored);
                        float d = (float)Math.Round(stored - aRecomp[a], 4);
                        if (Math.Abs(d) >= 0.0001f) aDiff[a] = d;
                    }
                    if (aDiff.Count > 0) { aOpening = aDiff; aRep.openingBalances++; }
                }

                if (!iDryRun)
                {
                    RebuildCurrent(r.persona, r.target, aOpening, false);
                    aRep.currentsWritten++;
                }
            }
            return aRep;
        }
    }
}
#endif
