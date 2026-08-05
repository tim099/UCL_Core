// 區塊職責：C# 端「用系統檔案管理器開啟路徑」的唯一實作點
// 物理意義：Windows 對檔案走 explorer.exe /select（開父夾並選中該檔），對資料夾走 shell execute
//          （直接進入該夾）；非 Windows 一律 shell execute 交給 OS 決定。
// 數值影響：純 spawn 外部 process，不讀寫專案任何檔案。
//
// 為什麼要有這一支（2026-08-05 summit）：
//   本 core 內原本有三份各自為政的實作，機制全都不一樣 ——
//     · UCL_AgentCommandRunner.Menu_OpenQueueFolder   → Process.Start + RevealInFinder fallback
//     · UCL_PersonaInspectorPage.OpenInExplorer       → explorer.exe /select（檔）／Process.Start（夾）
//     · UCL_LibraryManagePage.OpenInExplorer          → Application.OpenURL("file://…")
//   三份行為不同、log tag 不同、對「路徑不存在」的處理也不同。要接第四個呼叫端時，
//   再抄一份就是造第四套 —— 所以先把它收攏在這裡，新呼叫端一律走本支。
//   ⚠ 上面三份**尚未**遷移過來（那是獨立的清理，不混在功能單裡）。本支目前只有新呼叫端在用。
//
// 已知坑（三份複本各自踩過，收攏在這裡一次寫清）：
//   · EditorUtility.RevealInFinder(dir) 在 Windows 是「開父夾並選取該夾」，不是「進入該夾」——
//     想進入資料夾必須走 shell execute 開資料夾本身。
//   · 路徑不存在時**不可以靜默 return**：使用者按了按鈕什麼都沒發生，看起來跟 UI 壞掉一樣，
//     而那是最難查的一種壞法。一律留 warning，並且把實際路徑印出來。
using System;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace UCL.Core.EditorLib
{
    /// <summary>
    /// 用作業系統的檔案管理器開啟檔案或資料夾。所有「開啟資料夾」按鈕都應透過本類，
    /// 避免各頁各寫一份、行為互不相同。
    /// </summary>
    public static class UCL_ExplorerUtil
    {
        /// <summary>
        /// 開啟指定路徑：檔案 → 開父夾並選中該檔；資料夾 → 進入該夾。
        /// 路徑不存在或開啟失敗都會留 log（不靜默失敗）。
        /// </summary>
        /// <param name="iPath">絕對路徑。檔案或資料夾皆可。</param>
        /// <param name="iLogTag">log 前綴，填呼叫端名稱（例：LoginStatus）方便回溯是哪個按鈕。</param>
        /// <returns>true = 已成功交給 OS 開啟。false = 路徑不存在或 spawn 失敗（已留 log）。</returns>
        public static bool Open(string iPath, string iLogTag)
        {
            string tag = string.IsNullOrEmpty(iLogTag) ? "UCL_ExplorerUtil" : iLogTag;
            if (string.IsNullOrWhiteSpace(iPath))
            {
                Debug.LogWarning($"[{tag}] 開啟資料夾失敗：路徑為空。");
                return false;
            }

            string path = Path.GetFullPath(iPath);
            bool isDir = Directory.Exists(path);
            bool isFile = !isDir && File.Exists(path);
            if (!isDir && !isFile)
            {
                // 這裡刻意印絕對路徑：相對路徑的 warning 在「路徑解析錯了」跟「東西真的不存在」
                // 之間分不出來，而那兩種的修法完全不同。
                Debug.LogWarning($"[{tag}] 開啟資料夾失敗：路徑不存在 {path}");
                return false;
            }

            try
            {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
                if (isFile)
                {
                    // /select 需要 Windows 慣用的反斜線；正斜線會讓 explorer 忽略選取只開父夾。
                    Process.Start("explorer.exe", $"/select,\"{path.Replace('/', '\\')}\"");
                    return true;
                }
#endif
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true,
                });
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[{tag}] 開啟資料夾失敗 ({path}): {e.Message}");
                return false;
            }
        }
    }
}
