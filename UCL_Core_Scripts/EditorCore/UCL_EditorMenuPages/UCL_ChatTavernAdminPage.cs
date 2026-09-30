// 區塊職責：酒館後台管理頁（Tavern Admin）—— 訊息檔名 migration、渲染筆數參數、底層檔案入口。
// 物理意義：⛔ 2026-09-28（TASK-0316）起**不再管 Discord**：Unity 端 Discord In／Outbound 全面退場，
//          Discord 的一切（Bot、webhook、頻道對應、白名單、頭像網址、開關）改在 Senate 後台：
//          `senate ui --page discord-bot`／`discord-webhooks`／`discord-relay`。
//          本頁原本的五個 Discord 面板（鏡像狀態／分類路由／Inbound／頭像 override／Webhook）已移除。
// 數值影響：維護面板 dry-run 唯讀、apply 只改檔名；參數面板寫 PlayerPrefs（UCL_ChatTavernSettings）。
// 設計取捨：UI 字串仿 UCL_ControlPanelPage 慣例用 zh-Hant 硬編（內部管理頁，不走 CodeLocalize）。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UCL.Core.EditorLib.AgentCommands;
using UCL.Core.EditorLib.AgentCommands.ChatTavern;
using UCL.Core.JsonLib;
using UCL.Core.Page;
using UCL.Core.UI;
using UnityEditor;
using UnityEngine;
using UCL_ChatTavernSettings = UCL.Core.EditorLib.AgentCommands.ChatTavern.UCL_ChatTavernSettings;

namespace UCL.Core.EditorLib.Page
{
    /// <summary>
    /// 酒館後台管理頁 —— 訊息檔名 migration／渲染筆數參數／底層檔案。⛔ Discord 設定不在這裡（改在 Senate）。
    /// 入口：控制台 (UCL_ControlPanelPage) 的「🍺 酒館後台管理」按鈕。
    /// </summary>
    [HelpURL("ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_ChatTavernAdminPage.md")]
    public class UCL_ChatTavernAdminPage : UCL_CommonEditorPage
    {
        public override string WindowName => "酒館後台管理";
        public override bool ShowInPageMenu => true;
        public static UCL_ChatTavernAdminPage Create() => UCL_EditorPage.Create<UCL_ChatTavernAdminPage>();

        static string PromptQueueDir => Path.Combine(UCL_AgentCommandsPath.DataRoot, "PromptQueue");
        static string NotifyConfigPath => Path.Combine(PromptQueueDir, "notify_config.json");
        static string TavernStatePath => Path.Combine(PromptQueueDir, "_tavern_state.json");
        static string DrainLogPath => Path.Combine(PromptQueueDir, "_drain.log");

        readonly UCL_ObjectDictionary m_FoldDic = new UCL_ObjectDictionary();  // 折疊狀態

        GUIStyle m_WrapLabelStyle;
        GUIStyle WrapLabelStyle
        {
            get
            {
                if (m_WrapLabelStyle == null)
                    m_WrapLabelStyle = new GUIStyle(UCL_GUIStyle.LabelStyle) { wordWrap = true, richText = true };
                return m_WrapLabelStyle;
            }
        }

        protected override void ContentOnGUI()
        {
            GUILayout.Label("⛔ Discord 設定已移到 Senate 後台（senate ui --page discord-bot／discord-webhooks／discord-relay）—— 本頁不再管 Discord。", WrapLabelStyle);
            GUILayout.Space(8);
            DrawMaintenancePanel();
            GUILayout.Space(8);
            DrawParamSettingsPanel();
            GUILayout.Space(8);
            DrawFilesPanel();
        }

        // ===========================================================
        // 區塊：⚙ 參數設定 — 渲染筆數（Tim 2026-07-31 拍板：把硬編的「串幾筆」搬到後台可調）
        // 區塊職責：UCL_ChatTavernSettings 四個筆數參數的唯一 UI 入口。
        // 物理意義：這四個數字直接決定 agent 讀回 _last_op.md / _last_view.md 時吃掉多少 context —
        //          原本 op=read 預設 100 筆，實測一次早安 catch-up 就是 66k token。
        // 數值影響：draft 只是輸入暫存，按「套用」才寫 PlayerPrefs；寫入前經 Clamp 收進 [1, 500]。
        //          改完即時生效（下一個 Cmd 就吃新值），不需重啟 Editor。
        // ===========================================================
        readonly Dictionary<string, string> m_ParamDraft = new Dictionary<string, string>();  // 參數輸入 draft（key = pref 名）

