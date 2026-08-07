// 區塊職責：新 Reading Library（work → media → persona reader root）的**寫入端** Cmd 骨架。
// 物理意義：寫 <DataRoot>/BookNotes/Library/ 下的 work.json / media.json / reader.json /
//          chapters/<chapter-id>/(chapter.json + r<N>_<date>.md)；note_chapter 完成後將心得
//          同步發一篇進酒館，並把回傳的 seq 寫回該 round 當 receipt。
// 數值影響：只寫 Library/；**不讀不寫 Archive/**（Archive 唯讀，僅供人工遷移參考）。
//          目前寫入本體未實作 —— 規格待 Tim 拍板（見 ShortDescription 與各 op 的 throw 訊息）。
// 設計取捨：
//   - 為什麼是 C# 而非 Python：發文要走 Cmd_Tavern 同一條路徑才不會漏 mirror / inbox 路由 /
//     mention 解析 / 計酬判定。Python 端得 spawn run_cmd 再刮 stdout 拿 seq，那段跨行程接縫
//     在 Editor 內是 in-process 呼叫，直接消失。(Tim 2026-08-06 拍板「透過 CMD 串接 C#」)
//   - 為什麼 op 名用底線：Cmd 參數是 Dictionary<string,string>，op 值與既有 Cmd_Bartender
//     (time_add / time_list) 同慣例；不引入第二套命名風格。
//   - 為什麼身分三件套一律必填無預設：`bookmark --reader` 的預設值 2026-08-05 覆蓋掉另一位
//     persona 的長書籤。多讀者環境裡預設值是裝填好的槍，這裡一顆都不裝。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using Cysharp.Threading.Tasks;
using UCL.Core.EditorLib.AgentCommands.ChatTavern;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.ReadingLibrary
{
    /// <summary>
    /// 區塊職責：閱讀心得庫寫入指令（骨架）。
    /// 物理意義：唯一被允許寫 <c>BookNotes/Library/</c> 的程式入口；agent 端一律走
    ///           <c>run_cmd.py run Library --arg op=...</c>，不再各自手寫 JSON。
    /// 數值影響：寫入本體未實作；目前只做路徑解析與參數驗證，並以 op=paths 供驗收。
    ///
    /// <para>典型用法（規格定案後）：</para>
    /// <code>
    /// python &lt;UCL_Core&gt;/Tools~/AgentCommands/run_cmd.py run Library \
    ///   --arg op=note_chapter --arg media_id=film-xxx --arg persona=summit \
    ///   --arg chapter=0001 --wait-reply 0 --arg-stdin body
    /// </code>
    /// </summary>
    public class Cmd_Library : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "Library";

        public override string ShortDescription =>
            "閱讀心得庫讀寫（新 work/media/reader 模型）— 讀回與寫入同一套實作，與閱讀心得管理頁共用。";

        public override string ArgsSchema =>
            "op=paths|recall|media_init|note_chapter|bookmark|add_character|revise_view|share|scan（required；scan 免其他參數） | " +
            "agent=酒館發文的錢包身分，例 Zeta（share required —— 計酬進誰的帳不能猜） | " +
            "room=酒館房間 id（share 選填，default tavern） | " +
            "round=要分享的 round 號（share 選填；缺 = 該章最新一輪） | " +
            "character=人物 id（add_character / revise_view required） | " +
            "name=人物顯示名（add_character required） | name_original=原文讀音，供 STT prompt 用（選填） | " +
            "facts=已確認的客觀資料（選填；與主觀 view 分開存） | " +
            "view=你的第一人稱看法（add_character / revise_view required；長文走 --arg-stdin view） | " +
            "change_reason=什麼畫面或台詞讓你改觀（revise_view required —— 為什麼變比變成什麼更難事後重建） | " +
            "persona=讀者 persona，必須與 readers/<persona>/reader.json 相符（required，無預設） | " +
            "media_id=媒材 id，前綴須與 media_kind 同字，例 film-xxx / comic-xxx（required，無預設） | " +
            "work_id=作品 id（media_init required；同作品跨媒材共用） | " +
            "media_kind=comic|anim|film|series|stream|book（media_init required，須與 media_id 前綴同字） | " +
            "title=作品中文名（media_init required）／章節名（note_chapter 選填） | " +
            "title_original=原文名（media_init 選填） | author=作者／監督（media_init 選填） | " +
            "aliases=日後搜尋用的別名，`|` 或 `,` 分隔（media_init 強烈建議：中／日／英 + 常見異譯；" +
            "漏建的後果是「搜不到 → 有人再建一本」） | genre_tags=題材標籤，同上分隔 | " +
            "anticipation=期待度 0-5（media_init 選填） | " +
            "chapter=四位數章節 id；0001 起算，0000 保留給序章（note_chapter required） | " +
            "body=章節心得正文（note_chapter required；長文走 --arg-stdin body） | " +
            "time_range=手動切段的時間區間，例 00:00-30:00（note_chapter 建議必給 —— 這是切段動作留下的事實） | " +
            "display_number=人話段落名，例 Part 1（note_chapter 選填；缺則由 chapter_id 派生，別填成 id 複寫） | " +
            "impression=更新後的當前看法（選填） | bookmark_note=書籤（選填） | " +
            "note=書籤內文（bookmark op） | status=reading|finished|dropped（bookmark op 選填） | " +
            "full=recall 是否印出每個 round 全文（default true）";

        public override string ExampleArgs =>
            "op=paths;persona=summit;media_id=comic-delicious-in-dungeon";

        public override string HelpURL =>
            "ucl_core:Docs~/{lang}/Workflows/Reading_Library_Workflow.md";

        // 區塊職責：id 形狀守門 —— 任何會被接到路徑上的值都必須先驗形狀。
        // 物理意義：擋掉路徑分隔符與 ..，避免 persona / media_id 逃出 Library 根。
        // 數值影響：純驗證；不符即 throw，不做「清洗後照用」（清洗會讓錯誤靜默）。
        static readonly Regex k_IdPattern = new Regex(@"^[A-Za-z0-9][A-Za-z0-9_-]*$");

        // 章節 id 是四位數字串：0000 = 序章（非必有），正篇自 0001 起算（Tim 2026-08-06）。
        static readonly Regex k_ChapterIdPattern = new Regex(@"^\d{4}$");

        const string k_LibraryDirName = "BookNotes";
        const string k_LibraryRootName = "Library";
        const string k_MediaDirName = "media";
        const string k_ReadersDirName = "readers";
        const string k_ReaderJsonName = "reader.json";

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            await UniTask.Yield();

            string op = GetArg(args, "op", "").Trim();
            if (string.IsNullOrWhiteSpace(op))
                throw new ArgumentException($"[{CommandType}] op 必填（paths / media_init / note_chapter / bookmark）");

            switch (op)
            {
                case "paths": Op_Paths(args); break;
                case "recall": Op_Recall(args); break;
                case "media_init": Op_MediaInit(args); break;
                case "note_chapter": Op_NoteChapter(args); break;
                case "bookmark": Op_Bookmark(args); break;
                case "add_character": Op_AddCharacter(args); break;
                case "revise_view": Op_ReviseView(args); break;
                case "share": await Op_Share(args, token); break;
                case "scan": Op_Scan(); break;

                default:
                    throw new ArgumentException(
                        $"[{CommandType}] 未知 op：{op}" +
                        "（可用：paths / recall / media_init / note_chapter / bookmark / " +
                        "add_character / revise_view / share / scan）");
            }
        }

        /// <summary>
        /// 區塊職責：印出本次身分解析出來的實際路徑，供規格討論與驗收用（唯讀）。
        /// 物理意義：先證明「我算出來的 reader root 就是磁碟上那個」，再談寫入。
        /// 數值影響：不寫任何檔；reader.json 不存在也只是報告不存在，不建檔。
        /// </summary>
        void Op_Paths(Dictionary<string, string> args)
        {
            string persona = RequireId(args, "persona");
            string mediaId = RequireId(args, "media_id");

            string libraryRoot = Path.Combine(UCL_AgentCommandsPath.DataRoot, k_LibraryDirName, k_LibraryRootName);
            string mediaRoot = Path.Combine(libraryRoot, k_MediaDirName, mediaId);
            string readerRoot = Path.Combine(mediaRoot, k_ReadersDirName, persona);
            string readerJson = Path.Combine(readerRoot, k_ReaderJsonName);

            // 區塊職責：把解析結果寫進 _last_op.md，而不是只寫 Console。
            // 物理意義：呼叫端（agent 走 run_cmd.py）讀不到 Unity Console —— 只 Debug.Log 的話，
            //          這支「用來驗證路徑」的 op 會回報成功卻什麼都驗不到（實測 2026-08-06）。
            // 數值影響：純輸出；_last_op.md 是既有的 Cmd 回報通道，每次呼叫覆寫。
            string md =
                $"# 📚 Library paths\n\n" +
                $"- **persona**: `{persona}`\n" +
                $"- **media_id**: `{mediaId}`\n\n" +
                $"| 路徑 | 值 | 存在 |\n|---|---|---|\n" +
                $"| library_root | `{libraryRoot}` | {Directory.Exists(libraryRoot)} |\n" +
                $"| media_root | `{mediaRoot}` | {Directory.Exists(mediaRoot)} |\n" +
                $"| reader_root | `{readerRoot}` | {Directory.Exists(readerRoot)} |\n" +
                $"| reader.json | `{readerJson}` | {File.Exists(readerJson)} |\n\n" +
                $"> `reader.json` 不存在代表這位 persona 在此 media 尚無新架構紀錄 —— " +
                $"依 note_chapter 的前置階梯，該先查 Archive（有舊日記→先遷移）或走 media_init 建檔。\n";
            Cmd_Library_Helpers.ResolveLastOp(md);

            Debug.Log($"[{CommandType}] paths → reader_root={readerRoot} (exists={Directory.Exists(readerRoot)})");
        }

        /// <summary>
        /// 區塊職責：讀回 —— 與 UCL_ReadingNotesManagePage 呼叫同一段（Tim 2026-08-06：讀寫同框架）。
        /// 物理意義：把該 persona 在該 media 的累積紀錄組成單一視圖寫進 _last_op.md 供 agent 讀回。
        /// 數值影響：純讀。
        /// </summary>
        void Op_Recall(Dictionary<string, string> args)
        {
            string persona = RequireId(args, "persona");
            string mediaId = RequireId(args, "media_id");
            bool full = GetArg(args, "full", "true").Trim().ToLowerInvariant() != "false";

            // 區塊職責：先產檔、再把「檔在哪」報回呼叫端（Tim 2026-08-06 QA：要能落檔才能檢視格式）。
            // 物理意義：`_last_op.md` 是共用通道會被其他 Cmd 覆寫；追回檔要住 persona 自己的 letters/，
            //          與 `_wake_brief.md` 同一個家、同樣是可重建的機械視圖。
            // 數值影響：完整覆寫同一份 `_reading_recall_<media-id>.md`；原始筆記不動。
            string path = UCL_ReadingLibraryIO.WriteRecallBrief(mediaId, persona, full, out string error);
            if (path == null)
                throw new InvalidOperationException($"[{CommandType}] recall 失敗：{error}");

            Cmd_Library_Helpers.ResolveLastOp(
                $"# 📖 Library recall\n\n" +
                $"- **persona**: `{persona}`　**media**: `{mediaId}`　full_rounds: `{full}`\n" +
                $"- 追回檔已生成：`{path}`\n\n" +
                $"→ **Read 那份檔**（章節 round 全文 + 人物 facts 與看法版本史都在裡面）。\n" +
                $"本檔是機械產物，每次 recall 或寫入後重新生成 —— 手改會被覆寫。\n");
            Debug.Log($"[{CommandType}] recall → {path}（full={full}）");
        }

        /// <summary>
        /// 區塊職責：建檔 —— 查無舊心得時依新規則建立 work / media / reader（Tim 2026-08-06 拍板走 CMD）。
        /// 物理意義：三層各一個 json；已存在的檔不覆寫（重跑不該蓋掉既有進度）。
        /// 數值影響：media_kind 必須在 enum 內且與 media_id 前綴同字，否則 reject。
        /// </summary>
        void Op_MediaInit(Dictionary<string, string> args)
        {
            string persona = RequireId(args, "persona");
            string mediaId = RequireId(args, "media_id");
            string workId = RequireId(args, "work_id");
            string mediaKind = GetArg(args, "media_kind", "").Trim();
            string title = GetArg(args, "title", "").Trim();
            string titleOriginal = GetArg(args, "title_original", "").Trim();
            string author = GetArg(args, "author", "").Trim();

            if (string.IsNullOrEmpty(title))
                throw new ArgumentException($"[{CommandType}] title 必填（作品中文名 —— 之後靠它認人，不能空）");
            if (Array.IndexOf(UCL_ReadingLibraryIO.MediaKinds, mediaKind) < 0)
                throw new ArgumentException(
                    $"[{CommandType}] media_kind 非法：{mediaKind}" +
                    $"（合法值：{string.Join(" / ", UCL_ReadingLibraryIO.MediaKinds)}）");
            if (!mediaId.StartsWith(mediaKind + "-", StringComparison.Ordinal))
                throw new ArgumentException(
                    $"[{CommandType}] media_id 前綴須與 media_kind 同字：media_kind={mediaKind} " +
                    $"→ media_id 應為 `{mediaKind}-<work>`，實得 {mediaId}（兩欄互為校驗，不可各說各話）");

            int anticipation = 0;
            int.TryParse(GetArg(args, "anticipation", "0"), out anticipation);

            string log = UCL_ReadingLibraryIO.MediaInit(workId, mediaId, mediaKind, persona,
                title, titleOriginal, author, anticipation,
                UCL_ReadingLibraryIO.SplitList(GetArg(args, "aliases", "")),
                UCL_ReadingLibraryIO.SplitList(GetArg(args, "genre_tags", "")),
                out string error);
            if (!string.IsNullOrEmpty(error))
                throw new InvalidOperationException($"[{CommandType}] media_init 失敗：{error}\n{log}");

            Cmd_Library_Helpers.ResolveLastOp(
                $"# 📚 Library media_init\n\n{log}\n" +
                $"→ 接著跑 `op=note_chapter --arg chapter=0001`（0000 保留給序章，非必有）。\n");
            Debug.Log($"[{CommandType}] media_init → {mediaId} / {persona}");
        }

        /// <summary>
        /// 區塊職責：落一筆章節心得（本 Cmd 的主用途）。
        /// 物理意義：round md 是事實源 → chapter.json 索引 → reader.json 當前狀態 → bookshelf 投影。
        /// 數值影響：既有 round 絕不覆寫（同章再寫開下一個 r{N}）；沒有自己的紀錄則停下不自作主張建檔。
        /// </summary>
        void Op_NoteChapter(Dictionary<string, string> args)
        {
            string persona = RequireId(args, "persona");
            string mediaId = RequireId(args, "media_id");
            string chapterId = GetArg(args, "chapter", "").Trim();
            if (!UCL_ReadingLibraryIO.IsValidChapterId(chapterId))
                throw new ArgumentException(
                    $"[{CommandType}] chapter 須為四位數字（0001 起算，0000 保留給序章）：{chapterId}");

            string body = GetArg(args, "body", "");
            if (string.IsNullOrWhiteSpace(body))
                throw new ArgumentException($"[{CommandType}] body 必填（本章心得正文；長文走 --arg-stdin body）");

            string log = UCL_ReadingLibraryIO.NoteChapter(
                mediaId, persona, chapterId,
                GetArg(args, "display_number", "").Trim(),
                GetArg(args, "title", "").Trim(),
                GetArg(args, "time_range", "").Trim(),
                body,
                GetArg(args, "impression", "").Trim(),
                GetArg(args, "bookmark_note", "").Trim(),
                out string roundPath, out int roundNumber, out string error);

            if (log == null)
                throw new InvalidOperationException($"[{CommandType}] note_chapter 失敗：{error}");

            // 區塊職責：寫完後把「上次讀到哪」推回呼叫端（basecamp 2026-08-06：指路可以被跳過，推播不能）。
            // 物理意義：讀回與寫入走同一支 Cmd，寫完當場附上該 media 的最新書籤與看法。
            // 數值影響：純讀附加；recall 失敗不影響已落盤的心得（檔優先於投影）。
            string recall = UCL_ReadingLibraryIO.RenderRecall(mediaId, persona, false, out string recallErr);

            Cmd_Library_Helpers.ResolveLastOp(
                $"# 📚 Library note_chapter\n\n{log}\n" +
                $"- 路徑：`{roundPath}`\n\n---\n\n" +
                (recall ?? $"> [!WARNING]\n> 心得已落盤，但讀回視圖生成失敗：{recallErr}\n"));

            Debug.Log($"[{CommandType}] note_chapter → {mediaId} / {persona} / {chapterId} r{roundNumber}");
        }

        /// <summary>
        /// 區塊職責：把某章某 round 的心得發進酒館，並把 seq 落回該 round 當 receipt。
        /// 物理意義：發文**必須**走 Cmd_Tavern 的 Op_Post 同一條 pipeline（in-process 經 registry 呼叫）——
        ///           自呼 WriteMessageWithSeq 會漏 mirror / inbox 路由 / mention 解析 / 計酬判定四件事
        ///           （2026-08-06 定案的硬規則）。seq 由 Cmd_Tavern.LastPostSeq static slot 取回。
        /// 數值影響：心得檔只讀不寫（除了 shared_seq receipt）；發文失敗不回滾任何檔
        ///           （檔優先於投影）。同 round 已有 shared_seq → 拒發（防重複計酬）。
        /// </summary>
        async UniTask Op_Share(Dictionary<string, string> args, CancellationToken token)
        {
            string persona = RequireId(args, "persona");
            string mediaId = RequireId(args, "media_id");
            string agent = GetArg(args, "agent", "").Trim();
            if (string.IsNullOrEmpty(agent))
                throw new ArgumentException($"[{CommandType}] agent 必填（酒館發文的錢包身分）—— 計酬進誰的帳不能猜");
            string chapterId = GetArg(args, "chapter", "").Trim();
            if (!UCL_ReadingLibraryIO.IsValidChapterId(chapterId))
                throw new ArgumentException($"[{CommandType}] chapter 須為四位數字：{chapterId}");
            int round = int.TryParse(GetArg(args, "round", "").Trim(), out int r) ? r : 0;

            string body = UCL_ReadingLibraryIO.BuildShareBody(mediaId, persona, chapterId, ref round, out string error);
            if (body == null)
                throw new InvalidOperationException($"[{CommandType}] share 失敗：{error}");

            // 經 registry 拿 Tavern handler —— 跟 queue 分發同一個 instance，同一條 Op_Post pipeline
            var tavern = UCL_AgentCommandRegistry.Get("Tavern");
            if (tavern == null)
                throw new InvalidOperationException($"[{CommandType}] 找不到 Tavern handler —— registry 未註冊？");
            var tavernArgs = new Dictionary<string, string>
            {
                ["op"] = "post",
                ["room"] = GetArg(args, "room", "tavern").Trim(),
                ["agent"] = agent,
                ["persona"] = persona,
                ["body"] = body,
                ["meta"] = "{\"tag\":\"reading-note\",\"category\":\"reading\"}",
            };
            // token enforce / caller 環境標記照原樣穿透 —— share 不該是繞過驗證的側門
            if (args.TryGetValue("session_token", out string st) && !string.IsNullOrEmpty(st))
                tavernArgs["session_token"] = st;
            if (args.TryGetValue("_caller_env_marker", out string cem) && !string.IsNullOrEmpty(cem))
                tavernArgs["_caller_env_marker"] = cem;

            ChatTavern.Cmd_Tavern.LastPostSeq = 0;
            await tavern.ExecuteAsync(tavernArgs, token);
            int seq = ChatTavern.Cmd_Tavern.LastPostSeq;
            if (seq <= 0)
                throw new InvalidOperationException(
                    $"[{CommandType}] 酒館發文未取得 seq —— post 可能被 Op_Post 拒絕（原因見 _last_op.md）。" +
                    "心得檔不受影響，修好參數重新 share 即可。");

            UCL_ReadingLibraryIO.RecordSharedSeq(mediaId, persona, chapterId, round, seq, out string recErr);
            string receiptNote = string.IsNullOrEmpty(recErr)
                ? $"- receipt：`shared_seq={seq}` 已落 chapter.json round {round}"
                : $"> [!WARNING]\n> 已發文（seq={seq}）但 receipt 落檔失敗：{recErr}\n" +
                  $"> 請人工把 shared_seq={seq} 補進該 round —— 別重發（會重複計酬）。";

            Cmd_Library_Helpers.ResolveLastOp(
                $"# 📚 Library share\n\n- ✅ 已發酒館：seq={seq}（{mediaId} / {chapterId} r{round} by {persona}）\n{receiptNote}");
            Debug.Log($"[{CommandType}] share → {mediaId}/{chapterId} r{round} seq={seq}");
        }

        /// <summary>
        /// 區塊職責：op=scan —— Library / Archive 重複與異常候選審計（唯讀，Q4 定案）。
        /// 物理意義：印候選給人裁決；不合併、不搬移、不改資料（Q3：偵測自動、遷移人工）。
        /// 數值影響：唯一寫入是報告檔 _migration/scan_report.md（機械產物）。
        /// </summary>
        void Op_Scan()
        {
            string report = UCL_ReadingLibraryIO.ScanLibrary(out string reportPath, out string error);
            Cmd_Library_Helpers.ResolveLastOp(
                report +
                (reportPath != null ? $"\n\n📄 報告檔：`{reportPath}`" : "") +
                (string.IsNullOrEmpty(error) ? "" : $"\n\n> [!WARNING]\n> {error}"));
            Debug.Log($"[{CommandType}] scan 完成" + (reportPath != null ? $" → {reportPath}" : ""));
        }

        /// <summary>
        /// 區塊職責：第一次記一個人物（facts 客觀 / view 第一人稱，分開存）。
        /// 物理意義：已存在就 reject 並指路 revise_view —— 覆寫 v1 等於抹掉「當時我還不知道」。
        /// </summary>
        void Op_AddCharacter(Dictionary<string, string> args)
        {
            string persona = RequireId(args, "persona");
            string mediaId = RequireId(args, "media_id");
            string characterId = RequireId(args, "character");
            string name = GetArg(args, "name", "").Trim();
            string view = GetArg(args, "view", "");

            if (string.IsNullOrEmpty(name))
                throw new ArgumentException($"[{CommandType}] name 必填（人物顯示名）");
            if (string.IsNullOrWhiteSpace(view))
                throw new ArgumentException(
                    $"[{CommandType}] view 必填（你的第一人稱看法；長文走 --arg-stdin view）—— " +
                    "只記 facts 不記看法的話，這套系統就退化成人物百科了");

            string log = UCL_ReadingLibraryIO.AddCharacter(mediaId, persona, characterId, name,
                GetArg(args, "name_original", "").Trim(), GetArg(args, "facts", "").Trim(), view,
                out string error);
            if (log == null)
                throw new InvalidOperationException($"[{CommandType}] add_character 失敗：{error}");

            Cmd_Library_Helpers.ResolveLastOp($"# 🧑 Library add_character\n\n{log}\n");
            Debug.Log($"[{CommandType}] add_character → {mediaId} / {persona} / {characterId}");
        }

        /// <summary>
        /// 區塊職責：改觀 → fork 下一版看法（絕不覆寫舊版）。
        /// 物理意義：change_reason 必填 —— 「為什麼變」比「變成什麼」更難事後重建。
        /// </summary>
        void Op_ReviseView(Dictionary<string, string> args)
        {
            string persona = RequireId(args, "persona");
            string mediaId = RequireId(args, "media_id");
            string characterId = RequireId(args, "character");
            string view = GetArg(args, "view", "");
            string changeReason = GetArg(args, "change_reason", "").Trim();

            if (string.IsNullOrWhiteSpace(view))
                throw new ArgumentException($"[{CommandType}] view 必填（新版看法；長文走 --arg-stdin view）");
            if (string.IsNullOrEmpty(changeReason))
                throw new ArgumentException(
                    $"[{CommandType}] change_reason 必填 —— 是什麼畫面／台詞讓你改觀？" +
                    "沒有觸發事件的「改觀」多半只是換句話說，那不值得開新版本");

            string log = UCL_ReadingLibraryIO.ReviseView(mediaId, persona, characterId, view, changeReason,
                GetArg(args, "facts", "").Trim(), out string error);
            if (log == null)
                throw new InvalidOperationException($"[{CommandType}] revise_view 失敗：{error}");

            Cmd_Library_Helpers.ResolveLastOp($"# 🧑 Library revise_view\n\n{log}\n");
            Debug.Log($"[{CommandType}] revise_view → {mediaId} / {persona} / {characterId}");
        }

        /// <summary>只更新書籤 / 當前看法 / status（不落章節）。</summary>
        void Op_Bookmark(Dictionary<string, string> args)
        {
            string persona = RequireId(args, "persona");
            string mediaId = RequireId(args, "media_id");
            string note = GetArg(args, "note", "").Trim();
            string impression = GetArg(args, "impression", "").Trim();
            string status = GetArg(args, "status", "").Trim();
            if (string.IsNullOrEmpty(note) && string.IsNullOrEmpty(impression) && string.IsNullOrEmpty(status))
                throw new ArgumentException($"[{CommandType}] bookmark 至少要給 note / impression / status 之一");

            string log = UCL_ReadingLibraryIO.Bookmark(mediaId, persona, note, impression, status, out string error);
            if (log == null)
                throw new InvalidOperationException($"[{CommandType}] bookmark 失敗：{error}");

            Cmd_Library_Helpers.ResolveLastOp($"# 📚 Library bookmark\n\n{log}\n");
            Debug.Log($"[{CommandType}] bookmark → {mediaId} / {persona}");
        }

        /// <summary>
        /// 必填 id 取值 + 形狀驗證。缺值與形狀不符都 throw —— 兩者都不給預設。
        /// </summary>
        string RequireId(Dictionary<string, string> args, string key)
        {
            string value = GetArg(args, key, "").Trim();
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException($"[{CommandType}] {key} 必填且無預設值（多讀者環境不可靠預設身分）");
            if (!k_IdPattern.IsMatch(value))
                throw new ArgumentException(
                    $"[{CommandType}] {key} 只允許英數、底線、連字號，且不可含路徑分隔符：{value}");
            return value;
        }

        /// <summary>
        /// 章節 id 驗證 —— 四位數字；0000 保留給序章，正篇自 0001 起算。
        /// 目前僅供寫入 op 定案後使用；保留在骨架裡是為了讓規格討論有可指的實體。
        /// </summary>
        internal static bool IsValidChapterId(string chapterId)
            => !string.IsNullOrEmpty(chapterId) && k_ChapterIdPattern.IsMatch(chapterId);
    }

    // 區塊職責：Cmd 回報通道 helper。
    // 物理意義：`ResolveLastOp` / `RejectLastOp` **不在** UCL_AgentCommandHandlerBase 上
    //          （假設繼承會撞 CS0103，2026-05-11 實證），各 Cmd 自帶 internal static helper 是
    //          Cmd_Tavern / Cmd_Treasury / Cmd_Glossary 的既有慣例 —— 這裡沿用，不發明第二套。
    // 數值影響：只寫 _last_op.md（每次覆寫）；RejectLastOp 額外 throw，讓失敗傳回呼叫端而非靜默。
    internal static class Cmd_Library_Helpers
    {
        public static void ResolveLastOp(string md) => UCL_ChatTavernRender.WriteLastOp(md);
    }
}
#endif
