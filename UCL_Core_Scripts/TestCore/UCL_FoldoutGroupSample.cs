// 區塊職責：`[UCL_FoldoutGroup]` 的用法樣本 ＋ 不開 GUI 也能驗的自我檢查。
// 物理意義：分組的正確性有兩層 ——
//          ① **範圍層**：標記有沒有正確往下傳（收到下一個標記為止）、空組名有沒有真的結束分組。
//          ② **繪製層**：折疊框有沒有畫出來、收合時整組有沒有真的消失。
//          ①**不需要 GUI 就能量**（它只是 TypeFieldInfoCache 的輸出），所以本檔提供 SelfTest；
//          ②只有真的重繪才算數，本檔量不到，別把 SelfTest 綠燈讀成「分組畫對了」。
// 數值影響：純測試型別，不參與任何執行期邏輯；SelfTest 唯讀（只建 cache、不改狀態）。
// 用法：
//   run_cmd.py --persona <me> run Invoke --arg type=UCL.Core.TestLib.UCL_FoldoutGroupSample --arg member=SelfTest
using System.Text;
using UCL.Core.ATTR;

namespace UCL.Core.TestLib
{
    /// <summary>
    /// `[UCL_FoldoutGroup]` 樣本 —— **範圍語意**：一段只標第一個欄位，往下收到下一個標記為止。
    /// 這裡刻意涵蓋四種情形：組前的散欄位／一段多欄位／接續的下一組／顯式結束分組。
    /// </summary>
    public class UCL_FoldoutGroupSample
    {
        public int m_Plain1;                                    // 組之前的散欄位

        [UCL_FoldoutGroup("Advanced")] public int m_AdvA;       // ↓ Advanced 這一組從這裡開始（預設收合）
        public string m_AdvB;                                   // 同組 —— 不必再標
        public float m_AdvC;                                    // 同組

        [UCL_FoldoutGroup("Debug", true)] public bool m_DebugVerbose;   // 上一組到此結束；Debug 預設展開
        public int m_DebugLevel;                                        // 同組

        [UCL_FoldoutGroup("")] public int m_Plain2;             // 空組名＝顯式結束分組
        public int m_Plain3;                                    // 回到未分組

        /// <summary>
        /// 驗「範圍層」：印出 <see cref="UI.UCL_GUILayout.TypeFieldInfoCache"/> 算出的**每個欄位生效的組名**。
        /// <para>⚠ 這支證明的是**範圍傳遞與組名解析**，不證明折疊框畫得出來 —— 那要真的重繪才算。</para>
        /// </summary>
        public static string SelfTest() => Dump(typeof(UCL_FoldoutGroupSample));

        /// <summary>
        /// 對**任意型別**印出每個欄位生效的折疊組（驗真實資產的分組是不是照你想的斷開）。
        /// <code>run_cmd.py --persona &lt;me&gt; run Invoke --arg type=UCL.Core.TestLib.UCL_FoldoutGroupSample         ///     --arg member=Dump --arg paramTypes=System.String --arg args=LittleYellow.HSceneAsset</code>
        /// 找不到型別回一句話而不是丟例外 —— 打錯名字要看得出是打錯，不是「這型別沒有欄位」。
        /// </summary>
        public static string Dump(string iTypeName)
        {
            System.Type aType = null;
            foreach (var aAsm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                aType = aAsm.GetType(iTypeName);
                if (aType != null) break;
            }
            if (aType == null)
            {
                string aMsg = $"[FoldoutGroupSample] 找不到型別 `{iTypeName}`（要完整名稱含 namespace，大小寫精確）";
                UnityEngine.Debug.LogWarning(aMsg);
                return aMsg;
            }
            return Dump(aType);
        }

        static string Dump(System.Type iType)
        {
            var aCache = new UI.UCL_GUILayout.TypeFieldInfoCache(iType);
            var aSb = new StringBuilder();
            aSb.Append($"[FoldoutGroupSample] {iType.Name} fields={aCache.m_FieldInfos.Count} | ");
            foreach (var aField in aCache.m_FieldInfos)
            {
                string aGroup = string.IsNullOrEmpty(aField.m_FoldoutGroup) ? "-" : aField.m_FoldoutGroup;
                aSb.Append($"{aField.m_FieldInfo.Name}({aGroup}{(aField.m_FoldoutDefaultExpanded ? ",open" : "")}) ");
            }
            UnityEngine.Debug.Log(aSb.ToString());
            return aSb.ToString();
        }
    }
}
