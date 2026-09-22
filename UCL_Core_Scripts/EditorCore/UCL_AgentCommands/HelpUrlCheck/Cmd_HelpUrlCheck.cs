// 區塊職責：[HelpURL] 目標存在性的機械檢查（TASK-0257）—— 把「要有人記得去點那顆 ? 鈕」換成一支可被呼叫的驗證器。
// 物理意義：反射掃 AppDomain 全部型別上的 HelpURLAttribute，逐條走 UCL_URL.ResolveURL **本人**解析，
//          再對本地目標做 File.Exists；報告同時印「掃到 / 本地 / 解得到 / 缺」四個數（比較，不是狀態）。
// 數值影響：純讀 —— 不寫專案檔、不動任何資產，只寫一份回傳檔；缺檔時 throw ⇒ 呼叫端拿到非零退出。
//
// 🩸 本單的起點（kiara 2026-09-15 標「誠實的空白」，至第 6 個 wake 仍無人點過）：
//   HelpURL 指向不存在的檔時，鏈路是四層 fail-soft 疊起來，UCL_GUILayoutDrawObject 2026-08-17 補了
//   **點擊時**的 LogWarning —— 那句話只有在有人去點時才會響，而「沒有人去點」正是本案的成因。
//   ⇒ 本 Cmd 補的是另一個入口：不必有人記得，跑一次就把三個數字擺在一起。
//
// ⚠ 射程（刻意寫在最前面，不寫在結尾）：本 Cmd 驗的是「解得到路徑且檔案存在」。
//   ⛔ **不宣稱**「按鈕點下去會開出 MarkdownViewer」—— 那是 GUI 行為讀數，本 Cmd 給不了。
//   ⚠ 走 ResolveURL 本人的代價是它必須在 Editor 內跑（Unity 型別 + 語系服務）⇒ 入口是
//   `senate ucmd run HelpUrlCheck`，**Editor 沒開就跑不完**。那不是遺漏，是「不重寫第二把尺」的直接後果。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UCL.Core.EditorLib.AgentCommands.ChatTavern;

namespace UCL.Core.EditorLib.AgentCommands.HelpUrlCheck
{
    // 區塊職責：區域 helper —— 對齊 Cmd_TypeInspect / Cmd_Glossary 的 _last_op 通報慣例。
    //          （RejectLastOp / ResolveLastOp 不在 base class，假設繼承會撞 CS0103。）
    internal static class Cmd_HelpUrlCheck_Helpers
    {
        public static void ResolveLastOp(IDictionary<string, string> iArgs, string iMd)
            => UCL_ChatTavernRender.WriteLastOp(iMd, iArgs);

        // 區塊職責：報告已經落地之後才喊的失敗 —— 先寫檔再 throw，兩者順序不可調換。
        // 物理意義：呼叫端要的是「缺哪幾條」，而不是一句「失敗了」；throw 只負責讓退出碼非零。
        public static void FailAfterReport(IDictionary<string, string> iArgs, string iMd, string iMsg)
        {
            UCL_ChatTavernRender.WriteLastOp(iMd, iArgs);
            Debug.LogWarning($"[HelpUrlCheck] {iMsg}");
            throw new InvalidOperationException(iMsg);
        }
    }

    /// <summary>
    /// 區塊職責：掃遍所有 <c>[HelpURL]</c> 並檢查目標是否真的存在。
    /// 物理意義：反射取 attribute（⛔ 不 grep 原始碼 —— 開單時的 grep 把 7 個註解裡的示範字串
    ///          數成了真 attribute，而那批印出來跟真的一模一樣）；解析走 <see cref="UCL.Core.UCL_URL.ResolveURL"/> 本人
    ///          （⛔ 不重寫一份解析規則 —— 兩把尺會漂移，而漂移的樣子是「0 個缺檔」）。
    /// 數值影響：唯讀；缺檔 &gt; 0 時 throw ⇒ 非零退出。
    /// </summary>
    public class Cmd_HelpUrlCheck : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "HelpUrlCheck";

        public override string ShortDescription =>
            "[HelpURL] 目標存在性機械檢查 —— 反射掃 attribute，走 UCL_URL.ResolveURL 本人解析後驗檔案存在";

