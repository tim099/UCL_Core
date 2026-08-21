// 區塊職責：自動 commit 的**分群設定檔**（`.ucl_autocommit.json`，放各 repo 根）——
//          讓「這個 repo 的機器生成檔怎麼分群」由該 repo 自己宣告，而不是寫死在 UCL_Core。
// 物理意義：Tim 2026-08-21 拍板。原本規則是兩組寫死的 `GroupDef[]`（agent / letters），
//          ⇒ 每接一個新的資料 repo（Chess 是第一個）就要回頭改 UCL_Core 加一組寫死的。
//          設定檔化之後：掃到的 submodule 有這個檔就照它分群，沒有就跳過（**不猜**）。
//
// ⚠ 這推翻了 2026-08-07 的「規則寫在程式碼、不開放編輯」拍板。撤銷理由寫在這裡而不是只寫在
//   commit 訊息裡 —— 拍板的撤銷要跟拍板放同一個地方，否則下一個人只看得到結論看不到帳：
//   當時的理由是「能在 UI 亂改的規則等於沒有規則」，而那句針對的是**執行期參數**
//   （`--arg groups=…` 那種：不留痕跡、事後查不到誰改的）。
//   設定檔不是參數：它**入版控、由它管的那個 repo 自己擁有、改動在 diff 裡看得見**。
//   ⇒ 所以做法是「可宣告、但掀不動地板」，不是「全面開放」。
//
// 數值影響（地板在哪、為什麼設定檔掀不動它）：
//   · `UCL_AutoCommitRules.Classify` 的判定順序是 **subptr → ephemeral → 分群**。
//     ⇒ ephemeral 在分群**之前**就 return null，設定檔寫什麼前綴都碰不到它。
//     這是**結構保證**（順序），不是「呼叫端記得先檢查」—— 靠記得的地板不是地板。
//   · `__other` / `__subptr` 仍然只在顯式要求時才收，設定檔不能改變這件事。
//   · 只吃**前綴清單**，不吃 regex。刻意的：`UCL_AutoCommitRules` 的區塊註解自己寫著
//     「錯配是『檔進錯 commit』等級，規則要一眼能驗證」⇒ 設定檔要比 code **更受限**，不是更自由。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UCL.Core.JsonLib;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>設定檔裡的一群。對應 <see cref="UCL_AutoCommitRules.GroupDef"/>，但 Match 只能是前綴清單。</summary>
    public class UCL_AutoCommitGroupConfig : UnityJsonSerializable
    {
        /// <summary>群 key（commit 分組用；不可與 `__other` / `__subptr` 撞名）。</summary>
        public string m_Key = "";
        /// <summary>畫面上顯示的群名。作者自己寫，所以不進多語系表。</summary>
        public string m_Label = "";
        /// <summary>相對 repo root 的**正斜線前綴**清單，任一命中即屬本群。空字串不合法（會吃掉整個 repo）。</summary>
        public List<string> m_MatchPrefixes = new List<string>();
        /// <summary>commit 訊息主體（檔數統計由呼叫端補在後面）。</summary>
        public string m_Message = "";
        /// <summary>頁面/Cmd 預設是否勾選。</summary>
        public bool m_DefaultOn = true;

        // ⚠ bool 經 SaveFieldsToJsonUnityVer 會寫成 "True"/"False" **字串**，而 python 讀到 "False" 是 truthy。
        //   這個檔預期會被非 C# 端讀（工具腳本 / 人手改），所以寫回原生 bool。
        //   （判準是「有沒有別的語言在讀」，見 Coding_Standards「換成 typed model 時的三個坑」②。）
        public override JsonData SerializeToJson()
        {
            var aData = base.SerializeToJson();
            aData["DefaultOn"] = new JsonData(m_DefaultOn);
            return aData;
        }
    }

    /// <summary>一個 repo 的自動提交設定。檔案位置：<c>&lt;repoRoot&gt;/.ucl_autocommit.json</c>。</summary>
    public class UCL_AutoCommitConfig : UnityJsonSerializable
    {
        /// <summary>設定檔檔名（放 repo 根）。</summary>
        public const string FileName = ".ucl_autocommit.json";

        /// <summary>顯示用的 repo 名稱；空的話呼叫端用目錄名。</summary>
        public string m_Name = "";
        /// <summary>這個 repo 的分群（順序即優先序，第一個命中的收走）。</summary>
        public List<UCL_AutoCommitGroupConfig> m_Groups = new List<UCL_AutoCommitGroupConfig>();

        /// <summary>反斜線的碼點。**刻意不寫字面值** —— 這個檔多次由腳本產生／修改，
        /// 而反斜線在 shell→python→檔案 這條鏈上會被多解一層（2026-08-21 三次血證）。</summary>
        const char BackSlash = (char)92;

        // 區塊職責：找出「自己帶設定檔」的 submodule。
        // 物理意義：**設定檔是加入的唯一憑據** —— 沒有設定檔就不收（不猜規則）。
        //          判準刻意不是「是不是 submodule」：那會把所有 persona 信件庫與別人的資料庫
        //          一起掃進來，而那些 repo 的分群規則不住這裡。
        // ⚠ 這支是**唯一的發現實作**（Cmd_AutoCommit 與 UCL_AutoCommitPage 共用）。
        //   頁面自己再寫一份掃描的話，兩邊遲早對「有哪些 repo」給出不同答案，而兩邊都不報錯。
        public static List<string> DiscoverRepoPaths(string iDataRoot)
        {
            var aList = new List<string>();
            if (string.IsNullOrEmpty(iDataRoot) || !Directory.Exists(iDataRoot)) return aList;
            string aRoot = iDataRoot.Replace(BackSlash, '/');
            string aGitModules = Path.Combine(aRoot, ".gitmodules");
            if (!File.Exists(aGitModules)) return aList;
            foreach (string aRawLine in File.ReadAllLines(aGitModules))
            {
                string aLine = aRawLine.Trim();
                if (!aLine.StartsWith("path")) continue;
                int aEq = aLine.IndexOf('=');
                if (aEq < 0) continue;
                string aRel = aLine.Substring(aEq + 1).Trim();
                if (aRel.Length == 0) continue;
                string aDir = (aRoot + "/" + aRel).Replace(BackSlash, '/');
                if (!Directory.Exists(aDir)) continue;
                if (!Exists(aDir)) continue;
                aList.Add(aDir);
            }
            aList.Sort(StringComparer.Ordinal);
            return aList;
        }

        public static string PathOf(string iRepoRoot)
            => Path.Combine(iRepoRoot, FileName).Replace('\\', '/');

        public static bool Exists(string iRepoRoot)
            => !string.IsNullOrEmpty(iRepoRoot) && File.Exists(PathOf(iRepoRoot));

        /// <summary>讀取；檔案不存在回 null。格式壞掉**丟例外不回 null** ——
        /// 「壞檔」與「沒有檔」必須是兩種可分辨的結果，否則設定寫錯的症狀會長得跟「這個 repo 沒設定」一樣。</summary>
        public static UCL_AutoCommitConfig Load(string iRepoRoot)
        {
            string aPath = PathOf(iRepoRoot);
            if (!File.Exists(aPath)) return null;
            string aText = File.ReadAllText(aPath);
            var aJson = JsonData.ParseJson(aText);
            if (aJson == null) throw new System.Exception($"[AutoCommitConfig] 解析失敗：{aPath}");
            var aConfig = new UCL_AutoCommitConfig();
            aConfig.DeserializeFromJson(aJson);
            return aConfig;
        }

        /// <summary>寫回設定檔（UTF-8 無 BOM —— Encoding.UTF8 會寫 BOM，python 端讀到會炸）。</summary>
        public void Save(string iRepoRoot)
        {
            var aErrors = Validate();
            if (aErrors.Count > 0)
                throw new System.Exception("[AutoCommitConfig] 設定不合法，未寫入：\n" + string.Join("\n", aErrors));
            string aPath = PathOf(iRepoRoot);
            File.WriteAllText(aPath, SerializeToJson().ToJsonBeautify(),
                new System.Text.UTF8Encoding(false));
        }

        /// <summary>合法性檢查。回傳空清單＝過。**寫入前必跑** —— 錯配等級是「檔進錯 commit」。</summary>
        public List<string> Validate()
        {
            var aErrors = new List<string>();
            var aSeen = new HashSet<string>();
            for (int i = 0; i < m_Groups.Count; ++i)
            {
                var aGroup = m_Groups[i];
                string aWhere = $"第 {i + 1} 群";
                if (aGroup == null) { aErrors.Add($"{aWhere}：空的"); continue; }
                if (string.IsNullOrWhiteSpace(aGroup.m_Key)) aErrors.Add($"{aWhere}：Key 不可空白");
                else
                {
                    if (aGroup.m_Key == UCL_AutoCommitRules.KEY_OTHER || aGroup.m_Key == UCL_AutoCommitRules.KEY_SUBPTR)
                        aErrors.Add($"{aWhere}：Key '{aGroup.m_Key}' 是保留群，不可自訂");
                    if (!aSeen.Add(aGroup.m_Key)) aErrors.Add($"{aWhere}：Key '{aGroup.m_Key}' 重複");
                }
                if (string.IsNullOrWhiteSpace(aGroup.m_Message)) aErrors.Add($"{aWhere}：Message 不可空白（那是 commit 訊息）");
                int aValidPrefix = 0;
                foreach (var aPrefix in aGroup.m_MatchPrefixes)
                {
                    if (string.IsNullOrEmpty(aPrefix))
                    {
                        // 空前綴會 StartsWith 命中**每一個檔** ⇒ 一群吃掉整個 repo，而它不會報錯。
                        aErrors.Add($"{aWhere}：前綴不可為空字串（會吃掉整個 repo）");
                        continue;
                    }
                    if (aPrefix.Contains("\\"))
                        aErrors.Add($"{aWhere}：前綴 '{aPrefix}' 含反斜線 —— 比對用的是正斜線相對路徑");
                    ++aValidPrefix;
                }
                if (aValidPrefix == 0) aErrors.Add($"{aWhere}：至少要一個前綴");
            }
            return aErrors;
        }

        /// <summary>轉成分群規則。Match 一律是「正斜線相對路徑的前綴命中」。</summary>
        public UCL_AutoCommitRules.GroupDef[] ToGroupDefs()
        {
            var aDefs = new List<UCL_AutoCommitRules.GroupDef>();
            foreach (var aGroup in m_Groups)
            {
                if (aGroup == null || string.IsNullOrWhiteSpace(aGroup.m_Key)) continue;
                var aPrefixes = new List<string>();
                foreach (var aPrefix in aGroup.m_MatchPrefixes)
                    if (!string.IsNullOrEmpty(aPrefix)) aPrefixes.Add(aPrefix);
                aDefs.Add(new UCL_AutoCommitRules.GroupDef
                {
                    Key = aGroup.m_Key,
                    Label = string.IsNullOrEmpty(aGroup.m_Label) ? aGroup.m_Key : aGroup.m_Label,
                    Match = p =>
                    {
                        foreach (var aPrefix in aPrefixes) if (p.StartsWith(aPrefix)) return true;
                        return false;
                    },
                    Message = aGroup.m_Message,
                    DefaultOn = aGroup.m_DefaultOn,
                });
            }
            return aDefs.ToArray();
        }
    }
}
#endif