        // ===========================================================
        // 區塊：🗄 維護（檔名 migration）
        // 區塊職責：把「訊息檔名 → 全域 seq」這件一次性遷移，做成頁面上可手動觸發的入口。
        // 物理意義：判斷與對帳全在 `UCL_ChatTavernMessageFileMigration`（同 namespace 的 C# 實作）——
        //          本區塊只負責畫按鈕、擋前置條件、把報告貼回來，**不含任何改名邏輯**。
        //          那支直接呼叫 `UCL_ChatTavernIO_PerMsgFile.GetOrderedMessageFilePaths()`，
        //          也就是 seq 排序的**同一個函式本人** —— 不是複製一份排序規則。
        // 數值影響：dry-run 完全唯讀。apply 只改**檔名**，不碰任何檔案內容、不動 git。
        // 邊界：rooms 目錄由該 migration 走 `UCL_ChatTavernIO.GetRoomsRoot()` 解析，
        //      不寫死安裝路徑，跨專案可用（見 ucl-core-paths）。
        // ⚠ 執行前必須關閉聊天酒館系統總開關：改名進行中的窗口裡 seq 對應是錯亂的，
        //   bartender 可能把舊訊息當新訊息重跑 inline 指令／酒館 CLI（會真的發文、CLI 會真的動 Editor）。本區塊會擋（見下）。
        // ===========================================================
        string m_MigrateReport = "";
        Vector2 m_MigrateScroll;
        bool m_MigrateRunning;
        string m_MigrateRunningLabel = "";

        void DrawMaintenancePanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool aShow;
                using (new GUILayout.HorizontalScope())
                {
                    aShow = UCL_GUILayout.Toggle(m_FoldDic, "MaintenanceFold", 21);
                    GUILayout.Label("<b>🗄 維護 — 訊息檔名 migration（舊格式 → 全域 seq）</b>", WrapLabelStyle);
                }
                if (!aShow) return;

                GUILayout.Label(
                    "把 <b>HHMMSS_ms_uuid.json</b> 改名為 <b>00000001.json</b>（＝該訊息的全域 seq）。\n"
                    + "改名<b>照現在的排序順序</b>逐一指派，所以排序結果與 seq 對應關係一個都不動 —— "
                    + "改的是「怎麼知道 seq」，不是 seq 本身。\n"
                    + "改完之後 seq 直接寫在檔名上，冷啟動不必再列舉並排序整房才算得出 seq。",
                    WrapLabelStyle);

                bool tavernOn = UCL_ChatTavernSystemControl.IsEnabled;
                if (tavernOn)
                {
                    // 擋而不是只警告 —— 警示可以被忽略，拒絕不能。
                    // 這裡擋的是一個「外觀成功但會讓 bartender 誤發文」的操作。
                    GUILayout.Label(
                        "🚫 <b>聊天酒館系統目前是開啟的 —— 已停用執行鈕。</b>\n"
                        + "　 改名會讓日期目錄 mtime 改變 → 檔案清單快取失效 → daemon 重新列舉，"
                        + "而<b>改名進行中</b>那個窗口的排序是半舊半新的，seq 對應會暫時錯亂：\n"
                        + "　 bartender 可能把<b>舊訊息</b>當新訊息重跑 inline 指令／酒館 CLI（會真的發文、CLI 會真的動 Editor）。\n"
                        + "　 請先到 <b>UCL_ControlPanelPage</b> 關閉酒館系統總開關。",
                        WrapLabelStyle);
                }