        public override string ArgsSchema =>
            "max_list=最多列出幾條缺檔（選填，預設 50；0 = 不限）\n" +
            "list_ok=1 時連「解得到且存在」的本地條目也逐條列出（選填，預設 0 —— 缺檔清單才是本 Cmd 的正文）\n" +
            "list_remote=1 時列出雲端 URL 條目（選填，預設 0；雲端目標本 Cmd **不驗**，只數）\n" +
            "list_external=1 時列出「非本專案原始碼宣告」的條目（選填，預設 0；同樣不驗，只數）";

        public override UCL_CmdArgsSpec ArgsSpec => new UCL_CmdArgsSpec
        {
            Known = new[] { "max_list", "list_ok", "list_remote", "list_external" },
        };

        public override string ExampleArgs => "max_list=50";

        public override string HelpURL =>
            "ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_HelpUrlCheck.md";

        // 反射掃全 AppDomain 型別 + 逐條 File.Exists —— 秒級工作，早期失敗比卡 20 分鐘有用。
        public override int TimeoutSeconds => 300;

        // 區塊職責：單一條 [HelpURL] 的讀數（型別 → 原始 url → 解析後路徑 → 存在與否）。
        // 物理意義：每一格都帶位址，呼叫端不必相信報告，可以自己重跑。
        private class Row
        {
            public string TypeName;
            public string Assembly;
            public string RawUrl;
            public string Resolved;
            public bool IsExternal;    // 宣告它的組件不是由 Assets/ 底下的原始碼編出來的 ⇒ 本 Cmd 不驗
            public bool IsRemote;      // 解析結果含 "://" ⇒ 雲端目標，本 Cmd 不驗
            public bool Exists;        // 僅「本專案 ＋ 本地」的條目有意義
            public string ResolveError; // 解析本身炸掉時的訊息（null = 沒炸）
        }

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            int aMaxList = ParseInt(GetArg(args, "max_list", "50"), 50);
            bool aListOk = GetArg(args, "list_ok", "0") == "1";
            bool aListRemote = GetArg(args, "list_remote", "0") == "1";
            bool aListExternal = GetArg(args, "list_external", "0") == "1";

            var aRows = Scan(token);

            var aExternal = aRows.Where(r => r.IsExternal).ToList();
            var aProject = aRows.Where(r => !r.IsExternal).ToList();
            var aRemote = aProject.Where(r => r.IsRemote).ToList();
            var aLocal = aProject.Where(r => !r.IsRemote).ToList();
            var aMissing = aLocal.Where(r => !r.Exists).ToList();
            var aOk = aLocal.Where(r => r.Exists).ToList();

            string aMd = Render(aRows.Count, aExternal, aProject, aRemote, aLocal, aOk, aMissing,
                                aMaxList, aListOk, aListRemote, aListExternal);

            if (aMissing.Count > 0)
            {
                Cmd_HelpUrlCheck_Helpers.FailAfterReport(args, aMd,
                    $"[HelpURL] 缺 {aMissing.Count} 條（本專案本地目標 {aLocal.Count} 條，解得到 {aOk.Count} 條）—— 清單見回傳檔。");
                return;
            }

