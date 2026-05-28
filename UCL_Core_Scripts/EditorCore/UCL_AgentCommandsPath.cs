// 區塊職責：AgentCommands「資料根 (DataRoot)」的可配置解析點 — 控制台路徑設定的核心。
// 物理意義：
//   AgentCommands/ 底下同時住「持久狀態資料」(ChatTavern / Treasury / Bartender / _session /
//   AwakenInit / Lessons / baton / Rules / AutoMessage) 跟「腳本 / RPC 管線」(Tools/*.py /
//   PromptQueue/*.py / queue.json)。本類只負責**資料**那一半的根 (DataRoot) — 讓 Tim 能把
//   資料搬到專案外 / 別處。腳本 + RPC queue 仍錨在 repo 的 canonical AgentCommands (走
//   UCL_RepoPath.AgentCommandsDir),因為那是 code,不該跟著資料搬。
// 設計取捨 (Tim 2026-05-28 拍板 + apex-one Round 2 review):
//   - 三模式 enum:RepoRootDefault(預設,= 現行行為) / GlobalAbsolute / ProjectRelative
//   - 設定存 PlayerPrefs (per-machine,不同機器各自設定)
//   - C#↔Python 同步:Apply 時把解析出的絕對 DataRoot 寫進 git-root 的 gitignored pointer 檔
//     (.agentcommands_root.local),Python 端讀同一檔 → 雙語單一真相源
//   - 首要目標 (Tim):預設模式 DataRoot 與改動前**逐字相同**,不破壞現有運作流程
// 2026-05-28:不包 #if UNITY_EDITOR — 對齊 UCL_RepoPath / UCL_TreasuryPaths (純 path helper,
//   只用 PlayerPrefs / System.IO,runtime 可用;Treasury runtime 端要引用它)。
using System;
using System.IO;
using UnityEngine;

namespace UCL.Core.EditorLib
{
    /// <summary>AgentCommands 資料根的解析模式 (控制台可切換)。</summary>
    public enum AgentCommandsPathMode
    {
        /// <summary>預設:RepoRoot/AgentCommands (現行 git-walk 行為,跨 layout 安全)。</summary>
        RepoRootDefault = 0,
        /// <summary>全域:使用者填的絕對路徑 (e.g. D:\Unity\EmblemOfValor\AgentCommands)。</summary>
        GlobalAbsolute = 1,
        /// <summary>專案相對:Application.dataPath + 相對路徑 (用 ../ 往上層)。</summary>
        ProjectRelative = 2,
    }

    /// <summary>
    /// AgentCommands「資料根」解析 — 控制台路徑設定的單一解析點。
    /// 持久狀態資料的目錄都該透過 <see cref="DataRoot"/> / <see cref="ResolveData"/> 取得;
    /// 腳本 / RPC queue 仍走 <see cref="UCL_RepoPath.AgentCommandsDir"/> (canonical, 不搬)。
    /// </summary>
    public static class UCL_AgentCommandsPath
    {
        /// <summary>PlayerPrefs key — 解析模式 (int = AgentCommandsPathMode)。預設 0 = RepoRootDefault。</summary>
        public const string PrefKeyMode = "UCL.AgentCommands.PathMode";
        /// <summary>PlayerPrefs key — GlobalAbsolute 模式的絕對路徑。</summary>
        public const string PrefKeyAbsolute = "UCL.AgentCommands.AbsolutePath";
        /// <summary>PlayerPrefs key — ProjectRelative 模式的相對路徑 (相對 Application.dataPath)。</summary>
        public const string PrefKeyRelative = "UCL.AgentCommands.RelativePath";

        /// <summary>git-root 下的 gitignored pointer 檔名 — C#/Python 共讀的資料根真相源。</summary>
        public const string PointerFileName = ".agentcommands_root.local";

        static string s_CachedDataRoot;

        /// <summary>
        /// AgentCommands 資料根的絕對路徑 (cache)。預設模式 = RepoRoot/AgentCommands (與現行逐字相同)。
        /// 控制台 Apply 後須呼叫 <see cref="ResetCache"/> 才會重算。
        /// </summary>
        public static string DataRoot
        {
            get
            {
                if (string.IsNullOrEmpty(s_CachedDataRoot)) s_CachedDataRoot = Resolve();
                return s_CachedDataRoot;
            }
        }

        /// <summary>清快取 — 控制台 Apply 改設定後呼叫,下次取 DataRoot 會重算。</summary>
        public static void ResetCache() => s_CachedDataRoot = null;

        /// <summary>預設資料根 = RepoRoot/AgentCommands (現行行為)。</summary>
        public static string DefaultDataRoot
            => Path.Combine(UCL_RepoPath.RepoRoot, "AgentCommands").Replace('\\', '/');

        /// <summary>git-root 下 pointer 檔的絕對路徑。</summary>
        public static string PointerFilePath
            => Path.Combine(UCL_RepoPath.RepoRoot, PointerFileName).Replace('\\', '/');

