// 區塊職責：relationship 系統的資料模型 —— 事件 / 看法 / 當前總值，以及 8 軸的定義。
// 物理意義：對應 letters/<persona>/relationship/<target>/ 底下的三種檔案。
//          **計算完全沿用舊 affinity 的 8 軸與權重**（本案只動存放方式，不動好感度怎麼算）。
// 數值影響：純資料 + 純函式（重算向量、算 surface_score / tier），無 IO。
// 設計沿革：Plan_Relationship_System.md（Tim 2026-08-18 拍板）。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;

namespace UCL.Core.EditorLib.AgentCommands.Relationship
{
    // ===========================================================
    // 區塊職責：8 軸的名稱、權重、值域 —— 從舊 relations.json 的 `_emotion_*` 欄位原樣搬過來。
    // ⚠ 順序即舊檔 `emotion_vector` 陣列的順序，**不可重排** ——
    //   重排不會編譯錯、不會執行錯，只會讓每個人的好感度悄悄變成別的數字。
    // ===========================================================
    public static class UCL_RelationshipAxes
    {
        public static readonly string[] Names =
        {
            "trust", "affection", "respect", "interest",
            "irritation", "dependence", "admiration", "loyalty",
        };

        /// <summary>加權和用的權重（irritation 是負權重 —— 惱怒會拉低總分）。</summary>
        public static readonly Dictionary<string, float> Weights = new()
        {
            { "trust", 2.0f }, { "affection", 2.0f }, { "respect", 1.5f }, { "interest", 1.0f },
            { "irritation", -2.0f }, { "dependence", 0.5f }, { "admiration", 1.0f }, { "loyalty", 1.5f },
        };

        public const float MIN = -1.0f;
        public const float MAX = 1.0f;

        public static float Clamp(float v) => v < MIN ? MIN : (v > MAX ? MAX : v);

        // 區塊職責：加權和 → [-100, 100]。
        // 物理意義：分母是**權重絕對值的總和**（`abs(-2.0)` 也算進去），
        //          與 `affinity_manager.compute_surface_score` 逐字對應。
        // 🩸 2026-08-18：我第一版憑印象寫成「只加正權重當分母」——
        //   拿 108 筆既有資料回歸測，只對 20 筆。改成 abs 之後 **108/108 全中**。
        //   ⇒ 移植公式不要憑記憶重寫，去讀原實作然後拿既有資料回歸。
        // 數值影響：改分母等於把所有人的歷史分數重新定義，不要動。
        public static int SurfaceScore(Dictionary<string, float> iVec)
        {
            float aSum = 0f, aWAbs = 0f;
            foreach (var kv in Weights)
            {
                iVec.TryGetValue(kv.Key, out float v);
                aSum += v * kv.Value;
                aWAbs += Math.Abs(kv.Value);
            }
            if (aWAbs <= 0f) return 0;
            float aNorm = aSum / aWAbs * 100f;
            if (aNorm > 100f) aNorm = 100f;
            if (aNorm < -100f) aNorm = -100f;
            return (int)Math.Round(aNorm, MidpointRounding.AwayFromZero);
        }

        // 分段沿用 `affinity_manager.get_tier`（51 / 11 / -9 / -49），實測 108/108 相符。
        // ⚠ 不是 60/30/10 —— 那是我第一版憑印象寫的，錯的。
        public static string Tier(int iScore)
        {
            if (iScore >= 51) return "信任";
            if (iScore >= 11) return "在意";
            if (iScore >= -9) return "普通";
            if (iScore >= -49) return "冷淡";
            return "厭惡";
        }
    }

    /// <summary>一筆好感事件（有 axis_deltas 的帳）。對應 `events/<ts>-<fp>.md`。</summary>
    public class UCL_RelationshipEvent
    {
        /// <summary>UTC ISO8601。**遷移進來的舊事件一定有**（舊 history 每筆都有 `at`）。</summary>
        public string at = "";
        public string persona = "";
        public string target = "";
        /// <summary>來源標記：遷移填來源專案名（LY / Bar），新事件填 `live`。</summary>
        public string source = "live";
        public Dictionary<string, float> axis_deltas = new();
        /// <summary>寫入當下的分數 —— **只是歷史註記，不是事實來源**（事實來源是重算）。</summary>
        public int surface_score_after = 0;
        /// <summary>正文＝reason（人讀的那句）。</summary>
        public string reason = "";