                using (new EditorGUI.DisabledScope(m_MigrateRunning))
                {
                    using (new GUILayout.HorizontalScope())
                    {
                        // 試跑永遠可按（唯讀，不受總開關影響）—— 先看清單再決定，跟攤平同步頁同一個慣例。
                        if (GUILayout.Button("試跑（唯讀，只列清單）",
                                UCL_GUIStyle.GetButtonStyle(new Color(0.55f, 0.8f, 1f)),
                                GUILayout.ExpandWidth(false)))
                        {
                            RunMigrate(false);
                        }
                        using (new EditorGUI.DisabledScope(tavernOn))
                        {
                            if (GUILayout.Button("執行 migration（會改檔名）",
                                    UCL_GUIStyle.GetButtonStyle(new Color(1f, 0.5f, 0.3f)),
                                    GUILayout.ExpandWidth(false)))
                            {
                                ConfirmAndMigrate();
                            }
                        }
                    }
                }
                GUILayout.Space(6);
                // TASK-0332：發文計酬補款只剩 Senate 一個入口（它也涵蓋 commit／reading_note 等酒館那一類）。
                GUILayout.Label("<b>發文計酬補款</b>：`senate cmd bank-reconcile`（report 唯讀／apply 要 confirm=1）",
                    WrapLabelStyle);

                GUILayout.Space(6);
                GUILayout.Label("<b>訊息檔清單索引（冷啟動加速）</b>", WrapLabelStyle);
                GUILayout.Label(
                    "migration 之後 <b>seq == 檔名</b>，每個日期目錄裝一段連續 seq —— "
                    + "於是「排序後的完整清單」可由一張<b>每日範圍表</b>算出來，不必列舉。"
                    + "索引一天一行：大小跟<b>天數</b>成正比，不是跟訊息數成正比。"
                    + "它治的是<b>冷啟動</b>（記憶體快取 domain reload 就沒了，而每次編譯都會 reload）。"
                    + "任何一致性檢查不過就退回全量 —— 只會變慢，不會算錯。"
                    + "<b>Editor 只讀索引、不寫</b>（TASK-0335）：它由 Senate Server 在寫完每則訊息後刷新；"
                    + "要重建請跑 <b>senate cmd tavern-index --arg op=rebuild</b>。",
                    WrapLabelStyle);
                using (new EditorGUI.DisabledScope(m_MigrateRunning))
                using (new GUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("驗證索引（逐筆比對，慢）",
                            UCL_GUIStyle.GetButtonStyle(new Color(0.55f, 0.8f, 1f)),
                            GUILayout.ExpandWidth(false)))
                    {
                        // 加速層唯一該被問的問題是「它有沒有改變答案」——
                        // 所以驗法是兩條路各跑一次直接對撞，不是看數量、不是抽樣。
                        try { m_MigrateReport = UCL_ChatTavernMessageIndex.Verify(); }
                        catch (Exception e) { m_MigrateReport = $"🚨 驗證例外：{e}"; }
                    }
                }

