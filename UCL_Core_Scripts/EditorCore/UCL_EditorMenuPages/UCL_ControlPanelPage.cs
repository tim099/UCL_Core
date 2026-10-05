// 區塊職責：控制台 (Control Panel) IMGUI 頁面 — 集中控制專案內各項重要設定。
// 物理意義：給人類開發者一個總控台統一開關各子系統。
//          設計成可擴充 — 之後新增其他設定 (e.g. Discord / 排程 / 渲染) 各自再加一個 section method。
// 設計取捨 (Tim 2026-05-28 拍板)：
//   - 仿 UCL_ChatTavernPage 提升為 EditorMenu 外部主要按鈕 (ShowInPageMenu => false)
//   - 2026-10-05（TASK-0365）：Unity 端酒保整個廢棄（搬到 Senate 酒館 Server、後台在 Senate「酒保」頁），
//     「聊天酒館系統」總開關（它只管酒保 daemon）與「酒保後台」入口一起拿掉。
//   - 各 section 可折疊 (Tim 2026-07-29 要求, 比照 UCL_ChatTavernAdminPage)：**關鍵操作
//     (開關 / 重啟 / 開啟管理頁 / Discord 兩顆同步開關) 一律畫在折疊外層 header**，
//     收合後仍可一鍵操作；折疊內只放說明文字與低頻設定。折疊狀態走專用 m_FoldDic
//     (不與 PopupSearchCache 共用 — 見該欄位註解的血證)。
#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UCL.Core.EditorLib;
using UCL.Core.EditorLib.AgentCommands.ChatTavern;
using UCL.Core.JsonLib;
using UCL.Core.Page;
using UCL.Core.UI;
using UnityEngine;

namespace UCL.Core.EditorLib.Page
{
    /// <summary>
    /// 控制台頁面 — 專案重要設定的總控制面板。第一塊：聊天酒館系統總開關。
    /// </summary>
    public class UCL_ControlPanelPage : UCL_CommonEditorPage
    {
        public override string WindowName => "控制台";

        // 已提升為 EditorMenu 外部主要按鈕 (見 UCL_EditorMenuPage)，關閉下拉避免重複出現
        public override bool ShowInPageMenu => false;

        public static UCL_ControlPanelPage Create() => UCL_EditorPage.Create<UCL_ControlPanelPage>();

        // ===== AgentCommands 路徑 section 的 draft 狀態 =====
        // 物理意義：UI 上編輯的值 (尚未 Apply)。Apply 按下才寫 PlayerPrefs + pointer 檔。
        // 數值影響：m_PathDraftLoaded 控制只 lazy-load 一次,避免每幀重讀 PlayerPrefs 覆蓋使用者編輯
        bool m_PathDraftLoaded = false;
        AgentCommandsPathMode m_PathDraftMode = AgentCommandsPathMode.RepoRootDefault;
        string m_PathDraftAbsolute = "";
        string m_PathDraftRelative = "../../AgentCommands";
        // Mode dropdown 選項 (順序對齊 enum 0/1/2) — List<string> 對齊 UCL_GUILayout.PopupSearchCache 用法
        static readonly List<string> s_PathModeLabels = new List<string>
        {
            "預設 (RepoRoot/AgentCommands)",
            "全域絕對路徑 (Global)",
            "專案相對 (ProjectRelative)",
        };
        // PopupSearchCache 內部狀態容器 — 對齊 UCL_EditorMenuPage 的 m_PagePickerDic 模式
        readonly UCL_ObjectDictionary m_PickerDic = new UCL_ObjectDictionary();
        // 區塊職責：各 section 折疊狀態 — **刻意跟 m_PickerDic 分開**
        // 物理意義：折疊是使用者 UI 偏好（該長存）；PopupSearchCache 是衍生資料（選項變了該失效）。
        // 血證（2026-07-29 Tim QA, UCL_ChatTavernAdminPage）：兩者共用一個 dictionary 時，
        //          資料重載路徑上的 dic.Clear() 會把折疊值一併清掉 → 下一幀退回 iDefaultValue，
        //          症狀是「按某個開關就自動展開、而且收不起來」，看起來像 key 撞名實際是共用快取被清。
        //          本頁目前沒有 Clear 路徑，但先分開，免得日後有人加 Clear 又踩一次。
        readonly UCL_ObjectDictionary m_FoldDic = new UCL_ObjectDictionary();
        // Apply 後的回饋訊息 (取代 EditorUtility.DisplayDialog,持久顯示直到下次 Apply)
        string m_LastApplyMessage = "";
        protected override void TopBarButtons()
        {
            base.TopBarButtons();
            GUILayout.Label("<b>控制台 — 專案重要設定</b>", UCL_GUIStyle.LabelStyle);
        }
        protected override void ContentOnGUI()
        {
            GUILayout.Space(8);

            DrawPersonaAgentAdminSection();
            GUILayout.Space(8);

            DrawLibraryManageSection();
            GUILayout.Space(8);
            DrawAgentCmdAdminSection();
            GUILayout.Space(8);
            DrawAgentSkillManagerSection();
            GUILayout.Space(8);
            DrawAgentCommandsPathSection();
        }

