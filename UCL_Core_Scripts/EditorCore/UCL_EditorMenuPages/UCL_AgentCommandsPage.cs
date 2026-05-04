
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 05/04 2026
// Agent Commands Page — 在 Editor 內手動查看 / 觸發 / 新增 agent commands。
// 用 PopupSearchCache 把可用指令收成一個可搜尋的下拉選單；選定後直接顯示 metadata + 新增表單。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using UCL.Core.EditorLib.AgentCommands;
using UCL.Core.LocalizeLib;
using UCL.Core.UI;
using UnityEngine;

namespace UCL.Core.EditorLib.Page
{
    /// <summary>
    /// Agent Commands 頁面 — agent 透過 queue.json 排隊指令、使用者按按鈕觸發。
    ///
    /// 架構：繼承 <see cref="UCL_CommonEditorPage"/>。
    /// 操作放在 TopBarButtons 裡（Refresh / Run / Open Folder）；
    /// ContentOnGUI 列出隊列、可搜尋的 Command 下拉 + 新增表單。
    ///
    /// 文件關聯：對應的多語系說明文件位於 Docs~/{lang}/UCL_EditorPage/UCL_AgentCommandsPage.md
    /// 物理意義：透過 [HelpURL] 將編輯器頁面與本地化文檔綁定，編輯器內的 ? 按鈕會依當前語系跳轉到對應 md。
    /// 數值影響：無執行期影響，僅影響 Inspector / 編輯器內的說明連結指向。
    /// Docs~\en\UCL_EditorPage\UCL_AgentCommandsPage.md
    /// Docs~\ja\UCL_EditorPage\UCL_AgentCommandsPage.md
    /// Docs~\zh-Hans\UCL_EditorPage\UCL_AgentCommandsPage.md
    /// Docs~\zh-Hant\UCL_EditorPage\UCL_AgentCommandsPage.md
    /// </summary>
    [UCL.Core.ATTR.RequiresConstantRepaint]
    [HelpURL("ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_AgentCommandsPage.md")]
    public class UCL_AgentCommandsPage : UCL_CommonEditorPage
    {
        public override string WindowName => "Agent Commands";

        static public UCL_AgentCommandsPage Create()
        {
            return UCL_EditorPage.Create<UCL_AgentCommandsPage>();
        }

        // ==== 新增表單的暫存欄位 ====
        // 區塊職責：保留使用者在表單中尚未送出的編輯狀態
        // 物理意義：m_SelectedCmdIdx 對應 PopupSearchCache 的索引，指向 UCL_AgentCommandRegistry.ListHandlers() 的某一筆
        // 數值影響：Add 按下時依此狀態建立一筆新的 UCL_AgentCommand 並寫回 queue.json
        int m_SelectedCmdIdx = 0;
        UCL_AgentCommandMode m_NewMode = UCL_AgentCommandMode.OneShot;
        string m_NewDescription = "";
        string m_NewArgsRaw = ""; // 形如 key1=value1;key2=value2
        Vector2 m_Scroll = Vector2.zero;

        // PopupSearchCache 需要一個 UCL_ObjectDictionary 作為 cache 容器
        readonly UCL_ObjectDictionary m_Dic = new UCL_ObjectDictionary();

        // ==== 顯示用快取（每幀重讀檔太重，只在按下 Refresh 時更新）====
        UCL_AgentCommandQueueData m_Cached;

