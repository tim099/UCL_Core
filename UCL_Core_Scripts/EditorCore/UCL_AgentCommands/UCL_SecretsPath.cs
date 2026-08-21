// 區塊職責：secrets 資料夾**名稱**的唯一解析點（可配置、寫檔、後台可改）。
// 物理意義：Tim 2026-08-21：「路徑是否可以改為非硬編碼（可以把相對路徑寫檔，Page 上可以改，預設 Secret）」。
//          原本 `"AgentCommands/_secrets"` 這個字面值散在 **7 處 code、兩種語言**
//          （scanner 常數／3 處 Path.Combine／2 支 python／文件），改名等於七處同步 ——
//          而漏一處的症狀是靜默的：Discord daemon 會說「token 未就緒」，
//          那句話跟「還沒安裝」長得一模一樣。
//          ⇒ 把名字變成**資料**：一處設定、所有人讀它。
//
// 為什麼不用 EditorPrefs（跟 DataRoot 那套不同）：
//   `DataRoot` 是 **per-machine**（每個人 clone 的位置不同）⇒ 住 PlayerPrefs ＋ `.local` pointer 合理。
//   而「secrets 資料夾叫什麼」是**專案佈局事實，每台機器都一樣** ⇒ 必須是**共用設定、入版控**，
//   否則 A 機器叫 Secret、B 機器叫 _secrets，而兩邊各自都能正常跑 —— 直到有人交換檔案。
//
// 數值影響：
//   · 設定檔不存在 ⇒ 用預設 `Secret`（新專案的正確值）。
//     ⚠ **既有專案要顯式寫檔**：搬家前實際資料夾還叫 `_secrets`，靠預設值會當場全斷。
//     所以本次落地時會寫一份內容為 `_secrets` 的設定檔，等資料夾真的搬完再改成 `Secret`。
//     刻意**不做「找不到 Secret 就自動退回 _secrets」的 fallback** —— 自排 fallback 是
//     「跑起來了但用的是另一個宇宙的檔」那族的入口，而它不會叫。
//   · 快取一次，`ResetCache()` 供設定變更後失效。
// @doc-sync: Assets/Plugins/UCL_Core/Docs~/zh-Hant/Workflows/Secret_Manager_Workflow.md（資料夾位置設定）
// ⚠ **本檔住 `UCL_Core_Scripts`（assembly `UCL_Core`）不是 `Editor/`**：
//   消費端橫跨兩個組件 —— scanner／SecretManager 頁在 `UCL_CoreEditor`，
//   而 Discord daemon 與 ChatTavern 後台在 `UCL_Core`。引用是單向的
//   （`UCL_CoreEditor → UCL_Core`）⇒ 解析器只能放在**被引用的那一側**，
//   否則 UCL_Core 那兩個消費端會 CS0246。
#if UNITY_EDITOR
using System;
using System.IO;
using UCL.Core.JsonLib;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>設定檔本體：只有一個欄位 —— secrets 資料夾相對 DataRoot 的名字。</summary>
    public class UCL_SecretsPathConfig : UnityJsonSerializable
    {
        /// <summary>相對 DataRoot 的資料夾名（可含子路徑，一律用正斜線）。</summary>
        public string m_SecretsDir = UCL_SecretsPath.DefaultDirName;
    }

    public static class UCL_SecretsPath
    {
        /// <summary>設定檔名（放 DataRoot 底下）。</summary>
        public const string ConfigFileName = "secrets_config.json";
        /// <summary>設定檔缺席時的預設值（新專案的正確值；既有專案請顯式寫檔）。</summary>
        public const string DefaultDirName = "Secret";
        /// <summary>`ResolveData` 吃的前綴（既有 code 的慣例：`AgentCommands/<sub>`）。</summary>
        public const string ResolvePrefix = "AgentCommands";

        static string s_CachedDirName;

        public static string ConfigPath
            => Path.Combine(UCL_AgentCommandsPath.DataRoot, ConfigFileName).Replace('\\', '/');

        /// <summary>資料夾名（讀設定檔；缺席用預設）。</summary>
        public static string DirName
        {
            get
            {
                if (!string.IsNullOrEmpty(s_CachedDirName)) return s_CachedDirName;
                s_CachedDirName = LoadDirName();
                return s_CachedDirName;
            }
        }

        /// <summary>`AgentCommands/<dir>` —— 丟給 `UCL_AgentCommandsPath.ResolveData` 用。</summary>
        public static string AgentCommandsRelative => ResolvePrefix + "/" + DirName;

        /// <summary>絕對路徑（已套 DataRoot override）。</summary>
        public static string AbsoluteDir
            => UCL_AgentCommandsPath.ResolveData(AgentCommandsRelative).Replace('\\', '/');

        public static void ResetCache() => s_CachedDirName = null;

        /// <summary>讀設定。壞檔**印 warning 並用預設**（不 throw）——
        /// 這條路徑會被 daemon 與多個頁面在畫面上呼叫，讓它丟例外等於把整頁弄壞；
        /// 但**壞掉必須看得見**，所以印 warning 而不是安靜吞掉。</summary>
        static string LoadDirName()
        {
            try
            {
                string aPath = ConfigPath;
                if (!File.Exists(aPath)) return DefaultDirName;
                string aText = File.ReadAllText(aPath);
                if (string.IsNullOrWhiteSpace(aText)) return DefaultDirName;
                var aJson = JsonData.ParseJson(aText);
                if (aJson == null)
                {
                    Debug.LogWarning($"[UCL_SecretsPath] 設定檔解析失敗，改用預設 '{DefaultDirName}'：{aPath}");
                    return DefaultDirName;
                }
                var aConfig = new UCL_SecretsPathConfig();
                aConfig.DeserializeFromJson(aJson);
                string aDir = (aConfig.m_SecretsDir ?? "").Trim().Replace('\\', '/').Trim('/');
                if (string.IsNullOrEmpty(aDir))
                {
                    Debug.LogWarning($"[UCL_SecretsPath] 設定檔的 SecretsDir 是空的，改用預設 '{DefaultDirName}'");
                    return DefaultDirName;
                }
                return aDir;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[UCL_SecretsPath] 讀設定失敗，改用預設 '{DefaultDirName}'：{e.Message}");
                return DefaultDirName;
            }
        }

        /// <summary>寫設定（UTF-8 無 BOM —— python 端也讀這個檔）。寫完清快取。</summary>
        public static void Save(string iDirName)
        {
            string aDir = (iDirName ?? "").Trim().Replace('\\', '/').Trim('/');
            if (string.IsNullOrEmpty(aDir)) throw new Exception("[UCL_SecretsPath] 資料夾名不可空白");
            if (Path.IsPathRooted(aDir))
                throw new Exception("[UCL_SecretsPath] 只能填**相對 DataRoot** 的名字，不是絕對路徑");

            var aConfig = new UCL_SecretsPathConfig { m_SecretsDir = aDir };
            string aPath = ConfigPath;
            string aParent = Path.GetDirectoryName(aPath);
            if (!string.IsNullOrEmpty(aParent) && !Directory.Exists(aParent)) Directory.CreateDirectory(aParent);
            File.WriteAllText(aPath, aConfig.SerializeToJson().ToJsonBeautify(),
                new System.Text.UTF8Encoding(false));
            ResetCache();
        }
    }
}
#endif
