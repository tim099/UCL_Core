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
        //   letters 的正式入口是 `UCL_LettersPath`（python 端對偶是 `ucl_paths.letters_*`）。
        //   ⛔ 不要直接用 `UCL_AwakeningService.LettersDir`：它現在剛好是同一個值，
        //     但那是「碰巧相同」不是「同一個入口」—— letters 解析規則改了這裡不會跟著改，
        //     而且不會有人發現（路徑解析壞掉幾乎都是靜默的，最壞是找到另一棵樹上的檔）。
        public static string PersonaDir(string iPersona)
            => Path.Combine(UCL_LettersPath.PersonaDir(iPersona), DIR_NAME);
        // ===========================================================
        // 區塊職責：解析 target 的資料夾，**大小寫只差的名字要分開存**（Tim 2026-08-18 選 B）。
        //
        // 物理意義：Windows 檔案系統大小寫不敏感 ⇒ `Tim/` 與 `tim/` 是同一個資料夾，
        //          兩個 target 會被**靜默併在一起**；而 Linux/macOS 上不會。
        //          🩸 實測舊資料真的有：LY 5 組、Bar 5 組、共 217 筆事件
        //          （basecamp 對 Tim 的感情被拆成 89 筆與 5 筆兩桶）。
        //          ⇒ 併不併是**人要決定的事**，不能讓檔案系統代決 —— 目前決定是「先分開，之後再談合併」。
        //
        // 做法：資料夾裡放一個 `_target.txt` 釘住「這個資料夾屬於哪個 exact 名字」。
        //      名字對不上（大小寫不同）就換一個帶 `__<hash4>` 後綴的資料夾。
        //      後綴只依賴 **exact 名字本身**，不依賴誰先被處理
        //      ⇒ 同一批資料在不同機器上得到同一組資料夾名（冪等、跨機器一致）。
        //
        // 數值影響：可能建立資料夾與 `_target.txt`；iDryRun 時不寫，只回算出來的路徑。
        // ===========================================================
        public static string TargetDir(string iPersona, string iTarget) => TargetDir(iPersona, iTarget, false);

        public static string TargetDir(string iPersona, string iTarget, bool iDryRun)
        {
            // 先正規化 —— 之後所有路徑都用收斂後的名字，兩種寫法自然落進同一個資料夾
            string aExact = CanonicalTarget(iTarget);
            string aBase = Path.Combine(PersonaDir(iPersona), Sanitize(aExact));

            string aOwner = ReadOwner(aBase);
            if (aOwner == null)                                   // 資料夾還不存在／還沒釘 ⇒ 這個名字拿下它
            {
                if (!iDryRun) WriteOwner(aBase, aExact);
                return aBase;
            }
            if (string.Equals(aOwner, aExact, StringComparison.Ordinal)) return aBase;   // 本來就是我的

            // 名字只差大小寫（或同名不同寫法）⇒ 換一個專屬資料夾，兩邊都留著
            string aAlt = aBase + "__" + UCL_RelationshipEvent.Sha1Hex(aExact, 4);
            if (!iDryRun)
            {
                WriteOwner(aAlt, aExact);
                Debug.LogWarning($"[Relationship] target 名只差大小寫：`{aOwner}` 已占用 "
                    + $"{Path.GetFileName(aBase)}／`{aExact}` 改用 {Path.GetFileName(aAlt)}"
                    + "（刻意分開保存，合併與否另案處理）。");
            }
            return aAlt;
        }

        // ===========================================================
        // 區塊職責：target 名正規化 —— 大小寫只差的收斂成同一個（Tim 2026-08-18）。
        //
        // 規則：① 有同名 persona（大小寫不論）⇒ **以 persona 的寫法為準**
        //      ② 沒有對應 persona ⇒ **預設大寫開頭**
        //
        // 物理意義：舊資料把同一個人拆成兩桶（`Tim` 89 筆 / `tim` 5 筆）——
        //          那不是兩個人，是兩種打字方式。正規化把它們併回一個人。
        //          ⚠ 併回去**會改變分數**：兩桶的事件流合起來重算，
        //            那正是修正的目的（原本的分數是被拆開之後的局部值）。
        //
        // 🩸 規則 ① 不是「一律大寫開頭」：`Zeta`/`zeta` 收斂成**小寫** `zeta`，
        //   因為 persona 註冊表裡它就是小寫。實測 5 組衝突有 1 組走這條 ——
        //   要是我圖省事寫成「一律 Capitalize」，那一組會被改成一個不存在的寫法。
        //
        // 數值影響：純字串；已知名字集合快取一次（Editor 生命週期內不重掃）。
        // ===========================================================
        static HashSet<string> s_Known;

        static HashSet<string> KnownNames()
        {
            if (s_Known != null) return s_Known;
            s_Known = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                // 🥇 主來源：letters/ —— 一個人的櫃子就是他存在的證據。
                string aLetters = UCL_LettersPath.Root;
                if (Directory.Exists(aLetters))
                    foreach (var d in Directory.GetDirectories(aLetters))
                        s_Known.Add(Path.GetFileName(d));

                // 🥈 過渡期的次要來源：AwakenInit/personas —— Tim 2026-08-18 說它之後會遷進 letters。
                //    ⚠ 到那天**把這一段刪掉就好，規則本身不用動** ——
                //      先寫成兩段而不是混在一起，就是為了讓退場只是刪程式碼不是改邏輯。
                string aPersonas = Path.Combine(UCL_AgentCommandsPath.DataRoot, "AwakenInit", "personas");
                if (Directory.Exists(aPersonas))
                    foreach (var f in Directory.GetFiles(aPersonas, "*.json"))
                        s_Known.Add(Path.GetFileNameWithoutExtension(f));
            }
            catch (Exception e)
            {
                // 掃不到不該讓遷移停擺，但要喊 —— 空的已知集合會讓規則①失效、全部走規則②
                Debug.LogError($"[Relationship] 已知名字掃描失敗，正規化將只走「大寫開頭」：{e.Message}");
            }
            return s_Known;
        }

        /// <summary>target 名正規化。回傳規則決定的寫法；oWhy 說明依據（給報告用）。</summary>
        public static string CanonicalTarget(string iTarget, out string oWhy)
        {
            oWhy = "";
            string t = (iTarget ?? "").Trim();
            if (t.Length == 0) return t;
            var aKnown = KnownNames();
            if (aKnown.Contains(t)) { oWhy = "exact"; return t; }
            // 大小寫不論的命中 —— 多個時取 ordinal 最小者（決定性，不看誰先掃到）
            string aHit = null;
            foreach (var k in aKnown)
            {
                if (!string.Equals(k, t, StringComparison.OrdinalIgnoreCase)) continue;
                if (aHit == null || string.CompareOrdinal(k, aHit) < 0) aHit = k;
            }
            if (aHit != null) { oWhy = $"依 persona 寫法（{t} → {aHit}）"; return aHit; }
            string aCap = char.ToUpperInvariant(t[0]) + t.Substring(1);
            if (!string.Equals(aCap, t, StringComparison.Ordinal)) oWhy = $"預設大寫開頭（{t} → {aCap}）";
            return aCap;
        }

        public static string CanonicalTarget(string iTarget) => CanonicalTarget(iTarget, out _);

        const string OWNER_FILE = "_target.txt";

        static string ReadOwner(string iDir)
        {
            string f = Path.Combine(iDir, OWNER_FILE);
            if (!File.Exists(f)) return Directory.Exists(iDir) ? "" : null;   // 有夾無釘 ⇒ 回 "" 讓它被接管
            try { return File.ReadAllText(f, Encoding.UTF8).Trim(); } catch { return ""; }
        }

        static void WriteOwner(string iDir, string iExact)
        {
            Directory.CreateDirectory(iDir);
            File.WriteAllText(Path.Combine(iDir, OWNER_FILE), iExact + "\n", new UTF8Encoding(false));
        }
        public static string EventsDir(string iPersona, string iTarget)
            => Path.Combine(TargetDir(iPersona, iTarget), EVENTS);
        public static string EventsDir(string iPersona, string iTarget, bool iDryRun)
            => Path.Combine(TargetDir(iPersona, iTarget, iDryRun), EVENTS);
        public static string OpinionsDir(string iPersona, string iTarget)
            => Path.Combine(TargetDir(iPersona, iTarget), OPINIONS);
        public static string OpinionsDir(string iPersona, string iTarget, bool iDryRun)
            => Path.Combine(TargetDir(iPersona, iTarget, iDryRun), OPINIONS);
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
        // 區塊職責：寫一筆事件 —— 檔名 = 事件發生的時刻，**同名就是同一筆**。
        // 物理意義：去重是檔案系統的性質，不是一段要維護的比對程式碼（Plan §2.2）。
        //
        // ⚠ 檔名改成純 at 之後（Tim 2026-08-18），「同名必同內容」不再由檔名保證，
        //   而是由資料性質保證（實測兩專案 0 撞號）。**實測不會撞 ≠ 撞了可以靜默**：
        //   同名時比對 reason —— 相同才算重複跳過；不同就 **另存 `-b` ＋ LogError**，
        //   兩筆都留著讓人判斷。⇒ 最壞情況是多一個檔要人看，不是少一筆帳沒人知道。
        //
        // 數值影響：回 true = 真的寫了；false = 已存在且內容相同（重複）。dry run 只問不寫。
        // ===========================================================
        public static bool WriteEvent(UCL_RelationshipEvent e, bool iDryRun, out string oPath)
        {
            oPath = Path.Combine(EventsDir(e.persona, e.target, iDryRun), e.FileName());
            if (File.Exists(oPath))
            {
                string aOldReason = ReadBody(oPath);
                if (string.Equals(aOldReason, (e.reason ?? "").Trim(), StringComparison.Ordinal))
                    return false;                       // 真重複 —— 遷移的正常路徑
                // 同時戳但內容不同：不覆蓋、不丟棄，兩筆並存並且大聲喊
                oPath = oPath.Substring(0, oPath.Length - 3) + "-b.md";
                Debug.LogError($"[Relationship] ⚠ 同時戳但內容不同：{e.persona}→{e.target} @ {e.at}"
                    + $"　⇒ 另存 {Path.GetFileName(oPath)}，兩筆都保留，請人工判斷哪一筆是對的。");
                if (File.Exists(oPath)) return false;
            }
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
            oPath = Path.Combine(OpinionsDir(iPersona, iTarget, iDryRun), o.FileName());
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

        // 讀一個事件檔的正文（＝reason）。給撞號比對用。
        static string ReadBody(string iPath)
        {
            try
            {
                var sb = new StringBuilder();
                int aDash = 0;
                foreach (var ln in File.ReadAllLines(iPath, Encoding.UTF8))
                {
                    if (aDash < 2 && ln.StartsWith("---", StringComparison.Ordinal)) { aDash++; continue; }
                    if (aDash >= 2) sb.Append(ln).Append('\n');
                }
                return sb.ToString().Trim();
            }
            catch { return ""; }
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
            public int currentsWritten, openingBalances, eventsNoAt, caseCollisions;
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
            if (r.caseCollisions > 0)
                sb.Append($"🔀 **target 名大小寫收斂 {r.caseCollisions} 組** —— 已合併（見明細）。"
                          + "**這幾組的分數會與舊值不同**：原本被拆成兩桶，合併後由完整事件流重算。").Append(NL_);
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
            // key → 收進來的原始 target 寫法（>1 個代表這是大小寫合併的結果）
            var aRawNames = new Dictionary<string, HashSet<string>>();
            foreach (var (root, tag) in iSources)
            {
                aRep.sources++;
                foreach (var r in LoadLegacy(root, tag))
                {
                    string aRaw = r.target;
                    r.target = CanonicalTarget(r.target);      // 正規化後再合併，兩種寫法收成一筆
                    string k = r.persona + "|" + r.target;
                    // 記下「這個 key 收過哪些原始寫法」—— 收過兩種以上 = 它是合併來的
                    if (!aRawNames.TryGetValue(k, out var aSet)) aRawNames[k] = aSet = new HashSet<string>(StringComparer.Ordinal);
                    aSet.Add(aRaw);
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

            // ===========================================================
            // 區塊職責：target 名大小寫衝突偵測。
            // 物理意義：舊資料同一位 persona 底下同時有 `Tim` 與 `tim`（LY 5 組 / Bar 5 組 / 217 筆事件）。
            //          Windows 檔案系統大小寫不敏感 ⇒ 兩者**靜默合併**進同一個資料夾；
            //          Linux/macOS 則會是兩個資料夾 ⇒ **同一份程式在不同平台產生不同的資料**。
            //          合併多半是對的（那就是同一個人），但它必須是**被決定的**，
            //          不是被檔案系統決定的。⇒ 這裡只負責讓它顯形。
            // 數值影響：純偵測，不改任何寫入行為；數字進報告、明細進 notes。
            // ===========================================================
            {
                var aByLower = new Dictionary<string, List<string>>();
                foreach (var k in aMerged.Keys)
                {
                    int bar = k.IndexOf('|');
                    string pn = k.Substring(0, bar), tg = k.Substring(bar + 1);
                    string lk = pn + "|" + tg.ToLowerInvariant();
                    if (!aByLower.TryGetValue(lk, out var lst)) aByLower[lk] = lst = new List<string>();
                    lst.Add(tg);
                }
                foreach (var kv2 in aByLower)
                {
                    if (kv2.Value.Count <= 1) continue;
                    aRep.caseCollisions++;
                    string pn = kv2.Key.Substring(0, kv2.Key.IndexOf('|'));
                    string aCanon = CanonicalTarget(kv2.Value[0], out _);
                    aRep.notes.Add($"target 名只差大小寫：{pn} → {string.Join(" / ", kv2.Value)}"
                        + $"　⇒ 已**合併**成 `{aCanon}`（規則：有同名 persona 就依 persona 的寫法，"
                        + "否則大寫開頭）。兩桶的事件流合起來重算，分數會與舊值不同。");
                }
            }

            string aNow = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            foreach (var kv in aMerged)
            {
                var r = kv.Value;
                aRep.pairs++;

                foreach (var e in r.events)
                {
                    e.target = r.target;                       // 檔內的 target 與資料夾一致
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
                // ⛔ 被合併的配對**不算期初餘額**：舊存值只來自其中一桶，
                //    而重算用的是兩桶合起來的事件流 ⇒ 兩者的差是「另一桶的貢獻」不是「遺失的歷史」。
                //    填進去等於把合併的效果抵銷掉 —— 做了合併又假裝沒做。
                bool aWasMerged = aRawNames.TryGetValue(kv.Key, out var aRaws) && aRaws.Count > 1;
                if (aWasMerged)
                    aRep.notes.Add($"{r.persona} → {r.target}：由 {string.Join(" / ", aRaws)} 合併而來，"
                        + "**不套期初餘額**，分數由完整事件流重算（會與舊值不同）。");
                if (!aWasMerged && r.vector.Count > 0)
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