        protected override void TopBarButtons()
        {
            base.TopBarButtons();
            if (GUILayout.Button("Refresh", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                m_Cached = UCL_AgentCommandQueue.Load();
            }
            if (GUILayout.Button("Run Pending Commands", UCL_GUIStyle.GetButtonStyle(Color.green), GUILayout.ExpandWidth(false)))
            {
                UCL_AgentCommandRunner.Menu_RunPending();
                DelayedRefresh().Forget();
            }
            if (GUILayout.Button("Open Folder", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                UCL_AgentCommandRunner.Menu_OpenQueueFolder();
            }
        }

        protected override void ContentOnGUI()
        {
            // 載入 / 刷新
            if (m_Cached == null)
            {
                m_Cached = UCL_AgentCommandQueue.Load();
            }

            // ==== queue.json 路徑提示 ====
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label($"Queue: {UCL_AgentCommandQueue.GetQueuePath()}", UCL_GUIStyle.LabelStyle);
            }

            // ==== 統計列 ====
            // 區塊職責：在頂端顯示 queue 內的指令數量分布
            // 物理意義：OneShot 在執行成功後會被 runner 立刻移除，因此這裡的 OneShot 數即為「尚未成功執行的待辦」
            // 數值影響：純顯示，不修改任何狀態
            int total = m_Cached?.Commands?.Count ?? 0;
            int oneshot = 0, repeatable = 0;
            if (m_Cached?.Commands != null)
            {
                foreach (var c in m_Cached.Commands)
                {
                    if (c.Mode == UCL_AgentCommandMode.Repeatable) repeatable++;
                    else oneshot++;
                }
            }
            using (new GUILayout.HorizontalScope("box"))
            {
                GUILayout.Label($"Total: {total} | OneShot: {oneshot} | Repeatable: {repeatable}",
                    UCL_GUIStyle.LabelStyle);
            }

            m_Scroll = GUILayout.BeginScrollView(m_Scroll, GUILayout.ExpandHeight(false));

            // ==== Queue 中的命令清單 ====
            DrawQueueList();

            GUILayout.Space(8);

            // ==== Command 選擇 + 新增表單（整合在一起） ====
            DrawCommandPicker();

            GUILayout.Space(8);

            // ==== 提示 ====
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label("Tips", UCL_GUIStyle.LabelStyle);
                GUILayout.Label("- OneShot：成功執行後會直接從 queue 中移除", UCL_GUIStyle.LabelStyle);
                GUILayout.Label("- Repeatable：每次 Run 都會再執行一次，並把 RunCount +1", UCL_GUIStyle.LabelStyle);
                GUILayout.Label("- Run 之前自動 await UCL_ModuleService.WaitUntilInitialized — 確保模組系統已就緒", UCL_GUIStyle.LabelStyle);
                GUILayout.Label("- 失敗的指令會把錯誤訊息寫進 LastRunError 並留在 queue（不會被移除）", UCL_GUIStyle.LabelStyle);
            }

            GUILayout.EndScrollView();
        }

        // ===========================================================
        // 區塊：Queue 中的命令清單
        // 物理意義：顯示 queue.json 內所有指令當前狀態，並提供 Remove 按鈕
        // 數值影響：Remove 後立刻寫回 queue.json
        // ===========================================================
        void DrawQueueList()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label("Commands", UCL_GUIStyle.LabelStyle);
                if (m_Cached?.Commands == null || m_Cached.Commands.Count == 0)
                {
                    GUILayout.Label("(queue is empty — 從下方挑一個 Command 加入 queue，或直接編輯 queue.json)", UCL_GUIStyle.LabelStyle);
                    return;
                }

