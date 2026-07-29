// 區塊職責：控制台 (Control Panel) IMGUI 頁面 — 集中控制專案內各項重要設定。
// 物理意義：給人類開發者一個總控台統一開關各子系統；目前第一塊功能是「聊天酒館系統」總開關。
//          設計成可擴充 — 之後新增其他設定 (e.g. Discord / 排程 / 渲染) 各自再加一個 section method。
// 設計取捨 (Tim 2026-05-28 拍板)：
//   - 仿 UCL_ChatTavernPage 提升為 EditorMenu 外部主要按鈕 (ShowInPageMenu => false)
//   - 酒館系統開關預設關閉，存 PlayerPrefs (走 UCL_ChatTavernSystemControl 單一真相源)
//   - 關閉 → 停止酒館各自動廣播 + 背景程序 (Bartender daemon)
//   - Discord 雙向同步 (outbound mirror / inbound relay) 是獨立開關, 不受酒館系統總開關影響
//     (Tim 2026-07-28 拍板: 各自獨立關注點)
//   - 打開 → SetEnabled 內由 OFF→ON 自動 fire 重啟，讓 daemon 重新初始化
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

        protected override void ContentOnGUI()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label("<b>控制台 — 專案重要設定</b>", UCL_GUIStyle.LabelStyle);
            }
            GUILayout.Space(8);

            DrawChatTavernSystemSection();
            GUILayout.Space(8);
            DrawTavernAdminSection();
            GUILayout.Space(8);
            DrawBankAdminSection();
            GUILayout.Space(8);
            DrawKnowledgeBaseAdminSection();
            GUILayout.Space(8);
            DrawAgentCommandsPathSection();
        }

        // ===========================================================
        // 區塊：知識庫後台管理入口（Tim 2026-07-23 拍板）
        // 物理意義：push UCL_KnowledgeBaseAdminPage — Agent 長期記憶 / 文檔語意向量檢索的
        //          依賴安裝、bge-m3 權重預熱、索引重建、檢索測試管理頁。
        // ===========================================================
        void DrawKnowledgeBaseAdminSection()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool aShow;
                // header：折疊鈕 + 標題 + **開啟管理頁（關鍵操作）提到折疊外層**
                using (new GUILayout.HorizontalScope())
                {
                    aShow = UCL_GUILayout.Toggle(m_FoldDic, "KnowledgeBaseFold", 21, iDefaultValue: false);
                    GUILayout.Label("<b>🧠 知識庫後台</b>", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    if (GUILayout.Button("開啟知識庫後台管理頁", UCL_GUIStyle.GetButtonStyle(new Color(0.6f, 0.8f, 1f)), GUILayout.ExpandWidth(false)))
                    {
                        UCL_KnowledgeBaseAdminPage.Create();
                    }
                    GUILayout.FlexibleSpace();
                }
                if (!aShow) return;
                GUILayout.Label("Agent 長期記憶 / 文檔語意向量檢索：依賴安裝、bge-m3 權重預熱、Docs / Lessons 索引重建、檢索測試。", UCL_GUIStyle.LabelStyle);
            }
        }

        // ===========================================================
        // 區塊：銀行後台管理入口（Tim 2026-07-21 拍板）
        // 物理意義：push UCL_BankAdminPage — Treasury token 帳戶 / 繪圖券 / 酒館券 的查詢與管理
        //          （開戶 / 打款 / 轉帳 / 券發放）。
        // ===========================================================
        void DrawBankAdminSection()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool aShow;
                using (new GUILayout.HorizontalScope())
                {
                    aShow = UCL_GUILayout.Toggle(m_FoldDic, "BankAdminFold", 21, iDefaultValue: false);
                    GUILayout.Label("<b>🏦 銀行後台</b>", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    if (GUILayout.Button("開啟銀行後台管理頁", UCL_GUIStyle.GetButtonStyle(new Color(1f, 0.85f, 0.3f)), GUILayout.ExpandWidth(false)))
                    {
                        UCL_BankAdminPage.Create();
                    }
                    GUILayout.FlexibleSpace();
                }
                if (!aShow) return;
                GUILayout.Label("Treasury token 帳戶餘額 / 開戶 / 打款（薪酬入戶）/ 跨 bank 轉帳；繪圖券 & 酒館券 查詢與發放。", UCL_GUIStyle.LabelStyle);
            }
        }

        // ===========================================================
        // 區塊：酒館後台管理入口（Tim 2026-07-15 拍板）
        // 物理意義：push UCL_ChatTavernAdminPage — Discord mirror 同步狀態 / persona 頭像 override /
        //          底層 config・state 檔案的管理頁。
        // ===========================================================
        void DrawTavernAdminSection()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool aShow;
                // header：折疊鈕 + 標題 + **開啟管理頁（關鍵操作）提到折疊外層**
                using (new GUILayout.HorizontalScope())
                {
                    aShow = UCL_GUILayout.Toggle(m_FoldDic, "TavernAdminFold", 21, iDefaultValue: true);
                    GUILayout.Label("<b>🍺 酒館後台</b>", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    if (GUILayout.Button("開啟酒館後台管理頁", UCL_GUIStyle.GetButtonStyle(Color.cyan), GUILayout.ExpandWidth(false)))
                    {
                        UCL_ChatTavernAdminPage.Create();
                    }
                    GUILayout.FlexibleSpace();
                }

                if (!aShow) return;

                // Discord 兩顆開關折疊 因為超過一行!!
                DrawDiscordSyncToggles();
                GUILayout.Label("Discord mirror 同步進度（per-room 已同步 seq）、persona 頭像 override URL、底層 config / state 檔案管理。", UCL_GUIStyle.LabelStyle);

                // 區塊職責：Discord 雙向同步的兩顆 daemon 開關直接搬到控制台（Tim 2026-07-28 要求）
                // 物理意義：outbound = 酒館訊息 → Discord（UCL_DiscordMirrorDaemon）；
                //          inbound = Discord → 酒館（UCL_DiscordInboundDaemon）。兩者皆 EditorPrefs
                //          per-machine 持久化、預設 OFF，**與「聊天酒館系統」總開關無關**（獨立關注點）。
                // 數值影響：inbound 還要 config 的 tavern_inbound.enabled 為 true 才真的運作 →
                //          兩道閘門任一未開就標示原因，不讓人以為開了就會動（禁靜默失敗）。
                GUILayout.Label("↑ 兩顆 Discord 開關已提到本區塊 header 下方（折疊時仍可操作）。", UCL_GUIStyle.LabelStyle);
            }
        }

        // ===========================================================
        // 區塊：Discord 雙向同步的兩顆 daemon 開關（Tim 2026-07-28 要求搬進控制台）
        // 物理意義：outbound = 酒館訊息 → Discord（UCL_DiscordMirrorDaemon）；
        //          inbound = Discord → 酒館（UCL_DiscordInboundDaemon）。兩者皆 EditorPrefs
        //          per-machine 持久化、預設 OFF，**與「聊天酒館系統」總開關無關**（獨立關注點）。
        // 數值影響：inbound 還要 config 的 tavern_inbound.enabled 為 true 才真的運作 →
        //          兩道閘門任一未開就標示原因，不讓人以為開了就會動（禁靜默失敗）。
        // 設計取捨：抽成獨立方法並畫在折疊 **外層** —— 這是本頁最高頻操作，收合狀態下也要一鍵可切。
        // ===========================================================
        void DrawDiscordSyncToggles()
        {
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label("  <b>Discord 雙向同步</b>", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                GUILayout.Label("（獨立開關，與酒館系統總開關無關）", UCL_GUIStyle.LabelStyle);
                GUILayout.FlexibleSpace();
            }
            {
                using (new GUILayout.HorizontalScope())
                {
                    bool outOn = UCL.Core.EditorLib.AgentCommands.ChatTavern.UCL_DiscordMirrorDaemon.Enabled;
                    GUILayout.Label("  📤 Outbound（酒館→Discord）", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(230)));
                    if (GUILayout.Button(outOn ? "● 啟用中（按一下關閉）" : "○ 已關閉（按一下啟用）",
                            UCL_GUIStyle.GetButtonStyle(outOn ? new Color(0.4f, 0.85f, 0.5f) : new Color(0.85f, 0.5f, 0.4f)),
                            GUILayout.ExpandWidth(false)))
                    {
                        UCL.Core.EditorLib.AgentCommands.ChatTavern.UCL_DiscordMirrorDaemon.Enabled = !outOn;
                    }
                    GUILayout.FlexibleSpace();
                }
                using (new GUILayout.HorizontalScope())
                {
                    bool inOn = UCL.Core.EditorLib.AgentCommands.ChatTavern.UCL_DiscordInboundDaemon.Enabled;
                    bool inCfg = UCL.Core.EditorLib.AgentCommands.ChatTavern.UCL_DiscordInboundDaemon.ConfigEnabled;
                    GUILayout.Label("  📥 Inbound（Discord→酒館）", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(230)));
                    if (GUILayout.Button(inOn ? "● 啟用中（按一下關閉）" : "○ 已關閉（按一下啟用）",
                            UCL_GUIStyle.GetButtonStyle(inOn ? new Color(0.4f, 0.85f, 0.5f) : new Color(0.85f, 0.5f, 0.4f)),
                            GUILayout.ExpandWidth(false)))
                    {
                        UCL.Core.EditorLib.AgentCommands.ChatTavern.UCL_DiscordInboundDaemon.Enabled = !inOn;
                    }
                    if (inOn && !inCfg)
                        GUILayout.Label("⚠ config tavern_inbound.enabled=false → 仍不會運作", UCL_GUIStyle.LabelStyle);
                    else if (inOn)
                        GUILayout.Label($"路由 {UCL.Core.EditorLib.AgentCommands.ChatTavern.UCL_DiscordInboundDaemon.ActiveRouteCount} 頻道 · "
                                        + $"本 session 中繼 {UCL.Core.EditorLib.AgentCommands.ChatTavern.UCL_DiscordInboundDaemon.RelayedThisSession} 筆",
                                        UCL_GUIStyle.LabelStyle);
                    GUILayout.FlexibleSpace();
                }
            }
        }

        // ===========================================================
        // 區塊：聊天酒館系統總開關
        // 物理意義：讀 UCL_ChatTavernSystemControl.IsEnabled 顯示當前狀態；toggle 變動 → SetEnabled。
        //          OFF→ON 由 SetEnabled 內部自動重啟 daemon；另提供「重啟系統」按鈕手動重觸發。
        // 數值影響：寫 PlayerPrefs "UCL.ChatTavern.System.Enabled"；fire OnSystemRestart 通知 daemon。
        // ===========================================================
        void DrawChatTavernSystemSection()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool enabled = UCL_ChatTavernSystemControl.IsEnabled;
                bool aShow;
                // ---- header：折疊鈕 + 標題 + 狀態燈 + **關鍵操作（開關 / 重啟）提到折疊外層** ----
                // 物理意義：收合後仍要能一鍵開關與重啟 —— 常用操作不該被折疊藏起來（AdminPage 同款模式）。
                using (new GUILayout.HorizontalScope())
                {
                    aShow = UCL_GUILayout.Toggle(m_FoldDic, "TavernSystemFold", 21, iDefaultValue: false);
                    GUILayout.Label("<b>聊天酒館系統</b>", UCL_GUIStyle.LabelStyle, GUILayout.Width(140));
                    var stateStyle = new GUIStyle(UCL_GUIStyle.LabelStyle);
                    stateStyle.normal.textColor = enabled ? new Color(0.4f, 1f, 0.4f) : new Color(1f, 0.5f, 0.4f);
                    GUILayout.Label(enabled ? "● 運行中" : "○ 已停止", stateStyle, GUILayout.ExpandWidth(false));
                    

                    // 開關 toggle — 變動才寫，避免每幀 PlayerPrefs IO
                    bool newEnabled = GUILayout.Toggle(
                        enabled,
                        enabled ? " 系統啟用中（按一下關閉）" : " 系統已關閉（按一下啟用）",
                        UCL_GUIStyle.ButtonStyle,
                        GUILayout.ExpandWidth(false));
                    if (newEnabled != enabled)
                    {
                        UCL_ChatTavernSystemControl.SetEnabled(newEnabled);
                        Debug.Log($"[ControlPanel] 聊天酒館系統 → {(newEnabled ? "啟用 (自動重啟)" : "關閉")}");
                    }
                    // 手動重啟 — 只有系統啟用時才有意義 (停止狀態重啟無作用)
                    // 採 GUI.enabled 手動 save/restore (對齊 UCL_EditorMenuPage)，避免依賴 EditorGUI.DisabledScope
                    bool oldGUIEnabled = GUI.enabled;
                    GUI.enabled = enabled;
                    if (GUILayout.Button("重啟系統", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        UCL_ChatTavernSystemControl.Restart();
                        Debug.Log("[ControlPanel] 手動重啟聊天酒館系統");
                    }
                    GUI.enabled = oldGUIEnabled;
                    GUILayout.FlexibleSpace();
                }
                if (!aShow) return;

                GUILayout.Space(2);
                GUILayout.Label(
                    "控制酒保自動廣播（關鍵字觸發 / 時間規則 / 跨日保管費）與酒館背景子程序。\n" +
                    "關閉後酒館停止一切自動廣播與背景程序；打開時自動重啟系統。\n" +
                    "註：Discord 雙向同步是獨立開關（見「🍺 酒館後台」區塊），不受本開關影響。",
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
                    "設定走 PlayerPrefs (per-machine),Apply 時把絕對路徑寫到 git-root 的 .agentcommands_root.local 讓 Python 端同步。",
                    UCL_GUIStyle.LabelStyle);
                GUILayout.Space(4);

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
                bool tavernOn = UCL_ChatTavernSystemControl.IsEnabled;
                bool validInput = m_PathDraftMode switch
                {
                    AgentCommandsPathMode.GlobalAbsolute => !string.IsNullOrEmpty((m_PathDraftAbsolute ?? "").Trim()) && Path.IsPathRooted(m_PathDraftAbsolute.Trim()),
                    AgentCommandsPathMode.ProjectRelative => !string.IsNullOrEmpty((m_PathDraftRelative ?? "").Trim()),
                    _ => true,
                };
                string blockReason = null;
                if (!validInput) blockReason = m_PathDraftMode == AgentCommandsPathMode.GlobalAbsolute
                    ? "請填入有效的絕對路徑 (rooted)" : "請填入相對路徑";
                else if (tavernOn) blockReason = "聊天酒館系統目前是啟用中 — 請先到上面把系統關閉再改路徑 (避免 daemon 寫到舊路徑半途)";

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
                            "⚠ 建議重啟 Editor 讓所有常駐 daemon (Bartender / Discord inbound) 乾淨重讀新路徑。" +
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