        // 區塊職責：事件的身分指紋 —— **同一筆事件不論來自哪個專案，算出來逐字元相同**。
        // 物理意義：去重因此變成「檔案已存在就跳過」，不需要任何比對邏輯，
        //          也就沒有比對邏輯會漏掉的可能（Plan §2.2）。
        // 數值影響：純函式。實測 425 筆跨專案共同事件的 (at, reason) 完全一致、0 筆撞號。
        public string FileName() => FileNameOf(at, reason);

        public static string FileNameOf(string iAt, string iReason)
        {
            string aTs = (iAt ?? "").Replace("-", "").Replace(":", "").Replace(".", "");
            // 指紋刻意含 at：只用 reason 的話，同一句話在不同時間發生會被誤判成同一件事
            return aTs + "-" + Sha1Hex((iAt ?? "") + "\n" + (iReason ?? "").Trim(), 8) + ".md";
        }

        public static string Sha1Hex(string iText, int iLen)
        {
            using var aSha = System.Security.Cryptography.SHA1.Create();
            var aBytes = aSha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(iText ?? ""));
            var sb = new System.Text.StringBuilder();
            foreach (var b in aBytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString().Substring(0, iLen);
        }
    }

    /// <summary>一則看法（純文字，與向量解耦）。對應 `opinions/op-<hash>.md`。</summary>
    /// <remarks>
    /// ⚠ **舊資料沒有時戳** —— 實測兩專案的 opinions 全部是純字串、只有陣列位置，
    /// 而分支之後 LY 的第 N 則 ≠ Bar 的第 N 則 ⇒ **索引不能當身分**，只能用內容雜湊。
    /// 因此 <see cref="at"/> 對遷移進來的資料是空字串，且**要顯式寫成 `at: null`**：
    /// 省略的話下一個工具會以為「還沒填」而去猜一個時間，而猜出來的時間看起來跟真的一樣。
    /// </remarks>
    public class UCL_RelationshipOpinion
    {
        public string text = "";
        /// <summary>空＝舊資料沒有時戳（不是漏填）。遷移之後新寫的一律帶真的 UTC ISO8601。</summary>
        public string at = "";
        /// <summary>來源與原陣列索引，例 `LY#12`；合併時可能有多個。順序只能靠它還原到這個程度。</summary>
        public List<string> origin = new();
        public string migrated_at = "";

        public string FileName() => "op-" + UCL_RelationshipEvent.Sha1Hex((text ?? "").Trim(), 12) + ".md";
    }

    /// <summary>某個對象的當前總值。對應 `<target>/_current.md`。</summary>
    public class UCL_RelationshipCurrent
    {
        public string target = "";
        public Dictionary<string, float> emotion_vector = new();
        public int surface_score = 0;
        public string tier = "";
        public int event_count = 0;
        public int opinion_count = 0;
        public string last_updated = "";

        // 區塊職責：本案的體檢指標 —— 「存值算不算得出來」。
        // 物理意義：舊系統把存值與事件流並列在同一個檔裡，讀的人天然假設一致，
        //          而實測 108 筆配對裡有 3 筆已經對不上，**且沒有任何機制會叫**。
        //          把它變成檔案上的一個欄位，那個病以後會自己顯形。
        public bool recomputable = true;
        /// <summary>期初餘額：現值減重算的差；null/空＝全部由事件推得。</summary>
        public Dictionary<string, float> opening_balance = null;

        // 區塊職責：從事件流重算向量（＋可選的期初餘額）。
        // 物理意義：**事件是事實來源，存值是投影** —— 這是新架構跟舊架構最根本的差別。
        // 數值影響：逐筆累加 axis_deltas 後 clamp 到 [-1,1]；順序不影響結果（加法交換律），
        //          所以合併兩個專案的事件流不需要排序，這也是去重可以只靠檔名的原因之一。
        public static Dictionary<string, float> Recompute(
            IEnumerable<UCL_RelationshipEvent> iEvents, Dictionary<string, float> iOpening)
        {
            var aAcc = new Dictionary<string, float>();
            foreach (var a in UCL_RelationshipAxes.Names) aAcc[a] = 0f;
            if (iOpening != null)
                foreach (var kv in iOpening) if (aAcc.ContainsKey(kv.Key)) aAcc[kv.Key] += kv.Value;
            foreach (var e in iEvents)
                foreach (var kv in e.axis_deltas) if (aAcc.ContainsKey(kv.Key)) aAcc[kv.Key] += kv.Value;
            var aOut = new Dictionary<string, float>();
            foreach (var a in UCL_RelationshipAxes.Names)
                aOut[a] = (float)Math.Round(UCL_RelationshipAxes.Clamp(aAcc[a]), 4);
            return aOut;
        }
    }
}
#endif