        // 區塊職責：既有圖書館管理頁入口。
        // 物理意義：Control Panel 提供日常管理入口；閱讀心得的新／舊資料定位則交由
        //          UCL_ReadingNotesManagePage，避免此處把兩種資料模型混成一個操作面。
        // 數值影響：只 push page，不讀寫 BookNotes 或 Books。
        void DrawLibraryManageSection()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool aShow;
                using (new GUILayout.HorizontalScope())
                {
                    aShow = UCL_GUILayout.Toggle(m_FoldDic, "LibraryManageFold", 21, iDefaultValue: false);
                    GUILayout.Label("<b>📚 圖書館管理</b>", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    if (GUILayout.Button("開啟圖書館管理頁", UCL_GUIStyle.GetButtonStyle(new Color(0.7f, 0.85f, 1f)), GUILayout.ExpandWidth(false)))
                        UCL_LibraryManagePage.Create();
                    if (GUILayout.Button("閱讀心得入口", UCL_GUIStyle.GetButtonStyle(new Color(0.75f, 0.95f, 0.75f)), GUILayout.ExpandWidth(false)))
                        UCL_ReadingNotesManagePage.Create();
                    GUILayout.FlexibleSpace();
                }
                if (!aShow) return;
                GUILayout.Label("圖書館管理頁負責既有 Books / Library 操作；閱讀心得入口可依作品名稱列出 Archive 與新 Library 的手動開啟路徑，不會讓新流程讀取 Archive。", UCL_GUIStyle.LabelStyle);
            }
        }