            Cmd_HelpUrlCheck_Helpers.ResolveLastOp(args, aMd);
            await UniTask.CompletedTask;
        }

        // ===========================================================
        // 區塊職責：哪些組件是「本專案原始碼編出來的」—— 由 CompilationPipeline **自己**回答。
        // 物理意義：⛔ 不寫死 Unity / 套件的組件名單（那是我自己搭的尺，而它會隨 Unity 版本漂移）。
        //          判準＝該組件的 sourceFiles 落在 `Assets/` 底下；Packages / Library/PackageCache
        //          與 Unity 內建組件都不符合 ⇒ 自動落進「外部宣告」桶。
        // 數值影響：只決定分桶，不改任何一條的解析結果。
        //
        // 🩸 血證（本函式的來源，2026-09-22 第一趟自己撞的）：沒有這一格時，報告印「缺 20 條」，
        //   而那 20 條**全部是 Unity 自家型別**（`class-State`／`NestedStateMachines` 這種裸 slug
        //   不含 `://` ⇒ 被當成專案相對路徑 → `GetFullPath` → 檔案當然不存在）。
        //   那些 slug 是 Unity 說明系統自己在解的，從來不歸 UCL_URL 管。
        //   ⇒ 而失效的樣子不是漏報，是**永遠紅燈** —— 一支永遠紅的檢查跟沒有檢查是同一件事。
        // ===========================================================
        private static HashSet<string> GetProjectAssemblyNames()
        {
            var aNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var aType in new[]
                     {
                         UnityEditor.Compilation.AssembliesType.Editor,
                         UnityEditor.Compilation.AssembliesType.Player,
                     })
            {
                UnityEditor.Compilation.Assembly[] aAssemblies;
                try
                {
                    aAssemblies = UnityEditor.Compilation.CompilationPipeline.GetAssemblies(aType);
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (var aAssembly in aAssemblies)
                {
                    if (aAssembly?.sourceFiles == null || aAssembly.sourceFiles.Length == 0) continue;

                    // [計算邏輯] 取第一個原始檔判斷歸屬即可 —— 同一個組件的原始檔不會跨 Assets/ 與 Packages/。
                    string aFirst = aAssembly.sourceFiles[0].Replace('\\', '/');
                    if (aFirst.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                    {
                        aNames.Add(aAssembly.name);
                    }
                }
            }

            return aNames;
        }

        // ===========================================================
        // 區塊職責：反射掃描 —— AppDomain 全部型別上的 HelpURLAttribute。
        // 物理意義：口徑刻意是「全 AppDomain」而不是「grep Assets 下的 .cs」：後者數的是字串，
        //          而**註解裡的示範字串跟真 attribute 印出來一模一樣**（開單時的 grep 多數了 7 條）。
        //          掃全部之後再用上面那把有位址的尺分桶 —— 先量全，再分類，不先篩再量。
        // 數值影響：唯讀反射；解析每條走 ResolveURL 本人，失敗不吞（記進 ResolveError 照樣列出來）。
        // ===========================================================
        private List<Row> Scan(CancellationToken token)
        {
            var aRows = new List<Row>();
            var aProjectAssemblies = GetProjectAssemblyNames();

            foreach (var aType in AssemblyExtensions.GetAllTypes())
            {
                token.ThrowIfCancellationRequested();
                if (aType == null) continue;

                HelpURLAttribute aAttr;
                try
                {
                    aAttr = aType.GetCustomAttributes(typeof(HelpURLAttribute), false)
                                 .FirstOrDefault() as HelpURLAttribute;
                }
                catch (Exception)
                {
                    // 有些動態 / 損壞型別取 attribute 會炸 —— 那不是本 Cmd 要回答的問題，跳過。
                    continue;
                }
                if (aAttr == null || string.IsNullOrEmpty(aAttr.URL)) continue;

                var aRow = new Row
                {
                    TypeName = aType.FullName,
                    Assembly = aType.Assembly.GetName().Name,
                    RawUrl = aAttr.URL,
                };
                aRow.IsExternal = !aProjectAssemblies.Contains(aRow.Assembly);

                if (aRow.IsExternal)
                {
                    // [射程] 外部組件的 HelpURL 由它自己的說明系統解（Unity 的裸 slug／套件的雲端頁），
                    //        不歸 UCL_URL 管 ⇒ 只數不驗，連解析都不做（解了會得到一個誤導人的路徑）。
                    aRows.Add(aRow);
                    continue;
                }

                try
                {
                    aRow.Resolved = UCL.Core.UCL_URL.ResolveURL(aAttr.URL);
                }
                catch (Exception e)
                {
                    aRow.ResolveError = e.Message;
                }

                if (!string.IsNullOrEmpty(aRow.ResolveError))
                {
                    // 解析炸掉 ⇒ 當成本地缺檔處理（它絕對不是「通過」）。
                    aRow.IsRemote = false;
                    aRow.Exists = false;
                }
                else if (string.IsNullOrEmpty(aRow.Resolved))
                {
                    aRow.IsRemote = false;
                    aRow.Exists = false;
                    aRow.ResolveError = "ResolveURL 回傳空字串";
                }
                else if (aRow.Resolved.Contains("://"))
                {
                    aRow.IsRemote = true;
                }
                else
                {
                    aRow.Exists = System.IO.File.Exists(aRow.Resolved);
                }

                aRows.Add(aRow);
            }

            return aRows.OrderBy(r => r.TypeName, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // ===========================================================
        // 區塊職責：報告渲染 —— 四個數字擺在同一行，而不是一句「✅ 全部通過」。
        // 物理意義：判好的布林壞掉時跟它對的時候長得一模一樣；兩個數字擺在一起會自己說話。
        // ===========================================================
        private string Render(int iTotal, List<Row> iExternal, List<Row> iProject, List<Row> iRemote,
                              List<Row> iLocal, List<Row> iOk, List<Row> iMissing,
                              int iMaxList, bool iListOk, bool iListRemote, bool iListExternal)
        {
            var aSb = new StringBuilder();
            aSb.AppendLine("# 🔍 HelpUrlCheck — [HelpURL] 目標存在性");
            aSb.AppendLine();
            aSb.AppendLine($"- 語系（ResolveURL 的 {{lang}} 取值）：`{UCL.Core.Game.UCL_LocalizeService.CurLang}`");
            aSb.AppendLine($"- 解析器：`UCL_URL.ResolveURL` **本人**（⛔ 非重寫版）");
            aSb.AppendLine($"- 來源：反射 `HelpURLAttribute`，口徑＝AppDomain 全部型別（⛔ 非 grep 原始碼）");
            aSb.AppendLine($"- 分桶：組件是否由 `Assets/` 底下的原始碼編出（問 `CompilationPipeline` 本人，⛔ 非寫死名單）");
            aSb.AppendLine();
            aSb.AppendLine("## 讀數");
            aSb.AppendLine();
            aSb.AppendLine($"- 掃到 **{iTotal}** 條 `[HelpURL]`");
            aSb.AppendLine($"- 其中外部宣告 **{iExternal.Count}** 條（Unity／套件的組件 ⇒ 由它們自己的說明系統解，本 Cmd **不驗**）");
            aSb.AppendLine($"- 本專案宣告 **{iProject.Count}** 條 ⇒ 雲端 **{iRemote.Count}** 條（不驗）／本地 **{iLocal.Count}** 條");
            aSb.AppendLine($"- 本地那 **{iLocal.Count}** 條 ⇒ 解得到且檔案存在 **{iOk.Count}** 條 ／ 缺 **{iMissing.Count}** 條");
            aSb.AppendLine();
            aSb.AppendLine(iMissing.Count > 0
                ? $"⛔ **缺 {iMissing.Count} 條** —— 本 Cmd 以非零退出結束。"
                : "✅ 本地目標**零缺檔** —— 而這句只在上面三個數字同時在場時才有意義。");
            aSb.AppendLine();
            aSb.AppendLine("⚠ 射程：本檢查只回答「解得到路徑且檔案存在」。");
            aSb.AppendLine("⛔ 它**不宣稱**那顆 `?` 按鈕點下去會開出 MarkdownViewer —— 那是 GUI 行為讀數，本 Cmd 給不了。");

            if (iMissing.Count > 0)
            {
                aSb.AppendLine();
                aSb.AppendLine("## ⛔ 缺檔清單");
                aSb.AppendLine();
                AppendRows(aSb, iMissing, iMaxList, true);
            }

            if (iListRemote && iRemote.Count > 0)
            {
                aSb.AppendLine();
                aSb.AppendLine($"## 🌐 雲端目標（{iRemote.Count} 條，未驗）");
                aSb.AppendLine();
                AppendRows(aSb, iRemote, iMaxList, false);
            }

            if (iListExternal && iExternal.Count > 0)
            {
                aSb.AppendLine();
                aSb.AppendLine($"## 🧩 外部宣告（{iExternal.Count} 條，未驗、未解析）");
                aSb.AppendLine();
                AppendRows(aSb, iExternal, iMaxList, false);
            }

            if (iListOk && iOk.Count > 0)
            {
                aSb.AppendLine();
                aSb.AppendLine($"## ✓ 解得到且存在（{iOk.Count} 條）");
                aSb.AppendLine();
                AppendRows(aSb, iOk, iMaxList, false);
            }

            return aSb.ToString();
        }

        private void AppendRows(StringBuilder iSb, List<Row> iRows, int iMaxList, bool iWithError)
        {
            int aShown = 0;
            foreach (var aRow in iRows)
            {
                if (iMaxList > 0 && aShown >= iMaxList)
                {
                    iSb.AppendLine($"- …另有 {iRows.Count - aShown} 條未列出（`max_list=0` 看全部）");
                    break;
                }
                iSb.AppendLine($"- `{aRow.TypeName}` [{aRow.Assembly}]");
                iSb.AppendLine($"    · url：`{aRow.RawUrl}`");
                iSb.AppendLine($"    · 解析後：`{aRow.Resolved}`");
                if (iWithError && !string.IsNullOrEmpty(aRow.ResolveError))
                {
                    iSb.AppendLine($"    · ⚠ 解析本身失敗：{aRow.ResolveError}");
                }
                aShown++;
            }
        }

        private static int ParseInt(string iValue, int iDefault)
            => int.TryParse(iValue, out var aResult) ? aResult : iDefault;
    }
}
#endif
