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

        // ⚠ 2026-08-17：pointer 檔從 repo root **搬到 &lt;UCL_Core&gt;/**（Tim 拍板）。
        //   放 repo root 時，**要讀到它必須先知道 repo root** ⇒ 它只同步得了
        //   「data_root ≠ repo_root/AgentCommands」的情形，碰不到「兩端 repo_root 推導不一致」那格 ——
        //   而後者才是會咬人的（C# 與 Python 的 tier 順序至今不同）。
        //   UCL_Core 兩端都能在不知道 repo root 的情況下定位（C# 從 Application.dataPath 搜資料夾名、
        //   Python 從 __file__ 往上找目錄名）⇒ 放這裡才真的同步得到 repo_root。
        /// <summary>&lt;UCL_Core&gt;/ 下路徑快照檔的絕對路徑。</summary>
        public static string PointerFilePath
            => Path.Combine(UCL_RepoPath.UCLCoreDir, PointerFileName).Replace('\\', '/');

        // ===========================================================
        // 區塊職責：把本次 domain reload 解析出的路徑寫成快照，供 Python 端讀。
        // 物理意義：**C# 只寫不讀** —— 每次 reload 重算後覆寫，所以「寫錯會被固化」不存在
        //          （下一次 recompile 就被正確值蓋掉）。Python 只讀不寫 + 過期自癒刪檔。
        // 數值影響：內容與現有檔相同就**不寫** —— 避免每次 recompile 都動 mtime，
        //          讓「這個檔變了」保持是一個有意義的訊號。
        // 失敗處置：解析不到路徑（UCL_RepoPath 會 throw）→ 不寫任何東西，印一行 warning。
        //          刻意不寫「猜的值」：一個看起來正常的錯快照，比沒有快照糟。
        // ===========================================================
        public static void WritePathSnapshot()
        {
            try
            {
                string aRepo = UCL_RepoPath.RepoRoot;
                string aData = DataRoot;
                string aContent =
                    "schema=2\n" +
                    $"repo_root={aRepo}\n" +
                    $"data_root={aData}\n" +
                    $"written_at={DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}\n" +
                    "written_by=csharp-editor\n";
                string aPath = PointerFilePath;
                if (File.Exists(aPath))
                {
                    // 只比對語意欄位（written_at 每次都不同，拿它比會永遠判定「有變」）
                    string aOld = File.ReadAllText(aPath);
                    if (aOld.Contains($"repo_root={aRepo}") && aOld.Contains($"data_root={aData}"))
                        return;
                }
                File.WriteAllText(aPath, aContent);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[UCL_AgentCommandsPath] 路徑快照寫入略過（解析失敗）: {e.Message}");
            }
        }

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
        // ⚠ 2026-08-17（Tim 拍板）：**暫時只開放預設模式**，GlobalAbsolute / ProjectRelative 停用。
        //   原因：兩個 override 模式**至今一次都沒有被實跑過**（所有專案都用預設），
        //         而它們卻讓「資料根到底在哪」變成三分支 —— 每一條都要在 C# 與 Python 兩端各對一次。
        //         未驗證的彈性不是彈性，是三倍的待驗表面積。
        //   ⇒ 先收成單一行為，等規格想清楚（含 pointer 檔語意）再補回來。
        //   停用方式刻意是「保留 enum 與 PlayerPrefs key、只讓解析忽略它們」而不是刪型別：
        //     刪型別會讓既有呼叫端編譯錯，而那些呼叫端不是這次要動的東西。
        //   ⚠ 已設過 override 的機器：本次改動後**會安靜回到預設路徑** —— 這是行為變更。
        //     現況本專案無 pointer 檔、無 override，故影響為零；別的專案要先確認。
        static string Resolve()
        {
            return DefaultDataRoot;

            /* --- 停用中：override 模式（想好規格再補） ---
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
                        break;
                    }
                    case AgentCommandsPathMode.ProjectRelative:
                    {
                        string rel = (PlayerPrefs.GetString(PrefKeyRelative, "") ?? "").Trim();
                        if (!string.IsNullOrEmpty(rel))
                            return Path.GetFullPath(Path.Combine(Application.dataPath, rel)).Replace('\\', '/');
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[UCL_AgentCommandsPath] resolve 失敗,fallback default: {e.Message}");
            }
            return DefaultDataRoot;
            --- 停用中結束 --- */
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
        // ⚠ 2026-08-17：override 模式停用中 ⇒ 本方法只剩「回預設」一條路。
        //   參數保留給既有呼叫端（UCL_ControlPanelPage），但 absolutePath / relativePath 不再生效。
        //   pointer 檔在停用期間一律清掉 —— 留著一個沒有 UI 能改、也不會被 Resolve 讀的檔，
        //   是「名字比事實大」的東西：它看起來仍在生效，實際上不影響任何解析。
        public static void ApplySettings(AgentCommandsPathMode mode, string absolutePath, string relativePath)
        {
            PlayerPrefs.SetInt(PrefKeyMode, (int)AgentCommandsPathMode.RepoRootDefault);
            PlayerPrefs.Save();

            ResetCache();
            ClearPointerFile();
        }
    }

#if UNITY_EDITOR
    // 區塊職責：每次 domain reload（開 Editor / 重編譯 / 進出 Play）後重寫路徑快照。
    // 物理意義：InitializeOnLoad 的靜態建構子在**每次 domain reload** 都會跑 ——
    //          所以「重新編譯」就足以刷新快照，不需要重開 Editor。
    // 數值影響：一次字串比對 + 至多一次小檔寫入；值沒變就不寫。
    [UnityEditor.InitializeOnLoad]
    static class UCL_AgentCommandsPathSnapshot
    {
        static UCL_AgentCommandsPathSnapshot()
        {
            UCL_AgentCommandsPath.ResetCache();     // reload 後重算，不沿用上一輪的 static 快取
            UCL_AgentCommandsPath.WritePathSnapshot();
        }
    }
#endif
}