                if (m_MigrateRunning)
                {
                    GUILayout.Label($"⏳ 執行中（{m_MigrateRunningLabel}）— 進度條可取消", WrapLabelStyle);
                }
                if (!string.IsNullOrEmpty(m_MigrateReport))
                {
                    using (var sv = new GUILayout.ScrollViewScope(m_MigrateScroll,
                               GUILayout.MinHeight(UCL_GUIStyle.GetScaledSize(200))))
                    {
                        m_MigrateScroll = sv.scrollPosition;
                        GUILayout.TextArea(m_MigrateReport, UCL_GUIStyle.TextAreaStyle);
                    }
                }
            }
        }

        void ConfirmAndMigrate()
        {
            UCL_OptionPage.Create("確認執行檔名 migration？",
                "會把全部房間的舊格式訊息檔改名為 `NNNNNNNN.json`（＝全域 seq）。\n\n"
                + "· 只改檔名，**不動任何檔案內容**\n"
                + "· 執行後會自動對帳：檔數相同、每個 seq 對到同一則訊息（比 uuid）、檔名 == seq\n"
                + "· 對帳失敗會回非零 exit code，報告在下方\n\n"
                + "**這是不可逆操作**（可用 git revert 整批還原 —— 前提是還沒 commit 別的東西上去）。\n"
                + "建議先按「試跑」確認清單。",
                new ButtonData("執行", () => RunMigrate(true),
                    UCL_GUIStyle.GetButtonStyle(new Color(1f, 0.5f, 0.3f))),
                new ButtonData("取消"));
        }

        // 區塊職責：跑 migration（**直接呼叫 C# 實作，不開 process**）
        // 物理意義：排序的唯一事實源在 UCL_ChatTavernIO_PerMsgFile.GetOrderedMessageFilePaths()，
        //          migration 直接用同一個函式 —— 不是複製規則（見該 migration 檔的檔頭）。
        //          少開一顆 process 就少一整族問題：登記 / 編碼 / stream deadlock / 逾時 / 跨平台 python。
        // 數值影響：dry-run 完全唯讀；apply 只改檔名。兩者都在主執行緒跑（有可取消的進度條）。
        void RunMigrate(bool apply)
        {
            m_MigrateRunning = true;
            m_MigrateRunningLabel = apply ? "apply" : "dry-run";
            try
            {
                var r = apply
                    ? UCL_ChatTavernMessageFileMigration.Apply()
                    : UCL_ChatTavernMessageFileMigration.Plan();
                m_MigrateReport = UCL_ChatTavernMessageFileMigration.Format(r, apply);
            }
            catch (Exception e)
            {
                // 例外不可靜默：使用者按了按鈕什麼都沒發生，看起來跟 UI 壞掉一樣。
                m_MigrateReport = $"🚨 執行例外：{e}";
                Debug.LogError($"[TavernAdmin] migration 例外: {e}");
            }
            finally
            {
                m_MigrateRunning = false;
                m_MigrateRunningLabel = "";
            }
        }

        void DrawParamSettingsPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool aShow;
                using (new GUILayout.HorizontalScope())
                {
                    aShow = UCL_GUILayout.Toggle(m_FoldDic, "ParamFold", 21);
                    GUILayout.Label("<b>⚙ 參數設定（渲染筆數）</b>", WrapLabelStyle);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("↩ 全部回預設", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        UCL_ChatTavernSettings.ResetAll();
                        m_ParamDraft.Clear();   // draft 清掉 → 下次繪製重新從實際值帶入
                        Debug.Log("[TavernAdmin] 渲染筆數參數已全部回預設");
                    }
                }
                if (!aShow) return;

                GUILayout.Label($"  筆數合法區間 [{UCL_ChatTavernSettings.MinCount}, {UCL_ChatTavernSettings.MaxCount}]；"
                                + "超出範圍會自動夾回。改完即時生效，下一個 Cmd 就吃新值。", WrapLabelStyle);
                GUILayout.Space(4);

                DrawParamRow("op=read 預設筆數", "ReadTail",
                    UCL_ChatTavernSettings.ReadTailCount, UCL_ChatTavernSettings.DefaultReadTailCount,
                    v => UCL_ChatTavernSettings.ReadTailCount = v,
                    "agent 沒帶 tail 時 _last_op.md 串幾筆 — 早安 catch-up 的主要成本來源");
                DrawParamRow("post / join 後重渲染筆數", "LastView",
                    UCL_ChatTavernSettings.LastViewTailCount, UCL_ChatTavernSettings.DefaultLastViewTailCount,
                    v => UCL_ChatTavernSettings.LastViewTailCount = v,
                    "每次發言後 _last_view.md / _last_op.md 回串幾筆給 poster 讀");
                DrawParamRow("search 預設命中上限", "SearchLimit",
                    UCL_ChatTavernSettings.SearchLimit, UCL_ChatTavernSettings.DefaultSearchLimit,
                    v => UCL_ChatTavernSettings.SearchLimit = v,
                    "op=read search=... 未帶 limit 時");
                DrawParamRow("since_seq 預設回補上限", "SinceLimit",
                    UCL_ChatTavernSettings.SinceLimit, UCL_ChatTavernSettings.DefaultSinceLimit,
                    v => UCL_ChatTavernSettings.SinceLimit = v,
                    "op=read since_seq=... 未帶 limit 時");
                DrawParamRow("wake brief §8 catch-up 筆數", "BriefCatchup",
                    UCL_ChatTavernSettings.BriefCatchupCount, UCL_ChatTavernSettings.DefaultBriefCatchupCount,
                    v => UCL_ChatTavernSettings.BriefCatchupCount = v,
                    "早安 brief 撈幾筆他人訊息（消費者是 Python 端 wake_brief.py，讀同一份 render_settings.json）");

                GUILayout.Space(4);
                GUILayout.Label("<b>　叮 catchup（Cmd_Tavern op=catchup）</b>", WrapLabelStyle);
                DrawParamRow("叮 檢視 window 筆數", "DingWindow",
                    UCL_ChatTavernSettings.DingWindowCount, UCL_ChatTavernSettings.DefaultDingWindowCount,
                    v => UCL_ChatTavernSettings.DingWindowCount = v,
                    "撈最近幾筆比對 cursor（原 --min 預設值）");
                DrawParamRow("叮 補 context 目標筆數", "DingContext",
                    UCL_ChatTavernSettings.DingContextCount, UCL_ChatTavernSettings.DefaultDingContextCount,
                    v => UCL_ChatTavernSettings.DingContextCount = v,
                    "未看訊息少於此數就補印已看過的湊滿 — 對應 ucl-ding「至少讀最近 N 條掌握 context」");
                DrawParamRow("叮 inbox 逐筆列出筆數", "DingInboxShow",
                    UCL_ChatTavernSettings.DingInboxShowCount, UCL_ChatTavernSettings.DefaultDingInboxShowCount,
                    v => UCL_ChatTavernSettings.DingInboxShowCount = v,
                    "列「最新」幾筆 @你 的待辦（較舊的只報筆數）— 有 backlog 時這個數字決定你看不看得到今天的 @");
                GUILayout.Space(4);
                GUILayout.Label("<b>　訊息顯示</b>", WrapLabelStyle);
                // ⚠ 這一列不能走 DrawParamRow —— 它夾的是「筆數」區間（1-500），
                //   600 會被靜默夾成 500：畫面顯示 500 而你以為自己設了 600。
                DrawBodyClipRow();

            }
        }

        // 區塊職責：未讀訊息內文截斷（字元）—— 與筆數列分開，因為合法區間不同。
        // 物理意義：**顯示**截斷不改原文；太小會讓短訊息也被切掉（Tim 2026-08-21 回報：
        //          200 連短訊息都讀不完，聊天接不上）。0 ＝ 不截斷。
        // 數值影響：catchup／換骰的未讀段共用它；改完下一次跑就生效（不必重編）。
        void DrawBodyClipRow()
        {
            const string aKey = "MessageBodyClip";
            int aCur = UCL_ChatTavernSettings.MessageBodyClip;
            if (!m_ParamDraft.ContainsKey(aKey)) m_ParamDraft[aKey] = aCur.ToString();
            using (new GUILayout.HorizontalScope())
            {
                if (GUILayout.Button("💾", UCL_GUIStyle.GetButtonStyle(new Color(0.6f, 1f, 0.6f)),
                        GUILayout.ExpandWidth(false)))
                {
                    if (int.TryParse(m_ParamDraft[aKey], out int aParsed))
                    {
                        int aClamped = UCL_ChatTavernSettings.ClampBodyClip(aParsed);
                        UCL_ChatTavernSettings.MessageBodyClip = aClamped;
                        m_ParamDraft[aKey] = aClamped.ToString();
                        // 夾過就要說 —— 靜默夾取會讓人以為自己設的值生效了。
                        // 夾過就要說 —— 靜默夾取會讓人以為自己設的值生效了。
                        if (aClamped != aParsed)
                            Debug.LogWarning($"[TavernAdmin] 訊息截斷：{aParsed} 超出範圍，已夾為 {aClamped}"
                                + $"（{UCL_ChatTavernSettings.MinMessageBodyClip}-{UCL_ChatTavernSettings.MaxMessageBodyClip}，0＝不截斷）");
                        else Debug.Log($"[TavernAdmin] 訊息截斷 → {aClamped}");
                    }
                    else Debug.LogWarning($"[TavernAdmin] 訊息截斷：「{m_ParamDraft[aKey]}」不是整數，未套用");
                }
                if (GUILayout.Button("↩ 預設", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    UCL_ChatTavernSettings.MessageBodyClip = UCL_ChatTavernSettings.DefaultMessageBodyClip;
                    m_ParamDraft[aKey] = UCL_ChatTavernSettings.DefaultMessageBodyClip.ToString();
                    Debug.Log($"[TavernAdmin] 訊息截斷回預設 {UCL_ChatTavernSettings.DefaultMessageBodyClip}");
                }
                GUILayout.Label("未讀訊息內文截斷（字元）", UCL_GUIStyle.LabelStyle,
                    GUILayout.Width(UCL_GUIStyle.GetScaledSize(190)));
                m_ParamDraft[aKey] = GUILayout.TextField(m_ParamDraft[aKey], UCL_GUIStyle.TextFieldStyle,
                    GUILayout.Width(UCL_GUIStyle.GetScaledSize(80)));
                GUILayout.Label($"　現值 <b>{aCur}</b>／預設 {UCL_ChatTavernSettings.DefaultMessageBodyClip}"
                    + $"　區間 {UCL_ChatTavernSettings.MinMessageBodyClip}-{UCL_ChatTavernSettings.MaxMessageBodyClip}（0＝不截斷）"
                    + "　—— catchup 與自由時間換骰的未讀段共用", WrapLabelStyle);
                GUILayout.FlexibleSpace();
            }
        }

        // 區塊職責：單一筆數參數列 — 顯示現值 / 輸入 draft / 套用 / 單項回預設。
        // 設計取捨：照本頁既有慣例用 TextField + draft（非 GUILayout.IntField）— 邊打字邊寫 prefs
        //          會讓「打到一半的 1」先被當成 1 存進去，套用鍵是刻意的一道閘。
        void DrawParamRow(string label, string draftKey, int current, int defaultValue,
                          Action<int> apply, string hint)
        {
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label($"  {label}：現值 <b>{current}</b>（預設 {defaultValue}）", WrapLabelStyle);
                GUILayout.FlexibleSpace();
                if (!m_ParamDraft.ContainsKey(draftKey)) m_ParamDraft[draftKey] = current.ToString();
                m_ParamDraft[draftKey] = GUILayout.TextField(m_ParamDraft[draftKey], UCL_GUIStyle.TextFieldStyle,
                                                             GUILayout.Width(UCL_GUIStyle.GetScaledSize(70)));
                if (GUILayout.Button("套用", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    if (int.TryParse(m_ParamDraft[draftKey], out int parsed))
                    {
                        int clamped = UCL_ChatTavernSettings.Clamp(parsed);
                        apply(clamped);
                        m_ParamDraft[draftKey] = clamped.ToString();   // 夾過的值要回寫 draft，否則 UI 說謊
                        if (clamped != parsed) Debug.LogWarning($"[TavernAdmin] {label}：{parsed} 超出範圍，已夾為 {clamped}");
                        else Debug.Log($"[TavernAdmin] {label} → {clamped}");
                    }
                    else
                    {
                        // 非數字不靜默吞：說清楚沒改，並把輸入還原成現值
                        Debug.LogWarning($"[TavernAdmin] {label}：「{m_ParamDraft[draftKey]}」不是整數，未套用");
                        m_ParamDraft[draftKey] = current.ToString();
                    }
                    GUI.FocusControl(null);
                }
                if (GUILayout.Button("↩", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    apply(defaultValue);
                    m_ParamDraft[draftKey] = defaultValue.ToString();
                    GUI.FocusControl(null);
                }
            }
            GUILayout.Label($"      ↳ {hint}", WrapLabelStyle);
        }

        // ===========================================================
        // 區塊：底層檔案捷徑
        // ===========================================================
        void DrawFilesPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool aShow;
                using (new GUILayout.HorizontalScope())
                {
                    aShow = UCL_GUILayout.Toggle(m_FoldDic, "FilesFold", 21);
                    GUILayout.Label("<b>🗂 底層檔案</b>", WrapLabelStyle);
                    GUILayout.FlexibleSpace();
                }
                if (!aShow) return;
                DrawFileRow("notify_config.json（mirror/頭像/路由設定）", NotifyConfigPath);
                DrawFileRow("_tavern_state.json（同步進度 state）", TavernStatePath);
                DrawFileRow("_drain.log（同步結果 log）", DrainLogPath);
            }
        }

        void DrawFileRow(string label, string path)
        {
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label($"{label}：{path}", WrapLabelStyle);
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(!File.Exists(path)))
                {
                    if (GUILayout.Button("📂 開啟", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        EditorUtility.RevealInFinder(path);
                    }
                }
            }
        }
    }
}
#endif
