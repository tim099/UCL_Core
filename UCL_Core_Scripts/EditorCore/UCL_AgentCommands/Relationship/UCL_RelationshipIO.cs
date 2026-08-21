// 區塊職責：relationship 的磁碟層 —— 新結構讀寫。
// （舊 affinity 讀取與一次性遷移已於 2026-08-19 移除 —— 遷移完畢、來源資料已刪，史料見 git。）
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

                // 🥈 過渡期的次要來源：persona pool —— 走 UCL_PersonaProfile.PoolNames 唯一讀取入口
                //    （Phase 0 接縫；registry 退場時只改接縫實作，本段不必動）。
                foreach (var n in UCL_PersonaProfile.PoolNames())
                    s_Known.Add(n);
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

        // ===========================================================
        // 區塊職責：讀 `_current.md`（存值投影）。
        // 物理意義：**事實來源是 events/**，本檔只是投影 —— 但投影裡有一樣東西是重算拿不到的：
        //          `opening_balance`（遷移反推的期初餘額，沒有對應事件）。
        //          所以要顯示「跟其他工具一致的分數」就得讀它，不能只靠 RebuildCurrent(null)。
        // 數值影響：純讀；檔不存在或欄位缺 → 回 null / 型別預設，不猜值。
        //          ⚠ 缺 emotion_vector 的舊檔會回一個全 0 的向量，那跟「真的全 0」長得一樣 ——
        //          呼叫端要看的話請一併看 event_count（0 筆事件才可能真的全 0）。
        // ===========================================================
        public static UCL_RelationshipCurrent LoadCurrent(string iPersona, string iTarget)
        {
            string aPath = CurrentPath(iPersona, iTarget);
            if (!File.Exists(aPath)) return null;
            var aCur = new UCL_RelationshipCurrent { target = iTarget };
            foreach (var a in UCL_RelationshipAxes.Names) aCur.emotion_vector[a] = 0f;

            bool aIn = false, aPastFm = false;
            string aBlock = null;   // 目前在哪個縮排區塊（emotion_vector / opening_balance）
            foreach (var ln in File.ReadAllLines(aPath, Encoding.UTF8))
            {
                if (!aPastFm && ln.StartsWith("---", StringComparison.Ordinal))
                {
                    if (!aIn) { aIn = true; continue; }
                    aPastFm = true; continue;
                }
                if (aPastFm) break;   // 正文是給人讀的複述，不解析（那份重複正是 UI 該省掉的東西）
                if (ln.StartsWith("  ", StringComparison.Ordinal) && aBlock != null)
                {
                    int ci = ln.IndexOf(':');
                    if (ci <= 0) continue;
                    string ak = ln.Substring(0, ci).Trim();
                    if (!float.TryParse(StripComment(ln.Substring(ci + 1).Trim()),
                            NumberStyles.Float, CultureInfo.InvariantCulture, out float av)) continue;
                    if (aBlock == "emotion_vector") aCur.emotion_vector[ak] = av;
                    else
                    {
                        aCur.opening_balance ??= new Dictionary<string, float>();
                        aCur.opening_balance[ak] = av;
                    }
                    continue;
                }
                aBlock = null;
                int c = ln.IndexOf(':');
                if (c <= 0) continue;
                string k = ln.Substring(0, c).Trim();
                string v = StripComment(ln.Substring(c + 1).Trim());
                switch (k)
                {
                    case "target": if (!string.IsNullOrEmpty(v)) aCur.target = v; break;
                    case "emotion_vector": aBlock = "emotion_vector"; break;
                    // `opening_balance: null` 是**顯式的「沒有期初餘額」**，不是漏填 ——
                    // 只有後面沒跟值時才進縮排區塊模式。
                    case "opening_balance": if (string.IsNullOrEmpty(v)) aBlock = "opening_balance"; break;
                    case "surface_score": int.TryParse(v, out aCur.surface_score); break;
                    case "tier": aCur.tier = v; break;
                    case "event_count": int.TryParse(v, out aCur.event_count); break;
                    case "opinion_count": int.TryParse(v, out aCur.opinion_count); break;
                    case "last_updated": aCur.last_updated = v; break;
                    case "recomputable": aCur.recomputable = (v == "true"); break;
                }
            }
            return aCur;
        }

        // ===========================================================
        // 區塊職責：讀某對象的全部「看法」。
        // 物理意義：看法與向量解耦（純文字），所以它沒有 axis_deltas；
        //          遷移進來的舊看法 `at` 是空的（**沒有時戳，不是漏填**）。
        // 數值影響：純讀；排序交給呼叫端 —— 這裡回磁碟順序，不假裝有時序。
        // ===========================================================
        public static List<UCL_RelationshipOpinion> LoadOpinions(string iPersona, string iTarget)
        {
            var aOut = new List<UCL_RelationshipOpinion>();
            string d = OpinionsDir(iPersona, iTarget);
            if (!Directory.Exists(d)) return aOut;
            foreach (var f in Directory.GetFiles(d, "*.md"))
            {
                var o = new UCL_RelationshipOpinion();
                bool aIn = false, aPastFm = false;
                var aBody = new StringBuilder();
                foreach (var ln in File.ReadAllLines(f, Encoding.UTF8))
                {
                    if (!aPastFm && ln.StartsWith("---", StringComparison.Ordinal))
                    {
                        if (!aIn) { aIn = true; continue; }
                        aPastFm = true; continue;
                    }
                    if (aPastFm) { aBody.Append(ln).Append('\n'); continue; }
                    int c = ln.IndexOf(':');
                    if (c <= 0) continue;
                    string k = ln.Substring(0, c).Trim();
                    string v = StripComment(ln.Substring(c + 1).Trim());
                    switch (k)
                    {
                        // "null" 是顯式的「舊資料沒有時戳」—— 存成空字串，別讓它變成字面上的 "null"
                        case "at": o.at = (v == "null") ? "" : v; break;
                        case "migrated_at": o.migrated_at = v; break;
                        case "origin":
                            foreach (var s in v.Trim('[', ']').Split(','))
                            {
                                string t = s.Trim();
                                if (!string.IsNullOrEmpty(t)) o.origin.Add(t);
                            }
                            break;
                    }
                }
                o.text = aBody.ToString().Trim();
                aOut.Add(o);
            }
            return aOut;
        }

        static string StripComment(string v)
        {
            int i = v.IndexOf('#');
            return i < 0 ? v : v.Substring(0, i).Trim();
        }

    }
}
#endif