        // ===========================================================
        // 區塊職責：把舊的「AgentCommands/<sub>」相對路徑映射到 (可 override 的) 資料根
        // 物理意義：既有子系統的 *DirRelative const 形如 "AgentCommands/ChatTavern";本 helper 把
        //          前綴 "AgentCommands" 換成 DataRoot,保留 const 不動 → migration 機械化 + 低風險。
        // 數值影響：預設模式下 = Path.Combine(RepoRoot/AgentCommands, sub) = 改前逐字相同。
        // ===========================================================
        public static string ResolveData(string agentCommandsRelative)
        {
            const string prefix = "AgentCommands";
            string rel = (agentCommandsRelative ?? "").Replace('\\', '/');
            if (rel == prefix) return DataRoot;
            if (rel.StartsWith(prefix + "/")) rel = rel.Substring(prefix.Length + 1);
            return Path.Combine(DataRoot, rel).Replace('\\', '/');
        }

        // ===========================================================
        // 區塊職責：依 PlayerPrefs 解析資料根
        // 物理意義：mode 決定來源;任一解析失敗 / 值空 → fallback DefaultDataRoot (保證不破壞)。
        // 數值影響：GlobalAbsolute 用絕對路徑;ProjectRelative 用 dataPath + rel (GetFullPath 處理 ..)。
        // ===========================================================
        static string Resolve()
        {
            try
            {
                var mode = (AgentCommandsPathMode)PlayerPrefs.GetInt(PrefKeyMode, 0);
                switch (mode)
                {
                    case AgentCommandsPathMode.GlobalAbsolute:
                    {
                        string abs = (PlayerPrefs.GetString(PrefKeyAbsolute, "") ?? "").Trim();
                        if (!string.IsNullOrEmpty(abs))
                            return Path.GetFullPath(abs).Replace('\\', '/');
                        break;  // 空 → fallback default
                    }
                    case AgentCommandsPathMode.ProjectRelative:
                    {
                        string rel = (PlayerPrefs.GetString(PrefKeyRelative, "") ?? "").Trim();
                        if (!string.IsNullOrEmpty(rel))
                            return Path.GetFullPath(Path.Combine(Application.dataPath, rel)).Replace('\\', '/');
                        break;  // 空 → fallback default
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[UCL_AgentCommandsPath] resolve 失敗,fallback default: {e.Message}");
            }
            return DefaultDataRoot;
        }

        // ===========================================================
        // 區塊職責：pointer 檔讀 / 寫 / 清 — C#/Python 共讀的資料根真相源
        // 物理意義：控制台 Apply 時,override 模式寫絕對路徑進 pointer 檔;預設模式刪 pointer 檔
        //          (= 無 override)。Python 端讀同一檔得資料根,兩語言同步。
        // 數值影響：pointer 檔 gitignored,per-machine。
        // ===========================================================
        public static string ReadPointerFile()
        {
            try
            {
                string p = PointerFilePath;
                if (!File.Exists(p)) return null;
                string content = File.ReadAllText(p).Trim();
                return string.IsNullOrEmpty(content) ? null : content;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[UCL_AgentCommandsPath] pointer 讀取失敗: {e.Message}");
                return null;
            }
        }

        public static void WritePointerFile(string absoluteDataRoot)
        {
            try { File.WriteAllText(PointerFilePath, absoluteDataRoot.Replace('\\', '/') + "\n"); }
            catch (Exception e) { Debug.LogWarning($"[UCL_AgentCommandsPath] pointer 寫入失敗: {e.Message}"); }
        }

        public static void ClearPointerFile()
        {
            try { if (File.Exists(PointerFilePath)) File.Delete(PointerFilePath); }
            catch (Exception e) { Debug.LogWarning($"[UCL_AgentCommandsPath] pointer 刪除失敗: {e.Message}"); }
        }

        // ===========================================================
        // 區塊職責：套用設定 (控制台「套用」按鈕呼叫) — 寫 PlayerPrefs + 同步 pointer 檔 + 清快取
        // 物理意義：override 模式 → 寫 pointer 檔讓 Python 同步;預設模式 → 刪 pointer 檔 (回 canonical)。
        // 數值影響：呼叫後 DataRoot 立即反映新設定 (但常駐 daemon 需重啟 Editor 才乾淨重讀)。
        // ===========================================================
        public static void ApplySettings(AgentCommandsPathMode mode, string absolutePath, string relativePath)
        {
            PlayerPrefs.SetInt(PrefKeyMode, (int)mode);
            if (absolutePath != null) PlayerPrefs.SetString(PrefKeyAbsolute, absolutePath.Trim());
            if (relativePath != null) PlayerPrefs.SetString(PrefKeyRelative, relativePath.Trim());
            PlayerPrefs.Save();

            ResetCache();

            if (mode == AgentCommandsPathMode.RepoRootDefault)
                ClearPointerFile();                 // 回預設 = 無 override → 移除 pointer 檔
            else
                WritePointerFile(DataRoot);          // override → 寫解析後絕對路徑供 Python 讀
        }
    }
}