        // ===========================================================
        // 區塊職責：提供 Agent Skill Manager 的控制台入口。
        // 物理意義：skills 與 agent entry documents 的同步屬於日常專案設定，從控制台可直接到管理頁。
        // 數值影響：按鈕只 push Editor page；不會自行執行安裝器、覆寫入口檔或寫入設定。
        // ===========================================================
        void DrawAgentSkillManagerSection()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool aShow;
                using (new GUILayout.HorizontalScope())
                {
                    aShow = UCL_GUILayout.Toggle(m_FoldDic, "AgentSkillManagerFold", 21, iDefaultValue: false);
                    GUILayout.Label("<b>🧩 Agent Skills</b>", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    if (GUILayout.Button("開啟 Agent Skill Manager", UCL_GUIStyle.GetButtonStyle(new Color(0.65f, 0.85f, 1f)), GUILayout.ExpandWidth(false)))
                    {
                        UCL_AgentSkillManagerPage.Create();
                    }
                    GUILayout.FlexibleSpace();
                }
                if (!aShow) return;
                GUILayout.Label("管理 Claude Code、Codex、Antigravity 的 Skills 與 Agent Entry Documents 同步狀態。"
                    + "入口檔不同內容預設保留，只有在管理頁明確按 Force Sync 才覆寫。", UCL_GUIStyle.LabelStyle);
            }
        }

        // ===========================================================
        // 區塊：Cmd 後台管理入口（Tim 2026-07-29 拍板）
        // 物理意義：push UCL_AgentCmdAdminPage — 已註冊 Cmd 清單 + **schema 同步**（手動刷新按鈕）。
        //          Python client 端的參數預檢讀 C# 反射生成的 commands_schema.json；
        //          以前 Python 那張表是手抄的，抄漏就會「C# 有實作但 client 擋死」
        //          （血證 2026-07-29：create_trpg_room）。本入口讓同步這件事有個看得到的地方按。
        // ===========================================================
        void DrawAgentCmdAdminSection()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool aShow;
                using (new GUILayout.HorizontalScope())
                {
                    aShow = UCL_GUILayout.Toggle(m_FoldDic, "AgentCmdAdminFold", 21, iDefaultValue: false);
                    GUILayout.Label("<b>🧾 Cmd 後台</b>", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    if (GUILayout.Button("開啟 Cmd 後台管理頁", UCL_GUIStyle.GetButtonStyle(new Color(0.7f, 0.95f, 0.8f)), GUILayout.ExpandWidth(false)))
                    {
                        UCL_AgentCmdAdminPage.Create();
                    }
                    GUILayout.FlexibleSpace();
                }
                if (!aShow) return;
                GUILayout.Label("已註冊 Agent Command 清單，以及 Python client 端預檢用的 commands_schema.json 同步狀態與手動刷新。"
                    + "新增／修改 Cmd 後請按同步（或跑 `senate ucmd run ExportCmdSchema`，兩者等價）。",
                    UCL_GUIStyle.LabelStyle);
            }
        }

        // ===========================================================
        // 區塊：Persona & Agent 管理入口（Tim 2026-07-29 拍板）
        // 物理意義：push UCL_PersonaAgentAdminPage — 建 agent（含對應 bank）/ 建 persona（可選 fork 來源）/
        //          persona 換綁 agent。以前這些只能手改 AwakenInit 下的 json 或走 awakening.py CLI。
        // ===========================================================
        void DrawPersonaAgentAdminSection()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool aShow;
                using (new GUILayout.HorizontalScope())
                {
                    aShow = UCL_GUILayout.Toggle(m_FoldDic, "PersonaAgentAdminFold", 21, iDefaultValue: false);
                    GUILayout.Label("<b>🧬 Persona & Agent</b>", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    if (GUILayout.Button("開啟 Persona & Agent 管理頁", UCL_GUIStyle.GetButtonStyle(new Color(0.7f, 0.9f, 1f)), GUILayout.ExpandWidth(false)))
                    {
                        UCL_PersonaAgentAdminPage.Create();
                    }
                    GUILayout.FlexibleSpace();
                }
                if (!aShow) return;
                GUILayout.Label("身分兩層管理：建立 agent（帳號層，同時登記對應 bank／可帶種子額度）、建立 persona（人格層，"
                    + "可選 fork 來源複製 identity_vector 與血統）、persona 換綁 agent（只改歸屬，vector／wake_count 保留）。",
                    UCL_GUIStyle.LabelStyle);
            }
        }

        // ===========================================================
        // 區塊：AgentCommands 資料路徑配置 (T-PATH-01 Phase 2)
        // 物理意義：Enum 三模式 + 路徑輸入 + 即時預覽 + 套用按鈕。
        //          套用 → UCL_AgentCommandsPath.ApplySettings (寫 PlayerPrefs + pointer 檔 + ResetCache)。
        // 安全護欄 (basecamp 2026-05-28 拍板, Tim 給的自由意志決策):
        //   - 聊天酒館系統 ON → 擋下 Apply + 提示先關 (避免 daemon 寫到舊路徑半途)
        //   - active work-session 存在 → 擋下 Apply + 提示先結束 (執行狀態會撕裂)
        //   採 block + 提示而非 auto-toggle — 顯式優於隱式,不靜默 mutate 使用者開關狀態。
        // 數值影響：PlayerPrefs 寫入後快取 reset;daemon 需重啟 Editor 才乾淨重讀 (UI 明示)。
        // ===========================================================
        void DrawAgentCommandsPathSection()
        {
            EnsurePathDraftLoaded();

            using (new GUILayout.VerticalScope("box"))
            {
                // ---- header：折疊鈕 + 標題 + 當前已套用模式提示（狀態留在外層，收合也看得到）----
                bool aShow;
                using (new GUILayout.HorizontalScope())
                {
                    aShow = UCL_GUILayout.Toggle(m_FoldDic, "AgentCmdPathFold", 21, iDefaultValue: false);
                    GUILayout.Label("<b>AgentCommands 資料路徑</b>", UCL_GUIStyle.LabelStyle, GUILayout.Width(200));
                    var committedMode = (AgentCommandsPathMode)PlayerPrefs.GetInt(UCL_AgentCommandsPath.PrefKeyMode, 0);
                    GUILayout.Label($"已套用: {s_PathModeLabels[(int)committedMode]}", UCL_GUIStyle.LabelStyle);
                    GUILayout.FlexibleSpace();
                }
                if (!aShow) return;
                GUILayout.Space(2);
                GUILayout.Label(
                    "持久狀態資料 (酒館 / 銀行 / persona / 書籍 / Lessons / baton / Rules) 的存放根目錄。\n" +
                    "RPC queue 與腳本 (Tools / PromptQueue) 永遠錨在專案的 RepoRoot/AgentCommands,不受此設定影響。\n" +
                    "⚠ 2026-08-17：資料根 override（全域絕對 / 專案相對）暫時停用，只走預設。\n" +
                    "  理由：兩個 override 模式至今一次都沒被實跑過，卻讓「資料根在哪」變成三分支、\n" +
                    "  且 C#/Python 兩端各要對一次。未驗證的彈性不是彈性，是三倍的待驗表面積。\n" +
                    "  規格想清楚（含 pointer 檔語意）再補回來。",
                    UCL_GUIStyle.LabelStyle);
                GUILayout.Space(4);

                // ---- 模式（停用中：只走預設）----
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("模式", UCL_GUIStyle.LabelStyle, GUILayout.Width(60));
                    GUILayout.Label("預設 (RepoRoot/AgentCommands) —— override 模式停用中", UCL_GUIStyle.LabelStyle);
                    GUILayout.FlexibleSpace();
                }

                /* --- 停用中：模式切換與兩種 override 的輸入欄（想好規格再補） ---
                // ---- 模式 dropdown ----
                // 採 UCL_GUILayout.PopupSearchCache (runtime-safe, 自帶搜尋 + per-popup 快取), 對齊 UCL_EditorMenuPage 的 page picker 用法
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("模式", UCL_GUIStyle.LabelStyle, GUILayout.Width(60));
                    int newIdx = UCL_GUILayout.PopupSearchCache(
                        (int)m_PathDraftMode, s_PathModeLabels, m_PickerDic, "PathModePicker",
                        GUILayout.Width(360));
                    if (newIdx != (int)m_PathDraftMode && newIdx >= 0 && newIdx < s_PathModeLabels.Count)
                        m_PathDraftMode = (AgentCommandsPathMode)newIdx;
                    GUILayout.FlexibleSpace();
                }

                // ---- 模式專屬輸入 ----
                if (m_PathDraftMode == AgentCommandsPathMode.GlobalAbsolute)
                {
                    // 絕對路徑用手動輸入 — 不用 EditorUtility.OpenFolderPanel (Editor-only API);
                    // 即時預覽會驗證 rooted 性,使用者可從檔案總管複製貼上路徑
                    using (new GUILayout.HorizontalScope())
                    {
                        GUILayout.Label("絕對路徑", UCL_GUIStyle.LabelStyle, GUILayout.Width(80));
                        m_PathDraftAbsolute = GUILayout.TextField(m_PathDraftAbsolute ?? "", GUILayout.MinWidth(380));
                    }
                    GUILayout.Label("  範例: D:/Unity/EmblemOfValor/AgentCommands (可放專案外,從檔案總管複製貼上)", UCL_GUIStyle.LabelStyle);
                }
                else if (m_PathDraftMode == AgentCommandsPathMode.ProjectRelative)
                {
                    using (new GUILayout.HorizontalScope())
                    {
                        GUILayout.Label("相對 dataPath", UCL_GUIStyle.LabelStyle, GUILayout.Width(110));
                        m_PathDraftRelative = GUILayout.TextField(m_PathDraftRelative ?? "", GUILayout.MinWidth(380));
                    }
                    GUILayout.Label("  Application.dataPath = .../<UnityProject>/Assets — 用 ../ 往上層", UCL_GUIStyle.LabelStyle);
                    GUILayout.Label("  範例: ../AgentCommands (CardGame/AgentCommands) / ../../AgentCommands (EmblemOfValor/AgentCommands, = 預設位置)", UCL_GUIStyle.LabelStyle);
                }
                else
                {
                    GUILayout.Label("  預設模式:走 RepoRoot/AgentCommands (現行行為,跨 layout 安全,無 override)", UCL_GUIStyle.LabelStyle);
                }

                --- 停用中結束 --- */

                GUILayout.Space(4);

                // ---- 即時預覽 ----
                string previewPath = ComputeDraftPreview();
                bool exists = !string.IsNullOrEmpty(previewPath) && Directory.Exists(previewPath);
                bool hasData = exists && DirHasContent(previewPath);
                using (new GUILayout.VerticalScope("box"))
                {
                    GUILayout.Label($"<b>解析後絕對路徑</b>: {(string.IsNullOrEmpty(previewPath) ? "(待填入)" : previewPath)}", UCL_GUIStyle.LabelStyle);
                    string statusIcon = !exists ? "⚠ 不存在 (Apply 後會自動建立)"
                                        : !hasData ? "📂 存在但空目錄 (新位置 — 若有舊資料請手動搬移)"
                                                   : "✅ 存在且已有資料";
                    GUILayout.Label(statusIcon, UCL_GUIStyle.LabelStyle);
                }

                GUILayout.Space(4);

                // ---- 安全護欄檢查 ----
                bool validInput = m_PathDraftMode switch
                {
                    AgentCommandsPathMode.GlobalAbsolute => !string.IsNullOrEmpty((m_PathDraftAbsolute ?? "").Trim()) && Path.IsPathRooted(m_PathDraftAbsolute.Trim()),
                    AgentCommandsPathMode.ProjectRelative => !string.IsNullOrEmpty((m_PathDraftRelative ?? "").Trim()),
                    _ => true,
                };
                string blockReason = null;
                if (!validInput) blockReason = m_PathDraftMode == AgentCommandsPathMode.GlobalAbsolute
                    ? "請填入有效的絕對路徑 (rooted)" : "請填入相對路徑";

                // ---- 套用按鈕 + 重新載入 ----
                // 用 GUI.enabled 手動 save/restore 取代 UnityEditor.EditorGUI.DisabledScope (對齊 UCL_EditorMenuPage)
                using (new GUILayout.HorizontalScope())
                {
                    bool oldEnabled = GUI.enabled;
                    GUI.enabled = blockReason == null;
                    if (GUILayout.Button("套用設定", UCL_GUIStyle.GetButtonStyle(new Color(0.5f, 1f, 0.5f)), GUILayout.ExpandWidth(false)))
                    {
                        UCL_AgentCommandsPath.ApplySettings(m_PathDraftMode, m_PathDraftAbsolute, m_PathDraftRelative);
                        Debug.Log($"[ControlPanel] AgentCommands 路徑已套用 → {UCL_AgentCommandsPath.DataRoot}");
                        // 取代 EditorUtility.DisplayDialog (Editor-only): 把回饋持久顯示在頁面下方
                        bool overridden = UCL_AgentCommandsPath.DataRoot != UCL_AgentCommandsPath.DefaultDataRoot;
                        m_LastApplyMessage =
                            $"✅ 已套用 — 新資料根: {UCL_AgentCommandsPath.DataRoot}\n" +
                            "PlayerPrefs + pointer 檔 (.agentcommands_root.local) 已同步。\n" +
                            "⚠ 建議重啟 Editor 讓常駐的 Editor 元件（心跳等）乾淨重讀新路徑。" +
                            (overridden ? "\n📂 舊路徑既有資料不會自動搬移,如需保留請手動複製 (Migrate 工具列在後續 Phase)。" : "");
                    }
                    GUI.enabled = oldEnabled;

                    if (GUILayout.Button("從 PlayerPrefs 重新載入", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        m_PathDraftLoaded = false;
                        EnsurePathDraftLoaded();
                    }
                    GUILayout.FlexibleSpace();
                }

                if (blockReason != null)
                {
                    var warnStyle = new GUIStyle(UCL_GUIStyle.LabelStyle);
                    warnStyle.normal.textColor = new Color(1f, 0.6f, 0.3f);
                    GUILayout.Label($"⚠ {blockReason}", warnStyle);
                }

                // Apply 後的回饋訊息 — 持久顯示直到下次 Apply (取代 EditorUtility.DisplayDialog)
                if (!string.IsNullOrEmpty(m_LastApplyMessage))
                {
                    GUILayout.Space(4);
                    using (new GUILayout.VerticalScope("box"))
                    {
                        GUILayout.Label(m_LastApplyMessage, UCL_GUIStyle.LabelStyle);
                    }
                }
            }
        }

        // 從 PlayerPrefs 載 draft (lazy, 只第一次 / 顯式重載)
        void EnsurePathDraftLoaded()
        {
            if (m_PathDraftLoaded) return;
            m_PathDraftMode = (AgentCommandsPathMode)PlayerPrefs.GetInt(UCL_AgentCommandsPath.PrefKeyMode, 0);
            m_PathDraftAbsolute = PlayerPrefs.GetString(UCL_AgentCommandsPath.PrefKeyAbsolute, "");
            string rel = PlayerPrefs.GetString(UCL_AgentCommandsPath.PrefKeyRelative, "");
            if (!string.IsNullOrEmpty(rel)) m_PathDraftRelative = rel;
            m_PathDraftLoaded = true;
        }

        // 即時預覽 draft 解析後的絕對路徑 (不寫 PlayerPrefs)
        string ComputeDraftPreview()
        {
            try
            {
                switch (m_PathDraftMode)
                {
                    case AgentCommandsPathMode.GlobalAbsolute:
                    {
                        string abs = (m_PathDraftAbsolute ?? "").Trim();
                        if (string.IsNullOrEmpty(abs) || !Path.IsPathRooted(abs)) return "";
                        return Path.GetFullPath(abs).Replace('\\', '/');
                    }
                    case AgentCommandsPathMode.ProjectRelative:
                    {
                        string rel = (m_PathDraftRelative ?? "").Trim();
                        if (string.IsNullOrEmpty(rel)) return "";
                        return Path.GetFullPath(Path.Combine(Application.dataPath, rel)).Replace('\\', '/');
                    }
                    default:
                        return UCL_AgentCommandsPath.DefaultDataRoot;
                }
            }
            catch { return ""; }
        }

        // 目錄是否有內容 (用來判斷「空目錄 vs 已有資料」)
        static bool DirHasContent(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return false;
                foreach (var _ in Directory.EnumerateFileSystemEntries(dir)) return true;
                return false;
            }
            catch { return false; }
        }

    }
}
#endif