                int removeIdx = -1;
                for (int i = 0; i < m_Cached.Commands.Count; i++)
                {
                    var c = m_Cached.Commands[i];
                    Color tagColor = GetStatusColor(c);
                    using (new GUILayout.VerticalScope("box"))
                    {
                        using (new GUILayout.HorizontalScope())
                        {
                            GUILayout.Label($"<color=#{ColorUtility.ToHtmlStringRGB(tagColor)}>●</color> [{StatusText(c)}]",
                                UCL_GUIStyle.LabelStyle, GUILayout.Width(140));
                            GUILayout.Label($"<b>{c.Type ?? "<null>"}</b>",
                                UCL_GUIStyle.LabelStyle, GUILayout.Width(220));
                            GUILayout.Label($"({c.Mode}, run×{c.RunCount})", UCL_GUIStyle.LabelStyle, GUILayout.Width(140));
                            GUILayout.FlexibleSpace();
                            if (GUILayout.Button("Remove", UCL_GUIStyle.GetButtonStyle(Color.red), GUILayout.ExpandWidth(false)))
                            {
                                removeIdx = i;
                            }
                        }
                        GUILayout.Label($"ID: <i>{c.Id ?? ""}</i>", UCL_GUIStyle.LabelStyle);
                        if (!string.IsNullOrEmpty(c.Description))
                        {
                            GUILayout.Label($"  {c.Description}", UCL_GUIStyle.LabelStyle);
                        }
                        if (c.Args != null && c.Args.Count > 0)
                        {
                            GUILayout.Label($"  Args: {string.Join(", ", c.Args.Select(kv => $"{kv.Key}={kv.Value}"))}", UCL_GUIStyle.LabelStyle);
                        }
                        if (!string.IsNullOrEmpty(c.LastRunAt))
                        {
                            Color resultColor = c.LastRunResult == "Success" ? Color.green : Color.red;
                            GUILayout.Label($"  Last: <color=#{ColorUtility.ToHtmlStringRGB(resultColor)}>{c.LastRunResult ?? "?"}</color> @ {c.LastRunAt}",
                                UCL_GUIStyle.LabelStyle);
                            if (!string.IsNullOrEmpty(c.LastRunError))
                            {
                                GUILayout.Label($"  Error: {c.LastRunError}", UCL_GUIStyle.GetLabelStyle(Color.red));
                            }
                        }
                    }
                }
                if (removeIdx >= 0)
                {
                    m_Cached.Commands.RemoveAt(removeIdx);
                    UCL_AgentCommandQueue.Save(m_Cached);
                }
            }
        }

        // ===========================================================
        // 區塊：Command 選擇 + 新增表單（整合）
        // 職責：用 PopupSearchCache 提供可搜尋的下拉選單，選一個 handler 後顯示其 metadata 與新增欄位
        // 物理意義：使用者只看到目前選中的指令，不再看一大串清單；按 Add 就把目前選定的指令加入 queue
        // 數值影響：Add 按下時把一筆新指令寫進 queue.json
        // ===========================================================
        void DrawCommandPicker()
        {
            var handlers = UCL_AgentCommandRegistry.ListHandlers();
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label("Add Command", UCL_GUIStyle.LabelStyle);

                if (handlers.Count == 0)
                {
                    GUILayout.Label("(尚未註冊任何 Handler — 請寫一個 class 繼承 UCL_AgentCommandHandlerBase)", UCL_GUIStyle.LabelStyle);
                    return;
                }

                // 下拉選單顯示用文字（CommandType + 短描述）
                var displayOptions = handlers
                    .Select(h => string.IsNullOrEmpty(h.ShortDescription)
                        ? h.CommandType
                        : $"{h.CommandType} — {h.ShortDescription}")
                    .ToList();

                if (m_SelectedCmdIdx < 0) m_SelectedCmdIdx = 0;
                if (m_SelectedCmdIdx >= handlers.Count) m_SelectedCmdIdx = handlers.Count - 1;

                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("Command:", UCL_GUIStyle.LabelStyle, GUILayout.Width(100));
                    m_SelectedCmdIdx = UCL_GUILayout.PopupSearchCache(
                        m_SelectedCmdIdx, displayOptions, m_Dic, "CmdPicker");
                }

                var selected = handlers[m_SelectedCmdIdx];

                // 顯示選定 handler 的 metadata
                using (new GUILayout.VerticalScope("box"))
                {
                    GUILayout.Label($"<b>{selected.CommandType}</b>", UCL_GUIStyle.LabelStyle);
                    if (!string.IsNullOrEmpty(selected.ShortDescription))
                    {
                        GUILayout.Label($"  {selected.ShortDescription}", UCL_GUIStyle.LabelStyle);
                    }
                    if (!string.IsNullOrEmpty(selected.ArgsSchema))
                    {
                        GUILayout.Label($"  Args Schema: {selected.ArgsSchema}", UCL_GUIStyle.LabelStyle);
                    }
                    if (!string.IsNullOrEmpty(selected.HelpURL))
                    {
                        if (GUILayout.Button("查看說明", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                        {
                            Application.OpenURL(selected.HelpURL);
                        }
                    }
                }

                // Mode / Description / Args 表單欄位
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("Mode:", UCL_GUIStyle.LabelStyle, GUILayout.Width(100));
                    if (GUILayout.Toggle(m_NewMode == UCL_AgentCommandMode.OneShot, "OneShot", UCL_GUIStyle.ButtonStyle))
                        m_NewMode = UCL_AgentCommandMode.OneShot;
                    if (GUILayout.Toggle(m_NewMode == UCL_AgentCommandMode.Repeatable, "Repeatable", UCL_GUIStyle.ButtonStyle))
                        m_NewMode = UCL_AgentCommandMode.Repeatable;
                }

                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("Description:", UCL_GUIStyle.LabelStyle, GUILayout.Width(100));
                    m_NewDescription = GUILayout.TextField(m_NewDescription ?? "", UCL_GUIStyle.LabelStyle);
                }

                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("Args:", UCL_GUIStyle.LabelStyle, GUILayout.Width(100));
                    m_NewArgsRaw = GUILayout.TextField(m_NewArgsRaw ?? "", UCL_GUIStyle.LabelStyle);
                    GUILayout.Label("(format: k=v;k=v)", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                }

                if (GUILayout.Button($"Add '{selected.CommandType}' ({m_NewMode})",
                    UCL_GUIStyle.GetButtonStyle(Color.cyan), GUILayout.ExpandWidth(false)))
                {
                    AddCommand(selected.CommandType, m_NewMode, m_NewDescription, ParseArgsRaw(m_NewArgsRaw));
                    // 清空僅與本次新增有關的欄位（保留 Mode / 選定的 Command 方便連續新增）
                    m_NewDescription = "";
                    m_NewArgsRaw = "";
                }
            }
        }

        // ===========================================================
        // Helpers
        // ===========================================================

        Color GetStatusColor(UCL_AgentCommand c)
        {
            if (c.LastRunResult == "Failed") return Color.red;
            if (c.Mode == UCL_AgentCommandMode.Repeatable) return Color.cyan;
            // OneShot 成功會被立刻移除，所以還在 queue 內的 OneShot 等於 Pending
            return Color.yellow;
        }

        string StatusText(UCL_AgentCommand c)
        {
            if (c.LastRunResult == "Failed") return "Failed";
            if (c.Mode == UCL_AgentCommandMode.Repeatable)
            {
                return c.LastRunResult == "Success" ? "Repeat OK" : "Repeatable";
            }
            // OneShot 在 queue 內就是還沒成功跑過
            return "Pending";
        }

        void AddCommand(string type, UCL_AgentCommandMode mode, string description, Dictionary<string, string> args)
        {
            if (string.IsNullOrEmpty(type))
            {
                Debug.LogWarning("[UCL_AgentCmd UI] Type is empty — abort.");
                return;
            }
            if (m_Cached == null) m_Cached = new UCL_AgentCommandQueueData();
            if (m_Cached.Commands == null) m_Cached.Commands = new List<UCL_AgentCommand>();

            var c = new UCL_AgentCommand
            {
                Id = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{type.ToLower()}",
                Type = type,
                Mode = mode,
                RunCount = 0,
                Args = args ?? new Dictionary<string, string>(),
                CreatedAt = DateTime.UtcNow.ToString("o"),
                Description = string.IsNullOrEmpty(description) ? null : description,
            };
            m_Cached.Commands.Add(c);
            UCL_AgentCommandQueue.Save(m_Cached);
            Debug.Log($"[UCL_AgentCmd UI] Added command: {c.Type} (id={c.Id}, mode={c.Mode})");
        }

        static Dictionary<string, string> ParseArgsRaw(string raw)
        {
            var dict = new Dictionary<string, string>();
            if (string.IsNullOrWhiteSpace(raw)) return dict;
            foreach (var part in raw.Split(';'))
            {
                var kv = part.Split('=');
                if (kv.Length == 2)
                {
                    string k = kv[0].Trim();
                    string v = kv[1].Trim();
                    if (!string.IsNullOrEmpty(k)) dict[k] = v;
                }
            }
            return dict;
        }

        async UniTask DelayedRefresh()
        {
            // runner 是 async，等個 1.5 秒讓它有時間寫回 queue.json
            await UniTask.Delay(TimeSpan.FromSeconds(1.5));
            m_Cached = UCL_AgentCommandQueue.Load();
        }
    }
}
#endif
