// 區塊職責：`UnityEditor.PropertyEditor` 的狀態探針 —— 查 Odin 那條
//          `InspectorConfig.UpdateOdinEditors → PropertyEditor.ClearEditorsAndRebuild` NRE 的觸發點。
// 物理意義：那條 NRE **炸在 Unity 內部**，而 Odin 是 DLL（讀不到內文）⇒ 只能從**呼叫那一刻的狀態**去逼。
//          `ClearEditorsAndRebuild` 會對每一個開著的 PropertyEditor 家族視窗
//          （`InspectorWindow` 也是它的子類）做重建；其中任何一顆處在半初始化／追蹤對象解不出來的
//          狀態，就會在那支方法裡 NRE。
//          ⇒ 本探針做兩件事：① 把每顆視窗的狀態逐顆攤開 ② 用同一支方法**故意重現一次**。
// 數值影響：
//          · `Dump` / `DumpToFile` **純讀**（只反射讀欄位，不寫任何狀態）。
//          · `TryRebuild` **會真的呼叫那支方法** —— 它是「刻意重現」，不是檢查。
//            ⚠ 它就是那條 NRE 的呼叫路徑本身；重現成功＝Console 會多一條同樣的例外。
//
// 🩸 為什麼一定要落檔而不是只回傳字串（TASK-0172）：
//   `Cmd_Invoke` 的回傳值**只進 `Debug.Log`，到不了呼叫端** ——
//   「Success」與「拿到讀數」在 CLI 那一端同形。
//   ⇒ 所以每支公開入口都**自己把報告寫成檔案並回傳路徑**：
//     路徑是短字串，就算只剩 Debug.Log 也讀得到；而內容在磁碟上，agent 撈得走。
//
// 用法（Cmd_Invoke —— Tim 2026-09-15 指定的測試通道）：
//   senate ucmd run Invoke --persona <me> \
//       --arg type=UCL.Core.EditorLib.Diagnostics.UCL_PropertyEditorProbe \
//       --arg member=DumpToFile
//   senate ucmd run Invoke --persona <me> \
//       --arg type=UCL.Core.EditorLib.Diagnostics.UCL_PropertyEditorProbe \
//       --arg member=TryRebuildToFile
//   ⇒ 兩者都回傳**報告檔路徑**（落在 `<專案>/Library/`，gitignore 內，屬 ephemeral）。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UCL.Core.EditorLib.Diagnostics
{
    /// <summary>
    /// `UnityEditor.PropertyEditor` 狀態探針。查「Odin 重建 Inspector 時 NRE」的觸發點用。
    /// </summary>
    public static class UCL_PropertyEditorProbe
    {
        const string TYPE_NAME = "UnityEditor.PropertyEditor";
        const string REBUILD_METHOD = "ClearEditorsAndRebuild";

        const BindingFlags ALL_INSTANCE =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags ALL_STATIC =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        // 區塊職責：報告落點。
        // 物理意義：`Library/` 在 .gitignore 內 ⇒ 探針產物天生不會被誤 commit（它是 ephemeral）。
        // 數值影響：固定檔名 ⇒ 每次覆寫。要留存就自己另存，⛔ 本探針不做輪替
        //          （輪替會長出一堆沒有人會回頭看的檔）。
        static string ReportPath =>
            Path.Combine(Directory.GetParent(Application.dataPath).FullName,
                         "Library", "UCL_PropertyEditorProbe.md");

        // ===========================================================
        // 區塊職責：公開入口（給 Cmd_Invoke 用）—— 都回傳**檔案路徑**。
        // 物理意義：見檔頭 TASK-0172 那段。路徑短，就算只剩 Debug.Log 也讀得到。
        // 數值影響：Dump 系列純讀；TryRebuild 系列會真的觸發那支方法。
        // ===========================================================

        /// <summary>攤開所有 PropertyEditor 視窗的狀態，寫檔，回傳檔案路徑。**純讀。**</summary>
        public static string DumpToFile()
        {
            return WriteReport(Dump());
        }

        /// <summary>
        /// ⚠ **刻意重現**：用跟 Odin 同一支方法呼叫 `ClearEditorsAndRebuild`，
        /// 前後各攤一次狀態，寫檔，回傳檔案路徑。
        /// </summary>
        public static string TryRebuildToFile()
        {
            var aSb = new StringBuilder();
            aSb.AppendLine("# TryRebuild —— 刻意重現 `PropertyEditor.ClearEditorsAndRebuild`");
            aSb.AppendLine();
            aSb.AppendLine("## ① 呼叫前的狀態");
            aSb.AppendLine();
            aSb.Append(Dump());
            aSb.AppendLine();
            aSb.AppendLine("## ② 呼叫");
            aSb.AppendLine();
            aSb.Append(InvokeRebuild());
            aSb.AppendLine();
            aSb.AppendLine("## ③ 呼叫後的狀態");
            aSb.AppendLine();
            aSb.Append(Dump());
            return WriteReport(aSb.ToString());
        }

        // ===========================================================
        // 區塊職責：把每顆視窗的**所有** instance 欄位攤開，標出誰是 null。
        // 物理意義：知道「哪顆視窗會炸」之後，下一個問題是「炸在哪個欄位」——
        //          而 `ClearEditorsAndRebuild` 在 DLL 裡讀不到內文 ⇒ 只能用**對照組**逼：
        //          會炸的那顆有、不會炸的那顆沒有的 null，就是嫌疑。
        // 數值影響：純讀。⚠ 欄位很多（PropertyEditor 家族數十個）⇒ 只列 null 與 Unity 假 null，
        //          非 null 的只給計數 —— 全列會把訊號淹掉。
        /// <summary>攤開每顆視窗的 null 欄位（找 NRE 的嫌疑欄位用），寫檔，回傳路徑。**純讀。**</summary>
        public static string DumpNullFieldsToFile()
        {
            var aSb = new StringBuilder();
            aSb.AppendLine("# 欄位對拍 —— 找出「會炸的視窗有、不會炸的沒有」的那個 null");
            aSb.AppendLine();
            aSb.AppendLine("⚠ 只列 **null** 與 **Unity 假 null**（已銷毀但參照還在）。");
            aSb.AppendLine("　 非 null 的欄位只給計數 —— 全列會把訊號淹掉。");
            aSb.AppendLine();

            Type aType = FindPropertyEditorType();
            if (aType == null) return WriteReport(aSb.AppendLine("❌ 找不到型別。").ToString());

            var aWindows = Resources.FindObjectsOfTypeAll(aType);
            if (aWindows == null || aWindows.Length == 0)
                return WriteReport(aSb.AppendLine("⚠ 沒有任何視窗。").ToString());

            foreach (var aWin in aWindows)
            {
                if (aWin == null) continue;
                aSb.AppendLine($"## `{aWin.GetType().Name}`　title: `{SafeTitle(aWin)}`");
                aSb.AppendLine();

                int aTotal = 0, aNullCount = 0;
                var aNulls = new List<string>();
                for (Type aT = aWin.GetType(); aT != null && aT != typeof(object); aT = aT.BaseType)
                {
                    foreach (var aField in aT.GetFields(ALL_INSTANCE | BindingFlags.DeclaredOnly))
                    {
                        ++aTotal;
                        object aVal;
                        try { aVal = aField.GetValue(aWin); }
                        catch (Exception e) { aNulls.Add($"`{aT.Name}.{aField.Name}` ⚠ 讀取丟例外 {e.GetType().Name}"); continue; }

                        if (aVal == null)
                        {
                            ++aNullCount;
                            aNulls.Add($"`{aT.Name}.{aField.Name}` : `{aField.FieldType.Name}` = **null**");
                            continue;
                        }
                        // Unity 假 null：已銷毀的 UnityEngine.Object，`== null` 為 true 但參照不是 null。
                        // ⭐ 這一種比真 null 更值得看 —— 它正是「關聯到已經刪除的東西」的形狀。
                        if (aVal is UnityEngine.Object aUo && aUo == null)
                        {
                            ++aNullCount;
                            aNulls.Add($"`{aT.Name}.{aField.Name}` : `{aField.FieldType.Name}` = 🔴 **已銷毀（假 null）**");
                        }
                    }
                }

                aSb.AppendLine($"欄位總數 **{aTotal}**，其中 null／已銷毀 **{aNullCount}**");
                aSb.AppendLine();
                if (aNulls.Count == 0) aSb.AppendLine("（沒有 null 欄位）");
                else foreach (var aLine in aNulls) aSb.AppendLine($"- {aLine}");
                aSb.AppendLine();
            }

            aSb.AppendLine("---");
            aSb.AppendLine("⇒ **比對兩顆視窗的清單**：會炸的那顆有、不會炸的那顆沒有的，就是嫌疑欄位。");
            return WriteReport(aSb.ToString());
        }

        /// <summary>純字串版（給 GUI / 手動呼叫）。**純讀。**</summary>
        public static string Dump()
        {
            var aSb = new StringBuilder();
            aSb.AppendLine($"ts: `{DateTime.Now:yyyy-MM-dd HH:mm:ss}`　Unity {Application.unityVersion}");
            // ⭐ 當前選取一起印：視窗的 instanceID 是「它正在看誰」，而那多半就是 Selection。
            //   兩者對不上本身就是讀數，⇒ 給讀的人一個對照組，別讓他只能看一欄猜。
            var aSel = Selection.activeObject;
            aSb.AppendLine($"當前 `Selection.activeObject`: " + (aSel == null
                ? "（無 —— 現在沒選任何東西）"
                : $"`{aSel.GetType().Name}` `{aSel.name}`（instanceID `{aSel.GetInstanceID()}`）"));
            aSb.AppendLine();

            Type aType = FindPropertyEditorType();
            if (aType == null)
            {
                // ⚠ 找不到型別與「找到了但沒有視窗」**不是同一件事** —— 前者代表 Unity 版本換了
                //   （型別改名／搬家），那時整支探針的讀數都不能信。
                aSb.AppendLine($"❌ 找不到型別 `{TYPE_NAME}` —— Unity 版本可能換過（型別改名或搬家）。");
                aSb.AppendLine("⛔ 在這個情況下，本探針底下**所有讀數都不成立**，不要拿它下結論。");
                return aSb.ToString();
            }

            aSb.AppendLine($"型別: `{aType.FullName}`（assembly `{aType.Assembly.GetName().Name}`）");

            var aWindows = Resources.FindObjectsOfTypeAll(aType);
            aSb.AppendLine($"開著的 PropertyEditor 家族視窗: **{(aWindows == null ? 0 : aWindows.Length)}** 顆");
            aSb.AppendLine();

            if (aWindows == null || aWindows.Length == 0)
            {
                // ⭐ 這一格是好消息還是壞消息，讀的人要分得出來。
                aSb.AppendLine("⚠ **一顆都沒有。** 若此時仍然重現得出那條 NRE，");
                aSb.AppendLine("　 ⇒ 兇手就**不是視窗狀態**，要改查 Odin 自己的 drawing config。");
                return aSb.ToString();
            }

            for (int i = 0; i < aWindows.Length; ++i)
            {
                aSb.Append(DumpOne(aWindows[i], i));
                aSb.AppendLine();
            }
            aSb.AppendLine(Legend());
            return aSb.ToString();
        }

        // ===========================================================
        // 區塊職責：一顆視窗的狀態。
        // 物理意義：欄位名取自 Unity **自己序列化出來的 layout 檔**
        //          （`UserSettings/Layouts/*.dwlt` 裡逐字出現過）⇒ 不是猜的。
        //          ⚠ 但它們是 internal，Unity 換版可能改名 ⇒ 每一格都標「解不解得到」，
        //            ⛔ 讓「欄位不存在」與「欄位是 null」**不同形**。
        // 數值影響：純讀。
        // ===========================================================
        static string DumpOne(UnityEngine.Object iWindow, int iIndex)
        {
            var aSb = new StringBuilder();
            if (iWindow == null)
            {
                aSb.AppendLine($"### [{iIndex}] 🔴 **這一格本身是 null**（已銷毀卻還在清單上）");
                return aSb.ToString();
            }

            Type aRt = iWindow.GetType();
            string aTitle = SafeTitle(iWindow);
            aSb.AppendLine($"### [{iIndex}] `{aRt.Name}`　title: `{aTitle}`");

            // tracker —— NRE 最可能的那一格
            var aTracker = ReadMember(iWindow, "m_Tracker", out bool aTrackerFound);
            if (!aTrackerFound) aTracker = ReadMember(iWindow, "tracker", out aTrackerFound);
            aSb.AppendLine($"- `m_Tracker`: {(aTrackerFound ? (aTracker == null ? "🔴 **null**" : "✅ 有") : "⚠ 讀不到這個成員")}");

            // 追蹤對象：instanceID 能不能解回一個物件
            var aIdObj = ReadMember(iWindow, "m_LastInspectedObjectInstanceID", out bool aIdFound);
            if (aIdFound && aIdObj is int aId)
            {
                // 🩸 2026-09-15 實測修正：初版只把 `0` 當「沒有在看東西」，於是把 **-1 報成 🔴 壞掉**。
                //    實機讀數：兩顆視窗都是 `-1` 且 `activeEditors` 為 0 ⇒ 那是「現在沒選任何東西」。
                //    ⇒ 這正是本工具要防的那個錯的**反面**：把「乾淨的沒有」報成「壞的」。
                //    一份會亂叫的報告跟沒有報告一樣沒用，而它看起來更權威。
                // ⚠ 定語：Unity 的 instanceID **負數是合法的**（場景物件就是負的）——
                //    所以 `-1` 是**慣例上的哨兵**，不是型別保證。⛔ 不把「所有負數」當哨兵。
                bool aSentinel = (aId == 0 || aId == -1);
                var aResolved = aSentinel ? null : EditorUtility.InstanceIDToObject(aId);
                string aVerdict = aSentinel
                    ? $"（{aId} ＝ 哨兵值：現在沒有在看任何東西，正常）"
                    : aResolved == null
                        ? "🔴 **解不出物件** ⇒ 它指著一個已經不在的東西"
                        : $"✅ → `{aResolved.GetType().Name}` `{aResolved.name}`";
                aSb.AppendLine($"- `m_LastInspectedObjectInstanceID`: `{aId}` {aVerdict}");
            }
            else
            {
                aSb.AppendLine("- `m_LastInspectedObjectInstanceID`: ⚠ 讀不到這個成員");
            }

            // GlobalObjectId —— 跨 domain reload 重新解析身分用的那一格。
            // ⚠ 沒在看東西時它本來就是空的 ⇒ 只有在「有在看東西卻沒有 GlobalObjectId」時才值得警告，
            //   否則每次沒選東西都會亮一個假警報（而假警報會訓練人忽略真警報）。
            var aGid = ReadMember(iWindow, "m_GlobalObjectId", out bool aGidFound);
            string aGidStr = aGid as string;
            bool aWatchingSomething = aIdFound && aIdObj is int aId2 && aId2 != 0 && aId2 != -1;
            aSb.AppendLine($"- `m_GlobalObjectId`: " + (!aGidFound
                ? "⚠ 讀不到這個成員"
                : string.IsNullOrEmpty(aGidStr)
                    ? (aWatchingSomething
                        ? "⚠ **空，但它正在看某個東西** ⇒ reload 之後沒有穩定身分可重新解析（只剩會話內的 instanceID）"
                        : "（空 —— 沒在看東西時本來就是空的，正常）")
                    : $"`{aGidStr}`"));

            // PreviewWindow 是 PropertyEditor 家族的一員（實測 2026-09-15 才發現），
            // 而它掛著一顆父 Inspector。⭐ 父視窗沒了而預覽窗還在，是重建時很典型的 null 來源。
            var aParent = ReadMember(iWindow, "m_ParentInspectorWindow", out bool aParentFound);
            if (aParentFound)
            {
                // ⚠ UnityEngine.Object 的「假 null」：已銷毀的物件 `== null` 為 true 但參照不是 null。
                //   ⇒ 用 Unity 的比較（轉成 UnityEngine.Object 再比），不用 `is null`。
                var aParentObj = aParent as UnityEngine.Object;
                aSb.AppendLine("- `m_ParentInspectorWindow`: " + (aParent == null
                    ? "🔴 **null**"
                    : aParentObj == null
                        ? "🔴 **已銷毀**（參照還在、物件沒了）"
                        : $"✅ `{aParentObj.GetType().Name}`"));
            }

            var aMode = ReadMember(iWindow, "m_InspectorMode", out bool aModeFound);
            if (aModeFound) aSb.AppendLine($"- `m_InspectorMode`: `{aMode}`");

            var aLock = ReadMember(iWindow, "m_LockTracker", out bool aLockFound);
            if (aLockFound && aLock != null)
            {
                var aIsLocked = ReadMember(aLock, "isLocked", out bool aIsLockedFound);
                if (!aIsLockedFound) aIsLocked = ReadMember(aLock, "m_IsLocked", out aIsLockedFound);
                aSb.AppendLine($"- `m_LockTracker.isLocked`: {(aIsLockedFound ? aIsLocked?.ToString() : "⚠ 讀不到")}");
            }

            // 編輯器陣列：tracker 有了也可能裡面有 null
            if (aTracker != null)
            {
                var aEditors = ReadMember(aTracker, "activeEditors", out bool aEdFound);
                if (aEdFound && aEditors is Array aArr)
                {
                    int aNull = 0;
                    foreach (var aE in aArr) if (aE == null) ++aNull;
                    aSb.AppendLine($"- `tracker.activeEditors`: {aArr.Length} 個"
                        + (aNull > 0 ? $"，其中 🔴 **{aNull} 個是 null**" : "，沒有 null"));
                }
            }

            return aSb.ToString();
        }

        static string Legend()
        {
            var aSb = new StringBuilder();
            aSb.AppendLine("---");
            aSb.AppendLine("## 怎麼讀這份報告");
            aSb.AppendLine();
            aSb.AppendLine("任何一顆視窗出現 🔴 ⇒ 它就是 `ClearEditorsAndRebuild` 走進去會炸的那一格。");
            aSb.AppendLine();
            aSb.AppendLine("| 看到 | 意思 | 下一步 |");
            aSb.AppendLine("|---|---|---|");
            aSb.AppendLine("| `m_Tracker` 是 null | 視窗還沒 `OnEnable` 完就被要求重建 | 時機問題 —— 跟 domain reload 賽跑 |");
            aSb.AppendLine("| instanceID 解不出物件 | 它指著一個已經不在的東西 | 選一個別的物件再 reload 一次看還會不會 |");
            aSb.AppendLine("| `activeEditors` 裡有 null | 某個 Editor 建不起來 | 多半是被檢視物件上有缺腳本 Component ⇒ 走 Missing Reference 排查頁 |");
            aSb.AppendLine("| 全部 ✅ 而 NRE 照樣重現 | **不是視窗狀態** | 改查 Odin 的 `InspectorConfig.drawingConfig` |");
            aSb.AppendLine();
            aSb.AppendLine("⛔ **全部 ✅ 不等於沒問題** —— 本探針跑的時間點跟 Odin 的靜態建構子**不是同一刻**。");
            aSb.AppendLine("　 那條 NRE 的嫌疑時機是「reload 後、視窗還沒 `OnEnable`」，而那一刻本探針進不去。");
            aSb.AppendLine("　 ⇒ 手動重現不出來**也是一個讀數**：它把嫌疑收窄到那個時間窗。");
            return aSb.ToString();
        }

        // ===========================================================
        // 區塊職責：用跟 Odin 同一支方法呼叫 `ClearEditorsAndRebuild`。
        // 物理意義：Odin 走的是 `MethodBase.Invoke` ⇒ 本探針也走反射，**不走任何公開包裝**，
        //          否則重現的就不是同一條路。
        // 數值影響：⚠ **會真的重建所有 Inspector**。它就是那條 NRE 的呼叫路徑本身。
        //          例外在這裡接住並記進報告 —— ⛔ 不讓它變成 Console 裡一條看起來像自然發生的錯。
        // ===========================================================
        static string InvokeRebuild()
        {
            var aSb = new StringBuilder();
            Type aType = FindPropertyEditorType();
            if (aType == null)
            {
                aSb.AppendLine($"❌ 找不到型別 `{TYPE_NAME}`，無法重現。");
                return aSb.ToString();
            }

            MethodInfo aStatic = aType.GetMethod(REBUILD_METHOD, ALL_STATIC, null, Type.EmptyTypes, null);
            MethodInfo aInstance = aType.GetMethod(REBUILD_METHOD, ALL_INSTANCE, null, Type.EmptyTypes, null);

            if (aStatic == null && aInstance == null)
            {
                aSb.AppendLine($"❌ 型別上找不到 `{REBUILD_METHOD}()`（無參數多載）。");
                aSb.AppendLine("⇒ Unity 換版可能改了簽章 —— 先確認 stack 裡那支方法今天還在不在。");
                return aSb.ToString();
            }

            if (aStatic != null)
            {
                aSb.AppendLine($"呼叫方式：**static** `{aType.Name}.{REBUILD_METHOD}()`");
                aSb.Append(InvokeOne(aStatic, null, "static"));
                return aSb.ToString();
            }

            // instance 版 ⇒ 逐顆視窗各叫一次，並**逐顆回報**：
            // 只回報「有沒有炸」的話，分不出是哪一顆炸的，而那正是本探針存在的理由。
            aSb.AppendLine($"呼叫方式：**instance** `{aType.Name}.{REBUILD_METHOD}()`（逐顆視窗各叫一次）");
            var aWindows = Resources.FindObjectsOfTypeAll(aType);
            if (aWindows == null || aWindows.Length == 0)
            {
                aSb.AppendLine("⚠ 沒有任何視窗可呼叫 ⇒ 這一次什麼都沒重現（不是「沒問題」）。");
                return aSb.ToString();
            }
            for (int i = 0; i < aWindows.Length; ++i)
            {
                aSb.Append(InvokeOne(aInstance, aWindows[i], $"[{i}] {SafeTitle(aWindows[i])}"));
            }
            return aSb.ToString();
        }

        static string InvokeOne(MethodInfo iMethod, object iTarget, string iLabel)
        {
            try
            {
                iMethod.Invoke(iTarget, null);
                return $"- {iLabel}：✅ 沒有丟例外\n";
            }
            catch (TargetInvocationException e)
            {
                // ⭐ 這就是 Odin 那份 stack 裡的 `Rethrow as TargetInvocationException` ——
                //    真正的兇手在 InnerException，⛔ 不要只印外層。
                var aInner = e.InnerException;
                return $"- {iLabel}：🔴 **重現了** `{aInner?.GetType().Name}`：{aInner?.Message}\n"
                     + $"```\n{aInner?.StackTrace}\n```\n";
            }
            catch (Exception e)
            {
                return $"- {iLabel}：🔴 `{e.GetType().Name}`：{e.Message}\n";
            }
        }

        #region 反射小工具

        static Type FindPropertyEditorType()
        {
            // 先問 UnityEditor assembly（它一定載了），找不到再掃全部 —— ⛔ 不一開始就掃全部，
            // 那會在每次呼叫時走過上百個 assembly，而它幾乎永遠命中第一個。
            var aType = typeof(EditorWindow).Assembly.GetType(TYPE_NAME, false);
            if (aType != null) return aType;

            foreach (var aAsm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    aType = aAsm.GetType(TYPE_NAME, false);
                    if (aType != null) return aType;
                }
                catch (Exception) { /* 有些動態 assembly 問型別會丟，跳過 */ }
            }
            return null;
        }

        // ⚠ `oFound` 是必要的：讀不到成員（Unity 換版改名）與讀到 null **要分得出來** ——
        //   兩者都回 null 的話，一份「全是 null」的報告看起來就像「全壞了」，
        //   而真相可能是這支探針整個對不上這個 Unity 版本。
        static object ReadMember(object iObj, string iName, out bool oFound)
        {
            oFound = false;
            if (iObj == null) return null;
            for (Type aT = iObj.GetType(); aT != null; aT = aT.BaseType)
            {
                var aField = aT.GetField(iName, ALL_INSTANCE);
                if (aField != null) { oFound = true; return aField.GetValue(iObj); }

                var aProp = aT.GetProperty(iName, ALL_INSTANCE);
                if (aProp != null && aProp.CanRead)
                {
                    try { oFound = true; return aProp.GetValue(iObj); }
                    catch (Exception e) { oFound = true; return $"(讀取時丟例外：{e.GetType().Name})"; }
                }
            }
            return null;
        }

        static string SafeTitle(UnityEngine.Object iWindow)
        {
            try
            {
                var aWin = iWindow as EditorWindow;
                if (aWin != null && aWin.titleContent != null) return aWin.titleContent.text;
            }
            catch (Exception) { /* 半死的視窗連 titleContent 都可能丟 —— 那本身就是讀數 */ }
            return iWindow == null ? "(null)" : iWindow.name;
        }

        static string WriteReport(string iBody)
        {
            string aPath = ReportPath;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(aPath));
                File.WriteAllText(aPath, "# UCL_PropertyEditorProbe\n\n" + iBody, Encoding.UTF8);
                Debug.Log($"[PropertyEditorProbe] 報告：{aPath}");
                return aPath;
            }
            catch (Exception e)
            {
                // ⛔ 落檔失敗時**不要**把報告吞掉：這支的整個價值就是那份內容。
                Debug.LogError($"[PropertyEditorProbe] 寫檔失敗（{aPath}）：{e.Message}");
                Debug.Log(iBody);
                return $"WRITE_FAILED: {e.Message}";
            }
        }

        #endregion
    }
}
#endif
