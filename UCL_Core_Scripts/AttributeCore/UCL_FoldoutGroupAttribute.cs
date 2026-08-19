// 區塊職責：欄位分組折疊（Odin FoldoutGroup 的 UCL 版）—— 把同一組欄位收進一個可折疊的框。
// 物理意義：欄位一多，DrawField 畫出來就是一長串平鋪的列，而「哪些欄位是一起的」只存在讀的人腦裡。
//          本 attribute 是**範圍標記**：標在一段的**第一個欄位**上，
//          從它開始往下的欄位都屬於這一組，直到**碰到下一個 [UCL_FoldoutGroup]** 為止。
//          ⇒ 使用端一段只標一次；欄位順序完全不動（分組是「畫到哪裡為止」，不是把欄位搬到一起）。
// 數值影響：純顯示層 —— 不影響序列化、不影響欄位值、不影響 JSON 鍵名。
//          折疊狀態存在 DrawField 拿到的 UCL_ObjectDictionary（跟其他折疊狀態同一套機制），
//          所以它跟著「那個物件在哪個頁面被畫」走，不是全域狀態。
//
// 設計決策（2026-08-19）：
//   · **組名可在地化**：跟 [Header] 同規則 —— 組名若是 UCL_LocalizeManager 的既有 key 就翻譯，
//     否則原樣顯示。（在地化在 GUI 端做，不在共用反射 cache 做，理由見 UCL_TypeReflectCache。）
//   · **預設收合**（`iDefaultExpanded = false`）—— 分組的用途是收掉平常不看的東西；
//     預設展開等於分了組但畫面沒變。要預設展開的組顯式寫 `expanded: true`。
//   · **範圍語意，不是標籤語意**（Tim 2026-08-19 微調）—— 一段只標第一個欄位。
//     初版是「標同名就收攏到一起」，那會**把欄位搬離原本的位置**；改成範圍之後順序不再被動到，
//     而使用端要寫的 attribute 從「每個欄位一個」降到「每段一個」。
//   · **空組名＝結束目前這一組**（`[UCL_FoldoutGroup("")]`）—— 一段分組後面要接回未分組欄位時用。
//     不寫它就會一路收到下一個組或型別結尾，那通常正是想要的。
//   · **同名出現兩段** ⇒ 兩個框、**共用同一個折疊狀態**（狀態以組名為 key）。同名就是同一個概念。
//   · **組名裡的 `/` 目前是字面字元，不是巢狀路徑**（Odin 支援 `A/B` 巢狀）——
//     刻意不做：巢狀要引進一棵樹與逐層折疊狀態，而現在沒有任何一個現場需要它。
//     ⇒ 之後要加巢狀時，attribute 的呼叫端一行都不用改（字串已經帶得住路徑）。
using System;

namespace UCL.Core.ATTR
{
    /// <summary>
    /// 欄位分組折疊（**範圍標記**）—— 標在一段的第一個欄位上，往下收到**下一個 [UCL_FoldoutGroup]** 為止。
    /// <para>純顯示層，不影響序列化。折疊狀態存在該物件的 <c>UCL_ObjectDictionary</c>。</para>
    /// <code>
    /// public int m_Normal;                                          // 不在任何組
    /// [UCL_FoldoutGroup("Advanced")]    public int   m_Retry;       // ↓ 這一組從這裡開始
    /// public float m_Timeout;                                        // 同組（不必再標）
    /// [UCL_FoldoutGroup("Debug", true)] public bool  m_VerboseLog;  // 上一組到此結束；本組預設展開
    /// [UCL_FoldoutGroup("")]            public int   m_Tail;        // 顯式結束分組，回到未分組
    /// </code>
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
    public class UCL_FoldoutGroupAttribute : Attribute
    {
        /// <summary>
        /// 組名（顯示在折疊框標題；是 UCL_LocalizeManager 既有 key 時會被翻譯）。
        /// **空字串＝結束目前這一組**（之後的欄位回到未分組）。
        /// </summary>
        public string m_GroupName;

        /// <summary>該組預設是否展開。預設 false —— 分組的用途是收掉平常不看的東西。</summary>
        public bool m_DefaultExpanded;

        public UCL_FoldoutGroupAttribute(string iGroupName, bool iDefaultExpanded = false)
        {
            m_GroupName = iGroupName;
            m_DefaultExpanded = iDefaultExpanded;
        }
    }
}
