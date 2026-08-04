// 區塊職責：wait / 酒保插話的可調參數 —— 單一擁有者，後台可改、落檔持久化。
// 物理意義：這些數字原本寫死在 C# 常數裡（TriggerSeconds=450 / CooldownSeconds=90 / …）。
//          python 版本來還有 UCL_BARTENDER_TRIGGER_SEC 環境變數可調，固化上來時被我寫死了 ——
//          那是可測性倒退：沒有旋鈕就代表每驗一次酒保插話要枯等 7.5 分鐘，
//          而「不可測的行為」跟「沒有那個行為」在實務上差不多。
//          2026-08-04 實測時把觸發秒數調成 5 秒，才在 40 秒內跑完一輪並挖出
//          「op=wait 從來沒真的等過」那隻躺了很久的 bug。**可測性不是奢侈品。**
// 數值影響：落檔 <bartender-dir>/tavern_wait_config.json；缺檔／壞檔一律回預設值，不靜默歸零。
// 設計取捨：
//   - 參數集中在這裡而不是散在 NPC / Service / Cmd_Tavern 三處：同一個旋鈕散成三份必漂。
//   - **讀不到設定回預設而非 0**：回 0 會讓「酒保每秒插話」「wait 立刻逾時」這種
//     災難性行為看起來像是設定生效了。壞掉要往安全的方向壞。
// @doc-sync: Assets/Plugins/UCL_Core/Docs~/zh-Hant/API/UCL_AgentCommand/Cmd_Tavern.md
// @doc-sync: Assets/Plugins/UCL_Core/Docs~/zh-Hant/Workflows/ChatTavern_Workflow.md
// @doc-sync: Assets/Plugins/UCL_Core/Skills~/ucl-chat-tavern/reference/re-entry.md
#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using UCL.Core.JsonLib;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.ChatTavern
{
    /// <summary>wait 與酒保插話的可調參數（後台 UCL_BartenderAdminPage 內編輯）。</summary>
    public static class UCL_TavernWaitSettings
    {
        public const int DefaultNpcTriggerSeconds = 450;   // ≈7.5 分，慢速模式 wait=480s 內不被打斷
        public const int DefaultNpcCooldownSeconds = 90;
        public const int DefaultNpcRestHintDrinks = 3;
        public const double DefaultTickIntervalSeconds = 1.0;
        public const int DefaultWaitTimeoutSeconds = 300;

        public static int NpcTriggerSeconds = DefaultNpcTriggerSeconds;
        public static int NpcCooldownSeconds = DefaultNpcCooldownSeconds;
        public static int NpcRestHintDrinks = DefaultNpcRestHintDrinks;
        public static double TickIntervalSeconds = DefaultTickIntervalSeconds;
        public static int DefaultWaitTimeout = DefaultWaitTimeoutSeconds;

        const string ConfigFileName = "tavern_wait_config.json";
        static bool s_Loaded;

        public static string ConfigPath =>
            Path.Combine(Bartender.UCL_BartenderIO.GetBartenderDir(), ConfigFileName).Replace('\\', '/');

        /// <summary>第一次被讀到時自動載入 —— 呼叫端不必記得先 LoadConfig。</summary>
        public static void EnsureLoaded()
        {
            if (s_Loaded) return;
            s_Loaded = true;
            LoadConfig();
        }

        public static void LoadConfig()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return;
                var d = JsonData.ParseJson(File.ReadAllText(ConfigPath, Encoding.UTF8));
                if (d == null) return;
                NpcTriggerSeconds = Mathf.Clamp(d.GetInt("npc_trigger_sec", DefaultNpcTriggerSeconds), 1, 86400);
                NpcCooldownSeconds = Mathf.Clamp(d.GetInt("npc_cooldown_sec", DefaultNpcCooldownSeconds), 0, 86400);
                NpcRestHintDrinks = Mathf.Clamp(d.GetInt("npc_rest_hint_drinks", DefaultNpcRestHintDrinks), 1, 100);
                TickIntervalSeconds = Mathf.Clamp((float)d.GetDouble("tick_interval_sec", DefaultTickIntervalSeconds), 0.1f, 60f);
                DefaultWaitTimeout = Mathf.Clamp(d.GetInt("default_wait_timeout_sec", DefaultWaitTimeoutSeconds), 1, 86400);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TavernWaitSettings] 讀設定失敗，沿用預設值: {e.Message}");
            }
        }

        public static bool SaveConfig(out string error)
        {
            error = null;
            try
            {
                Bartender.UCL_BartenderIO.EnsureBartenderDir();
                var d = new JsonData();
                d["npc_trigger_sec"] = new JsonData(NpcTriggerSeconds);
                d["npc_cooldown_sec"] = new JsonData(NpcCooldownSeconds);
                d["npc_rest_hint_drinks"] = new JsonData(NpcRestHintDrinks);
                d["tick_interval_sec"] = new JsonData(TickIntervalSeconds);
                d["default_wait_timeout_sec"] = new JsonData(DefaultWaitTimeout);
                File.WriteAllText(ConfigPath, d.ToJsonBeautify(), new UTF8Encoding(false));
                s_Loaded = true;
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        /// <summary>回復出廠值（只改記憶體，要按保存才落檔 —— 誤按不會直接毀掉設定檔）。</summary>
        public static void ResetToDefaults()
        {
            NpcTriggerSeconds = DefaultNpcTriggerSeconds;
            NpcCooldownSeconds = DefaultNpcCooldownSeconds;
            NpcRestHintDrinks = DefaultNpcRestHintDrinks;
            TickIntervalSeconds = DefaultTickIntervalSeconds;
            DefaultWaitTimeout = DefaultWaitTimeoutSeconds;
        }
    }
}
#endif
