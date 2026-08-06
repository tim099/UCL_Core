// 區塊職責：category routing group 的**直寫修復** — 補齊缺漏欄位 / 切換計酬旗標。
// 物理意義：這些 group 的真相源是 runtime 的
//          `<UnityAssets>/.BuiltinModules/ModulesRoot/Modules/Core/UCL_Assets/UCL_TavernCategoryRoutingAsset/<id>.json`
//          （Templates~ 只是新專案的初始模板，安裝後複製進 .BuiltinModules，之後的編輯都在那邊）。
// 數值影響：只改指定欄位，其餘欄位原樣保留（讀 → 改一個鍵 → 寫回），不重寫整份 schema。
//
// ⚠ 為什麼**直寫 JSON 而不是走 `UCL_Asset.Save()`**（Tim 2026-08-06 點名的坑）：
//   `UCL_Asset.SaveFolderPath => UCL_ModuleService.Ins.GetCurEditModuleFolder(...)` ——
//   落點取決於**當前編輯模組**。使用者若正在編輯別的 module，`Save()` 會把這份 group
//   寫進那個 module 的資料夾：**原檔沒被修，卻多出一份影子**，而畫面看起來存成功了。
//   這一族（寫對了內容、寫錯了地方）不會報錯，只會在某天讓人查不出設定為何沒生效。
//   同樣的規避手法 `UCL_AgentSkillManagerPage.ResolveSkillConfigDir` 已經有一份 ——
//   本檔把「往上找 .BuiltinModules」收攏成可共用的解析，不再各自抄第四份。
//
// 跨專案：路徑一律由 `UCL_EditorPath.CorePath` 往上走找 `.BuiltinModules`，
//        **不寫死任何專案的掛載位置**（見 ucl-core-paths）。找不到就明確回報找去了哪，
//        不 fail-soft return —— 靜默失敗會讓「按了沒反應」跟「UI 壞了」長得一樣。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UCL.Core.JsonLib;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.ChatTavern
{
    public static class UCL_TavernRoutingRepair
    {
        public const string PaidPostKey = "IsPaidPost";
        static readonly string[] AssetRel =
            { "ModulesRoot", "Modules", "Core", "UCL_Assets", "UCL_TavernCategoryRoutingAsset" };

        /// <summary>runtime 資料目錄（.BuiltinModules 底下）。找不到回空字串 —— 呼叫端負責出聲。</summary>
        public static string ResolveRuntimeDir(out string diagnostic)
        {
            diagnostic = "";
            string core = UCL_EditorPath.CorePath;
            if (string.IsNullOrEmpty(core)) { diagnostic = "UCL_EditorPath.CorePath 為空"; return ""; }
            string cur;
            try { cur = Path.GetFullPath(Path.Combine(UCL_RepoPath.UnityProjectRoot, core)); }
            catch (Exception e) { diagnostic = "路徑解析失敗：" + e.Message; return ""; }

            string start = cur;
            for (int i = 0; i < 10; i++)
            {
                string builtin = Path.Combine(cur, ".BuiltinModules");
                if (Directory.Exists(builtin))
                {
                    var parts = new List<string> { builtin };
                    parts.AddRange(AssetRel);
                    return Path.Combine(parts.ToArray());
                }
                string parent = Path.GetDirectoryName(cur);
                if (string.IsNullOrEmpty(parent) || parent == cur) break;
                cur = parent;
            }
            diagnostic = $"由 `{start}` 往上 10 層都找不到 .BuiltinModules";
            return "";
        }

        /// <summary>
        /// 區塊職責：把某個 group 的 `IsPaidPost` 設成指定值（欄位不存在就補上）。
        /// 數值影響：只動那一個鍵；其餘欄位（Categories / WebhookUrls / Description…）原樣寫回。
        /// 回傳：人看的結果字串（成功或失敗原因），呼叫端直接顯示。
        /// </summary>
        public static string SetPaidPost(string groupId, bool value)
        {
            string dir = ResolveRuntimeDir(out string diag);
            if (string.IsNullOrEmpty(dir)) return $"✗ 找不到資料目錄（{diag}）";
            string path = Path.Combine(dir, groupId + ".json");
            if (!File.Exists(path)) return $"✗ 找不到 group 檔：{path}";
            try
            {
                var json = JsonData.ParseJson(File.ReadAllText(path, Encoding.UTF8));
                bool had = json.Contains(PaidPostKey);
                json[PaidPostKey] = value ? "True" : "False";
                File.WriteAllText(path, json.ToJsonBeautify(), new UTF8Encoding(false));
                return $"✓ {groupId}.{PaidPostKey} = {value}"
                       + (had ? "" : "（原本**沒有這個欄位** —— 反序列化拿 bool 預設值 false，等於靜默不計酬）");
            }
            catch (Exception e) { return $"✗ 寫入失敗（{path}）：{e.Message}"; }
        }

        /// <summary>
        /// 區塊職責：健檢 —— 找出「欄位缺漏」與「預設群不計酬」兩種會靜默吃掉收入的狀態。
        /// 物理意義：這個洞已經吃過兩次收入（2026-07-07 group 整個遺失；2026-08-06 欄位缺漏），
        ///          而兩次的共通點是**沒有人喊痛**：計酬那條分支是刻意靜默的
        ///          （chitchat 等頻道本來就不計酬，不該吵）。所以要在**設定層**檢查，不是在發放層。
        /// 判準：`Enabled && IsDefault && !IsPaidPost` = 幾乎必然是漏設 ——
        ///      預設群接的是**所有未分類訊息**，真的不計酬的頻道不會是預設群，因此不會誤報。
        /// </summary>
        public static List<string> Diagnose()
        {
            var issues = new List<string>();
            string dir = ResolveRuntimeDir(out string diag);
            if (string.IsNullOrEmpty(dir)) { issues.Add($"找不到 routing 資料目錄（{diag}）"); return issues; }
            if (!Directory.Exists(dir)) { issues.Add($"routing 資料目錄不存在：{dir}"); return issues; }

            foreach (string f in Directory.GetFiles(dir, "*.json"))
            {
                string id = Path.GetFileNameWithoutExtension(f);
                try
                {
                    var j = JsonData.ParseJson(File.ReadAllText(f, Encoding.UTF8));
                    bool enabled = j.Contains("Enabled") && j["Enabled"].GetString() == "True";
                    bool isDefault = j.Contains("IsDefault") && j["IsDefault"].GetString() == "True";
                    bool hasPaid = j.Contains(PaidPostKey);
                    bool paid = hasPaid && j[PaidPostKey].GetString() == "True";

                    if (!hasPaid)
                        issues.Add($"`{id}` **缺少 {PaidPostKey} 欄位** → 反序列化拿 bool 預設值 false，"
                                   + "發文計酬會靜默停止（2026-07-30 判準由 IsWorkChannel 換成本欄，資料未同步補上）");
                    if (enabled && isDefault && !paid)
                        issues.Add($"`{id}` 是**啟用中的預設群卻不計酬** → 所有未分類訊息都不會 +1 token。"
                                   + "預設群接的是全部 fallback 流量，不計酬幾乎必然是漏設");
                }
                catch (Exception e) { issues.Add($"`{id}` 解析失敗：{e.Message}"); }
            }
            return issues;
        }
    }
}
#endif
