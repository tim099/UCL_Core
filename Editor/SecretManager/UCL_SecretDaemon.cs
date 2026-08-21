// 區塊職責：通用 secret 安裝引導 helper — consumer 自家 daemon tick 內呼叫，偵測 need-install 自動彈窗
// 物理意義：取代「每種 secret 各刻一個 daemon hook」。掃 _secrets/*.enc，對 (.enc 有, 明文缺) 的
//          呼叫 UCL_SecretInstallWindow.MaybeAutoPopup。
// 數值影響：純讀掃描 + 條件彈窗。刻意「不」InitializeOnLoad 自動跑 — 避免跟 consumer 既有
//          daemon (e.g. EOV RCG_DiscordInboundDaemon) 雙重彈窗。consumer 主動 call 才動。

#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UCL.Core.EditorLib.SecretManager
{
    /// <summary>
    /// Secret 安裝引導 helper。opt-in 設計：consumer 從自家 tick 呼叫 <see cref="ScanAndMaybePopup"/>，
    /// 本類不自動 InitializeOnLoad，避免重複彈窗。
    /// </summary>
    public static class UCL_SecretDaemon
    {
        // 區塊職責：節流 — 避免每幀掃描 (subprocess 成本)
        static double s_LastScanTime = 0;
        const double SCAN_INTERVAL_SEC = 5.0;

        /// <summary>
        /// 掃 rootDir 下 need-install 的 secret，逐個 MaybeAutoPopup。
        /// 回傳本次實際彈出的數量。內建 5s 節流 (force=true 可略過)。
        /// </summary>
        // ⚠ 預設值改 `null`（`DefaultSecretsDir` 2026-08-21 起是 property，不能當預設參數值）——
        //   `null` ⇒ 交給 Scan 讀當下設定，行為與原本的「預設目錄」一致。
        public static int ScanAndMaybePopup(string rootDirRelative = null,
                                            bool force = false)
        {
            double now = EditorApplication.timeSinceStartup;
            if (!force && (now - s_LastScanTime) < SCAN_INTERVAL_SEC) return 0;
            s_LastScanTime = now;

            int popped = 0;
            foreach (var info in UCL_SecretScanner.Scan(rootDirRelative))
            {
                if (!string.IsNullOrEmpty(info.Error)) continue;
                if (info.PlainExists) continue;   // 已安裝, 不彈
                var entry = new UCL_SecretEntry
                {
                    PlainPath = info.PlainPath,
                    EncPath = info.EncPath,
                    Label = string.IsNullOrEmpty(info.Label) ? System.IO.Path.GetFileName(info.EncPath) : info.Label,
                };
                if (UCL_SecretInstallWindow.MaybeAutoPopup(entry)) popped++;
            }
            return popped;
        }

        /// <summary>列出 need-install 的 secret (不彈窗) — 給 UI / 摘要用。</summary>
        public static List<UCL_SecretInfo> ListNeedInstall(string rootDirRelative = null)
        {
            var need = new List<UCL_SecretInfo>();
            foreach (var info in UCL_SecretScanner.Scan(rootDirRelative))
            {
                if (string.IsNullOrEmpty(info.Error) && !info.PlainExists) need.Add(info);
            }
            return need;
        }
    }
}
#endif
