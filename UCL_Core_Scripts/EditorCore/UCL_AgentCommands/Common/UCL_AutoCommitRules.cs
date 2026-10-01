// 區塊職責：自動 commit 分群規則的 **Unity 端轉接層** —— 規則本體已下沉到 SCP_Core 的 `SCP_AutoCommitRules`。
// 物理意義：⏳ **準備退場**（Tim 2026-09-30，TASK-0340）：自動 commit 移植到 Senate
//          （「自動 Commit」頁＋`senate cmd auto-commit`），規則與引擎在 SCP_Core。
//          本檔還留著，是因為 `UCL_AutoCommitPage` 退場前還在用它。
//          ⭐ 它**不再持有規則表**，只把 SCP_Core 那兩張表轉成本端的 `GroupDef` ——
//            🩸 理由：下沉之後規則一度有兩份（UCL 一份、SCP 一份），而這種規則的錯配等級是「檔進錯 commit」，
//            只改其中一份的症狀是「同一個檔在 Unity 被分到 A 群、在 Senate 被分到 B 群」，兩邊各自看起來都正常。
//            ⇒ 規則只准有一個真相源；改規則去改 `<SCP_Core>/Runtime/Git/SCP_AutoCommitRules.cs`。
// 數值影響：純資料與純函式，不碰 IO。分群結果與 SCP_Core 端逐群相同（判定順序同為 subptr → ephemeral → 分群）。
//          ⚠ 行為差異只有一格，而它是修正：status 裡以 `dir/` 結尾的條目（**還沒登記進 .gitmodules 的巢狀 repo**）
//            一律歸 `__subptr` —— 舊版會被 `ChatTavern/` 前綴吃進 runtime 群（預設勾選），
//            `git add` 會把它塞成沒有 .gitmodules 的 gitlink，而那不會報錯（TASK-0340 沙盒實測）。
// @doc-sync: <SCP_Core>/Docs~/AutoCommit.md（分群表的真相源在 SCP_Core）
#if UNITY_EDITOR
using System;
using SCP.Core.Git;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>自動 commit 的分群規則（轉接 SCP_Core 的 <see cref="SCP_AutoCommitRules"/>）。</summary>
    public static class UCL_AutoCommitRules
    {
        /// <summary>一群的規則。Match 吃「相對該 repo root 的正斜線路徑」。</summary>
        public class GroupDef
        {
            public string Key;
            public string Label;
            public Func<string, bool> Match;
            public string Message;       // commit 訊息主體（檔數統計由呼叫端補在後面）
            public bool DefaultOn;
        }

        public const string KEY_SUBPTR = SCP_AutoCommitRules.KeySubPtr;
        public const string KEY_OTHER = SCP_AutoCommitRules.KeyOther;
        public const string KEY_OTHER_UNTRACKED = SCP_AutoCommitRules.KeyOtherUntracked;

        /// <summary>AgentCommands 本層（真相源：<see cref="SCP_AutoCommitRules.AgentGroupDefs"/>）。</summary>
        public static readonly GroupDef[] AgentGroupDefs = Adapt(SCP_AutoCommitRules.AgentGroupDefs);

        /// <summary>persona 信件庫（真相源：<see cref="SCP_AutoCommitRules.PersonaGroupDefs"/>）。</summary>
        public static readonly GroupDef[] PersonaGroupDefs = Adapt(SCP_AutoCommitRules.PersonaGroupDefs);

        /// <summary>取該模式的規則表。`iPersonaLetters`＝letters 模式。</summary>
        public static GroupDef[] Defs(bool iPersonaLetters)
            => iPersonaLetters ? PersonaGroupDefs : AgentGroupDefs;

        static GroupDef[] Adapt(SCP_AutoCommitGroupDef[] iDefs)
        {
            var aOut = new GroupDef[iDefs.Length];
            for (int i = 0; i < iDefs.Length; ++i)
            {
                var aDef = iDefs[i];
                aOut[i] = new GroupDef
                {
                    Key = aDef.Key,
                    Label = aDef.Label,
                    Match = aDef.Match,
                    Message = aDef.Message,
                    DefaultOn = aDef.DefaultOn,
                };
            }
            return aOut;
        }

        /// <summary>ephemeral —— 永遠不進候選（判準在 SCP_Core）。</summary>
        public static bool IsEphemeral(string path) => SCP_AutoCommitRules.IsEphemeral(path);

        /// <summary>
        /// 一個路徑該進哪一群。`iIsSubPointer`＝這個路徑是巢狀 submodule 的 pointer。
        /// 回 null ＝ ephemeral（不進候選）。判定順序同 SCP_Core：subptr → ephemeral → 分群。
        /// </summary>
        public static string Classify(string iPath, GroupDef[] iDefs, bool iIsSubPointer)
        {
            // `dir/` 結尾 ＝ 未登記的巢狀 repo（見檔頭）—— 跟 pointer 同一族，永不自動收。
            if (iIsSubPointer || iPath.EndsWith("/", StringComparison.Ordinal)) return KEY_SUBPTR;
            if (IsEphemeral(iPath)) return null;
            if (iDefs != null)
            {
                foreach (var def in iDefs)
                {
                    if (def.Match(iPath)) return def.Key;
                }
            }
            return KEY_OTHER;
        }
    }
}
#endif
