// 區塊職責：新 Reading Library（work → media → persona reader root）的**唯一 schema 實作者**。
// 物理意義：讀與寫都在這一層 —— Cmd_Library（agent 入口）與 UCL_ReadingNotesManagePage（人的入口）
//          都呼叫本檔，人點按鈕看到的與 agent 拿到的必然一致。
// 數值影響：只讀寫 <DataRoot>/BookNotes/Library/；**不讀不寫 Archive/**（Archive 唯讀，
//          僅供人工遷移參考，遷移紀錄走 BookNotes/_migration/registry.json）。
// 設計取捨：
//   - 為什麼讀寫同層：Tim 2026-08-06 拍板「讀取跟寫入框架要相符，都在 C# 端避免漂移」。
//     原案（Python 讀 / C# 寫）本身就是同一份 schema 被兩個語言各理解一次 —— 真正的漂移源。
//   - 為什麼用 JsonData 而非 JsonUtility：JsonUtility 會把型別上沒宣告的欄位**靜默吐掉**，
//     而 reader.json / chapter.json 是多方（其他 persona、未來的 schema 版本）共寫的檔；
//     read-modify-write 掉欄位是不會叫的壞掉。JsonData 是保序 DOM，只動我指名的 key。
//   - 為什麼章節 round 不覆寫：作品可以重讀，無知不能 —— 一讀時的「還不知道」是紀錄的一部分。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UCL.Core.JsonLib;
using Debug = UnityEngine.Debug;

namespace UCL.Core.EditorLib.AgentCommands.ReadingLibrary
{
    /// <summary>
    /// 閱讀心得庫 IO 服務。所有路徑、schema 欄位、寫入不變量集中在此。
    /// </summary>
    public static class UCL_ReadingLibraryIO
    {
        // ── schema key 常數化（字串 key 散落各處是改名時的靜默漂移源）──
        public const string Key_SchemaVersion = "schema_version";
        public const string Key_ReaderPersona = "reader_persona";
        public const string Key_MediaId = "media_id";
        public const string Key_MediaKind = "media_kind";
        public const string Key_WorkId = "work_id";
        public const string Key_Status = "status";
        public const string Key_Anticipation = "anticipation";
        public const string Key_Progress = "progress";
        public const string Key_CurrentChapterId = "current_chapter_id";
        public const string Key_LastRead = "last_read";
        public const string Key_BookmarkNote = "bookmark_note";
        public const string Key_CurrentImpression = "current_impression";
        public const string Key_UpdatedAt = "updated_at";
        public const string Key_ChapterId = "chapter_id";
        public const string Key_DisplayNumber = "display_number";
        public const string Key_Title = "title";
        public const string Key_TitleOriginal = "title_original";
        public const string Key_Author = "author";
        public const string Key_TimeRange = "time_range";
        public const string Key_Rounds = "rounds";
        public const string Key_Round = "round";
        public const string Key_ReadingDate = "reading_date";
        public const string Key_File = "file";
        public const string Key_SharedSeq = "shared_seq";
        public const string Key_Gap = "gap";

        /// <summary>
        /// 這一個 round 是由幾**場**寫成的（沒有這個欄位 ＝ 1 場，舊資料不必遷移）。
        /// <para>🩸 TASK-0121：一話分兩場看完，第二場照舊會開 `r2` ⇒ 讀的人（含未來的自己）
        /// 看到 r1+r2 會讀成「她重看過一次」，而那個誤讀**不會有任何一層報錯**。
        /// ⇒ 續寫改成追加進同一個 round，場數記在這裡；`r{N}` 的語意維持
        /// 「第 N 次**讀這一話**」，而不是「第 N 次寫入」。</para>
        /// </summary>
        public const string Key_Segments = "segments";
        public const string Key_ReadingStartedAt = "reading_started_at";
        public const string Key_Aliases = "aliases";
        public const string Key_GenreTags = "genre_tags";

        // ===========================================================
        // 區塊職責：寫書線（authored）在 work.json 上的四欄 —— TASK-0146 ①
        // 物理意義：舊 store（`BookNotes/<book>/book.json`）用這四欄承接「這本正在寫、只有作者看得見」。
        //          新 store 的 work.json 原本一欄都沒有 ⇒ **搬過去就沒有任何欄位承接得了「正在寫」**。
        // 🔴 為什麼是四欄不是三欄（開單時寫三欄，這是量出來的第四欄）：
        //   `SCP_BookStore` 列 authored 書的第一個動作是 `origin != "authored" ⇒ 跳過`，
        //   下面三欄是**過了那一關之後才被讀的**。
        //   ⇒ 少了 `origin`，搬過去的書對整條寫書線**完全不可見**，而失效樣子是「**0 本**」——
        //     跟「這個人沒有在寫的書」**同形**（早安 brief 見筆那一節印的就是這個數字）。
        //   📌 所以它不是加需求：① 自己寫著「三欄的**語意**要跟舊 store 一致」，
        //     而語意的承重點在 `origin` 上。
        // ⚠ `Key_Status` 是**共用的鍵名而不是共用的語意**：
        //   reader.json 的 `status` ＝這個人**讀到哪**；work.json 的 `status` ＝這部作品**寫到哪**
        //   （舊 store 的值是 `writing`）。兩個不同層、兩種意思、同一個字。
        //   ⛔ 別把任何一邊的讀取器指到另一邊 —— 它不會報錯，它會給你另一個宇宙的答案。
        // 數值影響：**四欄都只在非空時才落盤** ⇒ 純閱讀作品的 work.json 逐位元組不變
        //          （① 明文要求「既有欄位與讀取端一個字不動」）。
        // ===========================================================
        public const string Key_AuthorPersona = "author_persona";
        public const string Key_PublishStatus = "publish_status";
        public const string Key_Origin = "origin";
        /// <summary>舊 store 的 `origin` 值：唯一代表「這是自己寫的」的那個字面。</summary>
        public const string OriginAuthored = "authored";

        const string k_BookNotesDirName = "BookNotes";
        const string k_LibraryDirName = "Library";
        const string k_MediaDirName = "media";
        const string k_WorksDirName = "works";
        const string k_ReadersDirName = "readers";
        const string k_ChaptersDirName = "chapters";
        const string k_ReaderJsonName = "reader.json";
        const string k_MediaJsonName = "media.json";
        const string k_WorkJsonName = "work.json";
        // authored 正文的兩個容器（TASK-0146 ②）—— 名字刻意與舊 store 逐字相同，讓遷移是「搬」不是「改版面」
        const string k_WorkChaptersDirName = "chapters";
        const string k_WorkArcsDirName = "arcs";
        const string k_ChapterJsonName = "chapter.json";
        const string k_BookshelfName = "bookshelf.md";
        const string k_CharactersDirName = "characters";
        const string k_ProfileJsonName = "profile.json";
        // 追回檔輸出路徑（與 wake brief 同住 persona 的 letters/cmd/）
        // ⛔ 原本這裡有 k_ChatTavernDirName / k_BatonDirName / k_LettersDirName 三個常數
        //    自己拼出 letters 路徑 —— 那是把佈局知識複製了一份（BUG-2）。
        //    letters 的唯一入口是 `UCL_LettersPath`，佈局調整時只有它需要改。

        public const string Key_CharacterId = "character_id";
        public const string Key_Name = "name";
        public const string Key_NameOriginal = "name_original";
        public const string Key_Facts = "facts";

        /// <summary>
        /// 區塊職責：media_kind 合法值 —— 與 media-id 前綴同字（Tim 2026-08-06 定案）。
        /// 物理意義：兩個欄位互為對方的校驗；另造名字（movie）等於同一件事兩個名字。
        /// 數值影響：不在清單內即 reject，不做「清洗後照用」。
        /// </summary>
        public static readonly string[] MediaKinds = { "comic", "anim", "film", "series", "stream", "book" };

        static readonly Regex k_IdPattern = new Regex(@"^[A-Za-z0-9][A-Za-z0-9_-]*$");
        static readonly Regex k_ChapterIdPattern = new Regex(@"^\d{4}$");

        /// <summary>序章保留號；非必有，且排除在章節連續性判定之外。</summary>
        public const string PrologueChapterId = "0000";

        // ===========================================================
        // 路徑層
        // 物理意義：一律由 UCL_RepoPath.AgentCommandsDir 推導，不寫死 UCL_Core 安裝路徑。
        // ===========================================================
        public static string BookNotesRoot => Path.Combine(UCL_RepoPath.AgentCommandsDir, k_BookNotesDirName);
        public static string LibraryRoot => Path.Combine(BookNotesRoot, k_LibraryDirName);
        public static string MediaRoot(string mediaId) => Path.Combine(LibraryRoot, k_MediaDirName, mediaId);
        public static string WorkRoot(string workId) => Path.Combine(LibraryRoot, k_WorksDirName, workId);

        // ===========================================================
        // 區塊職責：authored 正文的容器 —— TASK-0146 ②（設計決定寫在該單留言，⛔ 不只定在這裡）
        // 物理意義：正文放 **work 層**，與 work.json 並列：
        //            works/<work_id>/chapters/<NNN>.md   章
        //            works/<work_id>/arcs/<range>.md     卷／弧
        //          為什麼不是 media 層或 reader 層：三層語意是
        //          work（作品本身）／media（某個版本或媒材）／reader（某個人的進度與看法），
        //          而 authored 的正文**是作品本身**，不是任何人的閱讀視圖。
        //          一個譯本會是另一個 media，而原文屬於 work。
        //          ⇒ 放 reader 層會讓「作者」被當成一個 reader，那是把寫書塞進讀書的形狀。
        // ⚠ 為什麼維持與舊 store 相同的相對版面（`<book>/chapters/`）：遷移就變成「搬目錄」，
        //   而不是「重新設計落點」⇒ 逐欄對拍不必為位置差異多一層映射。
        //   ⛔ 不趁遷移順手改版面 —— 那會讓「搬壞了」與「版面改了」在對拍結果上同形。
        // 🩸 而 `arcs/` 是量出來才補進來的：② 原文只寫「章的容器」，
        //   而 ④ 指定第一本搬的 @gura《深海對拍錄》**`chapters/` 真的 0 個、`arcs/arc_1-3.md` 有 1 個**
        //   （日期 2026-09-09，比那張單晚三天）⇒ 只做章的容器會**靜默丟掉她唯一的內容**。
        // ===========================================================
        public static string WorkChaptersRoot(string workId)
            => Path.Combine(WorkRoot(workId), k_WorkChaptersDirName);
        public static string WorkArcsRoot(string workId)
            => Path.Combine(WorkRoot(workId), k_WorkArcsDirName);

        /// <summary>
        /// 正文容器的**存在性讀數** —— ⚠ 回三態而不是回檔數（TASK-0146 ② 的反向對照）。
        /// 🩸 現行讀取端數章數用的是「目錄不存在就回 0」，
        ///   ⇒ **容器放錯位置 ＝ 靜默 0**，而 ④ 指定的第一本正好真的是 0 章
        ///   ⇒ 兩者逐位元組同形，**搬壞了會長得像搬對了**。
        ///   （@gura 開單時那句「內容為空反而讓『搬壞了』更難被看見」的機制版本。）
        /// ⇒ 所以這裡把「沒有這個目錄」與「有目錄但裡面 0 個」做成**兩個不同的值**，
        ///   ⛔ 不留一個 0 讓讀的人去猜是哪一種 —— 人往空格裡填的一定是成功。
        /// </summary>
        public enum WorkProseState { NoDir, EmptyDir, HasFiles }

        public static WorkProseState ProbeWorkProse(string dir, out int fileCount)
        {
            fileCount = 0;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return WorkProseState.NoDir;
            try { fileCount = Directory.GetFiles(dir).Length; }
            catch (System.Exception) { return WorkProseState.NoDir; }
            return fileCount > 0 ? WorkProseState.HasFiles : WorkProseState.EmptyDir;
        }

        // ===========================================================
        // 區塊職責：寫書線四欄的寫入與讀回 —— TASK-0146 ①
        // ⚠ 刻意**不動 `MediaInit` 的簽名**：那是閱讀線的建檔路徑，
        //   而四欄只屬於 authored。混進去會讓每一本閱讀作品都多帶四個空欄
        //   ⇒ 違反 ① 明文的「既有欄位與讀取端一個字不動」。
        // 數值影響：**空值不落盤** ⇒ 沒帶寫書線的 work.json 逐位元組不變。
        // ===========================================================
        public sealed class WorkAuthored
        {
            public string AuthorPersona = "";
            public string Status = "";
            public string PublishStatus = "";
            public string Origin = "";
            /// <summary>四欄全空 ＝ 這份 work.json 上沒有寫書線（⛔ 不等於「它不是 authored」，只是這裡沒寫）。</summary>
            public bool IsEmpty => AuthorPersona.Length == 0 && Status.Length == 0
                                   && PublishStatus.Length == 0 && Origin.Length == 0;
            /// <summary>對寫書線讀取端可見的唯一條件 —— `origin` 必須逐字是 `authored`（見 Key_Origin 的區塊註解）。</summary>
            public bool VisibleToWritingLine => Origin == OriginAuthored;
        }

        /// <summary>把寫書線四欄寫上既有的 work.json（**只寫非空的那幾欄**）。work.json 不存在 ⇒ 不建、回 false。</summary>
        public static bool TrySetWorkAuthored(string workId, WorkAuthored fields, out string error)
        {
            error = null;
            if (fields == null) { error = "fields 是 null"; return false; }
            string path = Path.Combine(WorkRoot(workId), k_WorkJsonName);
            if (!File.Exists(path))
            {
                // ⛔ 刻意不順手建一份：這裡若建檔，「作品本來就在」與「我剛剛替它生了一份」會同形。
                error = $"work.json 不存在：`{path}` —— ⛔ 本函式不建檔（那是 media_init 的職責）";
                return false;
            }
            JsonData work = LoadJson(path, out string loadErr);
            if (work == null) { error = loadErr; return false; }
            if (fields.AuthorPersona.Length > 0) work[Key_AuthorPersona] = fields.AuthorPersona;
            if (fields.Status.Length > 0) work[Key_Status] = fields.Status;
            if (fields.PublishStatus.Length > 0) work[Key_PublishStatus] = fields.PublishStatus;
            if (fields.Origin.Length > 0) work[Key_Origin] = fields.Origin;
            SaveJson(path, work);
            return true;
        }

        // ===========================================================
        // 區塊職責：建 work.json（不存在才建；已存在一律不覆寫）—— `MediaInit` 與 ④ 搬遷**共用這一份**
        // 物理意義：兩條路都會需要「這部作品在新 store 有一份 work.json」，而它們建出來的
        //          schema 必須逐欄相同 —— 各寫一份的話，兩種來源的 work.json 會慢慢長歪，
        //          🩸 而那個漂移**不會報錯**：讀取端 `GetString(key, "")` 對缺欄回空字串，
        //          於是「這本沒填」與「這條路徑沒寫這欄」同形。
        // 數值影響：檔案不存在 ⇒ 建一份；存在 ⇒ 一個位元組都不動（⛔ 不補欄、不升版）。
        // ===========================================================
        static string EnsureWorkJson(string workId, string title, string titleOriginal, string author,
                                     IList<string> aliases, IList<string> genreTags)
        {
            string workPath = Path.Combine(WorkRoot(workId), k_WorkJsonName);
            if (File.Exists(workPath)) return $"- work.json 已存在，不覆寫：`{workId}`\n";

            var work = new JsonData();
            work[Key_WorkId] = workId;
            work[Key_Title] = title;
            work[Key_TitleOriginal] = titleOriginal ?? "";
            work[Key_Author] = author ?? "";
            // 區塊職責：aliases 是**日後搜尋的唯一入口**（中／日／英 + 常見異譯）。
            // 物理意義：搜尋比對打的是 title / title_original / aliases 三欄；
            //          漏建 alias 的後果不是「找不到」，是「找不到 → 有人再建一本」
            //          （arakawa 雙 entry 的成因，2026-08-05 實測 101 本裡有四組重複）。
            // 數值影響：純 metadata；不影響進度與章節。
            work[Key_Aliases] = ToStringArray(aliases, title, titleOriginal);
            work[Key_GenreTags] = ToStringArray(genreTags);
            work[Key_SchemaVersion] = 1;
            SaveJson(workPath, work);
            return $"- ✅ 建立 work.json：`{workId}`《{title}》（aliases {work[Key_Aliases].Count} 筆）\n";
        }

        /// <summary>讀回寫書線四欄（③ 逐欄對拍的讀取側）。work.json 不存在或解析不動 ⇒ 回 false，⛔ 不回一個空物件假裝讀到了。</summary>
        public static bool TryReadWorkAuthored(string workId, out WorkAuthored fields, out string error)
        {
            fields = null; error = null;
            string path = Path.Combine(WorkRoot(workId), k_WorkJsonName);
            if (!File.Exists(path)) { error = $"work.json 不存在：`{path}`"; return false; }
            JsonData work = LoadJson(path, out string loadErr);
            if (work == null) { error = loadErr; return false; }
            fields = new WorkAuthored
            {
                AuthorPersona = work.GetString(Key_AuthorPersona, ""),
                Status = work.GetString(Key_Status, ""),
                PublishStatus = work.GetString(Key_PublishStatus, ""),
                Origin = work.GetString(Key_Origin, ""),
            };
            return true;
        }

        // ===========================================================
        // 區塊職責：③ 逐欄對拍 —— 舊 store（`BookNotes/<slug>/book.json`）↔ 新 store（`works/<work_id>/work.json`）
        // 物理意義：搬遷前後的**唯一判準讀取器**。回傳的不是 bool，是「對不上的欄位名」。
        // 數值影響：**純讀**。不建目錄、不寫任何檔（搬遷本身是 ④ 的事，不是對拍的事）。
        // 設計取捨：
        //   - 為什麼要 `mismatchedFields` 而不是 `bool ok`：① 條文明文「⛔ 不是『有這個 key 就算』」，
        //     而 ③ 明文「印出對不上的欄位名；⛔ 不是看 exit 0」。
        //     🩸 一個永遠回「全對」的對拍與一個真的全對的對拍，在 exit code 上逐位元組同形。
        //   - ⭐ 為什麼「四欄都空」不算全對（這一格是本函式最重要的判準）：
        //     新 store 的 work.json 沒有那四欄時，逐欄比較會拿**兩個空字串**去比，
        //     ⇒ 「還沒搬」會回報「全對」。那正是 @gura 開單時說的那個形狀，只是換到欄位這一維。
        //     ⇒ 所以「新 store 上沒有寫書線」是**一個獨立的回傳值**，⛔ 不准掉進 AllMatch。
        //   - 為什麼 `book` 與 `work_id` 都必填、不從對方推導：兩個 store 的 id 慣例不同
        //     （@gura 舊 store 是 `book-gura-abyssal-verifications`，而新 store 那本還不存在）。
        //     推導會讓「id 對不上」與「這本沒搬」同形。
        // ===========================================================
        public enum AuthoredDiffOutcome
        {
            /// <summary>舊 store 那本 `book.json` 不存在 ⇒ 沒有比較的左邊（⛔ 不是「值不同」）。</summary>
            OldStoreMissing,
            /// <summary>新 store 那份 `work.json` 不存在 ⇒ 還沒建（⛔ 不是「值不同」）。</summary>
            NewStoreMissing,
            /// <summary>任一邊解析不動 ⇒ ⛔ 不回報「不同」，那會把壞檔講成搬壞了。</summary>
            ParseFailed,
            /// <summary>兩邊都沒有寫書線四欄 ⇒ 這本不是 authored，⛔ 對拍沒有通過，只是無事可拍。</summary>
            NeitherHasWritingLine,
            /// <summary>舊 store 有寫書線而新 store 四欄全空 ⇒ **還沒搬**（⛔ 這格若掉進 AllMatch 就是綠得最假的一格）。</summary>
            NewStoreNoWritingLine,
            /// <summary>有欄位對不上 ⇒ `mismatchedFields` 逐欄指名。</summary>
            Mismatch,
            /// <summary>
            /// 四欄逐欄相同，**而正文容器對不上**（舊 store 有檔、新 store 沒有）⇒ 搬下去會靜默丟掉正文。
            /// 🩸 這個值是 2026-09-10 自己吃自己的狗糧吃出來的：第一版讓這種情況回 `AllMatch`，
            ///   於是回傳檔標題印「✓ 四欄逐欄相同」而底下那張表同時印「⛔ 舊 store 有 2 個檔而新 store 沒有」。
            ///   ⇒ 掃標題的人讀到「搬對了」，而真搬下去會丟掉兩章正文。
            ///   ⛔ 修法不是把標題措辭寫好一點 —— 是讓「欄位全對」不再能獨自產生一個 ✓。
            /// </summary>
            FieldsMatchProseMissing,
            /// <summary>四欄逐欄相同、新 store 真的有寫書線，**且正文容器沒有遺漏**。</summary>
            AllMatch,
        }

        /// <summary>舊 store 的草稿檔位置（`origin`／`author_persona`／`status`／`publish_status` 的事實源）。</summary>
        public static string OldStoreBookJsonPath(string bookSlug)
            => Books.UCL_BooksIO.BookNotesJsonPath(bookSlug);

        /// <summary>③ 逐欄對拍。⛔ 純讀；`report` 是給人看的逐欄表，`mismatchedFields` 是給程式判的欄位名。</summary>
        public static AuthoredDiffOutcome DiffWorkAuthored(string bookSlug, string workId,
            out List<string> mismatchedFields, out string report)
        {
            mismatchedFields = new List<string>();
            var sb = new StringBuilder();

            string oldPath = OldStoreBookJsonPath(bookSlug);
            string newPath = Path.Combine(WorkRoot(workId), k_WorkJsonName);
            sb.AppendLine($"- 舊 store：`{oldPath}`");
            sb.AppendLine($"- 新 store：`{newPath}`");
            sb.AppendLine();

            if (!File.Exists(oldPath))
            {
                sb.AppendLine("⛔ **舊 store 那本不存在** ⇒ 沒有比較的左邊。" +
                              "⛔ 這不是「值不同」，是 `book` 給錯或那本不在舊 store。");
                report = sb.ToString();
                return AuthoredDiffOutcome.OldStoreMissing;
            }
            if (!File.Exists(newPath))
            {
                sb.AppendLine("⛔ **新 store 還沒有這份 work.json** ⇒ 這本還沒建（④ 的前置）。⛔ 這不是「值不同」。");
                report = sb.ToString();
                return AuthoredDiffOutcome.NewStoreMissing;
            }

            JsonData oldData = LoadJson(oldPath, out string oldErr);
            if (oldData == null)
            {
                sb.AppendLine($"⛔ 舊 store 解析失敗：{oldErr}　⇒ ⛔ 不回報「不同」—— 壞檔與搬壞了是兩件事。");
                report = sb.ToString();
                return AuthoredDiffOutcome.ParseFailed;
            }
            if (!TryReadWorkAuthored(workId, out WorkAuthored newFields, out string newErr))
            {
                sb.AppendLine($"⛔ 新 store 讀取失敗：{newErr}　⇒ ⛔ 不回報「不同」。");
                report = sb.ToString();
                return AuthoredDiffOutcome.ParseFailed;
            }

            var oldFields = new WorkAuthored
            {
                AuthorPersona = oldData.GetString(Key_AuthorPersona, ""),
                Status = oldData.GetString(Key_Status, ""),
                PublishStatus = oldData.GetString(Key_PublishStatus, ""),
                Origin = oldData.GetString(Key_Origin, ""),
            };

            // 逐欄表 —— 順序固定，`origin` 放第一列：它是寫書線可見性的承重欄（見 Key_Origin 的區塊註解）。
            var keys = new[] { Key_Origin, Key_AuthorPersona, Key_Status, Key_PublishStatus };
            var oldVals = new[] { oldFields.Origin, oldFields.AuthorPersona, oldFields.Status, oldFields.PublishStatus };
            var newVals = new[] { newFields.Origin, newFields.AuthorPersona, newFields.Status, newFields.PublishStatus };

            sb.AppendLine("| 欄位 | 舊 store | 新 store | 判定 |");
            sb.AppendLine("|---|---|---|---|");
            for (int i = 0; i < keys.Length; i++)
            {
                bool same = oldVals[i] == newVals[i];
                if (!same) mismatchedFields.Add(keys[i]);
                sb.AppendLine($"| `{keys[i]}` | {ShowFieldValue(oldVals[i])} | {ShowFieldValue(newVals[i])} | " +
                              (same ? "✓ 相同" : "**✗ 對不上**") + " |");
            }
            sb.AppendLine();
            sb.Append(ProseSection(bookSlug, workId, out bool proseMissing));

            // ⭐ 空對空不准算全對 —— 見本區塊註解第三條。
            if (oldFields.IsEmpty && newFields.IsEmpty)
            {
                sb.AppendLine();
                sb.AppendLine("⚠ **兩邊都沒有寫書線四欄** ⇒ 這本不是 authored。⛔ 這不是「對拍通過」，是無事可拍。");
                report = sb.ToString();
                return AuthoredDiffOutcome.NeitherHasWritingLine;
            }
            if (newFields.IsEmpty)
            {
                sb.AppendLine();
                sb.AppendLine("⛔ **新 store 那份 work.json 上四欄全空 ⇒ 這本還沒搬。**");
                sb.AppendLine("　⚠ 上表把四個空字串逐欄比出「對不上」是對的讀數，但**處置不同**：" +
                              "這裡要跑的是 ④ 搬遷（而 ④ 的前置是本人的搬遷確認），不是去修欄位值。");
                report = sb.ToString();
                return AuthoredDiffOutcome.NewStoreNoWritingLine;
            }
            if (mismatchedFields.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"⛔ **對不上的欄位（{mismatchedFields.Count} 欄）：** " +
                              string.Join("、", mismatchedFields.ConvertAll(k => "`" + k + "`")));
                report = sb.ToString();
                return AuthoredDiffOutcome.Mismatch;
            }

            sb.AppendLine();
            sb.AppendLine("✓ **四欄逐欄相同**，且新 store 真的有寫書線" +
                          (newFields.VisibleToWritingLine
                              ? "（`origin=authored` ⇒ 對寫書線讀取端可見）。"
                              : "　⚠ 但 `origin` 不是 `authored` ⇒ **對寫書線讀取端仍然不可見**。"));

            // ⭐ 欄位全對**不足以**產生一個 ✓ —— 正文容器對不上時，搬下去會靜默丟掉正文。
            //   見 FieldsMatchProseMissing 的註解（那是本函式自己咬到自己的那一格）。
            if (proseMissing)
            {
                sb.AppendLine();
                sb.AppendLine("⛔ **但正文容器對不上**（上表已逐列指出）⇒ **這一趟還不能搬**：" +
                              "欄位對得起來只證明 metadata，正文是另一本帳。");
                report = sb.ToString();
                return AuthoredDiffOutcome.FieldsMatchProseMissing;
            }
            report = sb.ToString();
            return AuthoredDiffOutcome.AllMatch;
        }

        /// <summary>空字串要看得出是空的 —— ⛔ 不印成空白格（空白格與「我沒讀那一欄」同形）。</summary>
        static string ShowFieldValue(string v) => string.IsNullOrEmpty(v) ? "_(空)_" : "`" + v + "`";

        /// <summary>
        /// 正文容器對拍 —— ⭐ 這是 `ProbeWorkProse` 三態的**第一個讀取端**（在此之前它存在而未生效）。
        /// 🩸 @gura《深海對拍錄》舊 store 的 `chapters/` 是**目錄存在、0 個檔**，而 `arcs/` 有 1 個
        ///   ⇒ 「還沒寫」與「搬掉了」在數章數那條路上同形，所以這裡印的是三態的名字，不是檔數。
        /// ⚠ 舊 store 的相對版面與新 store 相同（`&lt;book&gt;/chapters`、`&lt;book&gt;/arcs`），
        ///   所以這裡沿用同兩個目錄名常數 —— ⛔ 不另立一套舊版面的名字。
        /// </summary>
        static string ProseSection(string bookSlug, string workId, out bool anyMissing)
        {
            string oldRoot = Path.Combine(BookNotesRoot, bookSlug);
            var sb = new StringBuilder();
            sb.AppendLine("| 正文容器 | 舊 store | 新 store | 判定 |");
            sb.AppendLine("|---|---|---|---|");
            sb.Append(ProseRow(k_WorkChaptersDirName,
                Path.Combine(oldRoot, k_WorkChaptersDirName), WorkChaptersRoot(workId), out bool missA));
            sb.Append(ProseRow(k_WorkArcsDirName,
                Path.Combine(oldRoot, k_WorkArcsDirName), WorkArcsRoot(workId), out bool missB));
            anyMissing = missA || missB;
            return sb.ToString();
        }

        // ===========================================================
        // 區塊職責：④ 把一本 authored 書從舊 store 搬進新 store（四欄 ＋ 正文容器）
        // 物理意義：**複製，不是移動** —— 舊 store 那份原地保留。
        //   🩸 理由是 @gura 在 TASK-0146 留言 #3 寫下的驗收條件：搬完之後 `arcs/` 要是
        //   `HasFiles(1) ↔ HasFiles(1)`。移動的話舊側會變 `EmptyDir`，而**那個讀數與「搬壞了」同形**
        //   —— 兩邊都不再是她交出來的那個形狀。舊 store 何時退場是 TASK-0143 的事，⛔ 不在本刀。
        // 數值影響：`confirm=false` ⇒ **零寫入**（只回計畫）。`confirm=true` ⇒ 可能建 work.json、
        //   補四欄、複製 .md；⛔ 一律不覆寫既有檔（同名檔跳過並出聲）。
        // ⛔ 為什麼不順手拆 `status` 的兩條軸（舊 store：草稿 `writing`／發表後 `reading`）：
        //   ① 的條文是「逐欄相同」，拆軸會讓對拍**必然紅**，而那格紅的原因不是搬壞了。
        //   ⇒ 拆軸是另一個決定，要另一張單；這裡照搬（TASK-0146 留言 #5 的甲案）。
        // ⚠ 只搬 `origin=authored` 的書：閱讀線的書搬過來會多出四個空欄，而 ① 明文
        //   「既有欄位與讀取端一個字不動」。⇒ 不是 authored 就出聲拒絕，⛔ 不靜默跳過。
        // ===========================================================
        public enum AuthoredMigrateOutcome
        {
            /// <summary>舊 store 沒有這本 ⇒ 沒有可搬的來源（⛔ 不是「搬完了」）。</summary>
            OldStoreMissing,
            /// <summary>舊 store 的 book.json 解析不動 ⇒ ⛔ 不當成「沒有內容」。</summary>
            ParseFailed,
            /// <summary>舊 store 那本 `origin` 不是 `authored` ⇒ 這支不搬它。</summary>
            NotAuthored,
            /// <summary>沒給 confirm ⇒ 只回計畫，**一個位元組都沒寫**。</summary>
            Planned,
            /// <summary>真的搬了（報告裡附回讀對拍的結果）。</summary>
            Migrated,
        }

        public static AuthoredMigrateOutcome MigrateAuthoredWork(string bookSlug, string workId,
            bool confirm, out string report, out string error)
        {
            error = null;
            var sb = new StringBuilder();
            string oldRoot = Path.Combine(BookNotesRoot, bookSlug);
            string oldPath = OldStoreBookJsonPath(bookSlug);
            sb.AppendLine($"- 舊 store：`{oldPath}`");
            sb.AppendLine($"- 新 store：`{Path.Combine(WorkRoot(workId), k_WorkJsonName)}`");
            sb.AppendLine($"- 模式：{(confirm ? "**confirm ⇒ 真的寫**" : "**dry-run ⇒ 零寫入**（要寫就加 `confirm=1`）")}");
            sb.AppendLine();

            if (!File.Exists(oldPath))
            {
                error = $"舊 store 沒有這本：`{oldPath}`";
                report = sb.ToString();
                return AuthoredMigrateOutcome.OldStoreMissing;
            }
            JsonData oldData = LoadJson(oldPath, out string loadErr);
            if (oldData == null)
            {
                error = $"舊 store 解析失敗：{loadErr} —— ⛔ 這不是「沒有內容」";
                report = sb.ToString();
                return AuthoredMigrateOutcome.ParseFailed;
            }

            var fields = new WorkAuthored
            {
                AuthorPersona = oldData.GetString(Key_AuthorPersona, ""),
                Status = oldData.GetString(Key_Status, ""),
                PublishStatus = oldData.GetString(Key_PublishStatus, ""),
                Origin = oldData.GetString(Key_Origin, ""),
            };
            if (!fields.VisibleToWritingLine)
            {
                error = $"舊 store 那本的 `{Key_Origin}` 是 `{fields.Origin}`，不是 `{OriginAuthored}` ⇒ " +
                        "這支只搬寫書線的書。⛔ 不靜默跳過，也不替它補上 origin。";
                report = sb.ToString();
                return AuthoredMigrateOutcome.NotAuthored;
            }

            sb.AppendLine("## 要搬的四欄（照搬，⛔ 不轉換）");
            sb.AppendLine();
            sb.AppendLine("| 欄 | 值 |");
            sb.AppendLine("|---|---|");
            sb.AppendLine($"| `{Key_Origin}` | `{fields.Origin}` |");
            sb.AppendLine($"| `{Key_AuthorPersona}` | `{fields.AuthorPersona}` |");
            sb.AppendLine($"| `{Key_Status}` | `{fields.Status}` |");
            sb.AppendLine($"| `{Key_PublishStatus}` | `{fields.PublishStatus}` |");
            sb.AppendLine();

            sb.AppendLine("## 正文容器（三態，⛔ 不是檔數）");
            sb.AppendLine();
            sb.AppendLine(ProseSection(bookSlug, workId, out _));

            if (!confirm)
            {
                sb.AppendLine("⇒ **dry-run 到此為止** —— 上面每一格都是讀出來的，沒有任何寫入。");
                report = sb.ToString();
                return AuthoredMigrateOutcome.Planned;
            }

            // --- 這行以下才會動磁碟 ---
            sb.AppendLine("## 寫入");
            sb.AppendLine();
            var aliases = new List<string>();
            JsonData oldAliases = oldData.Contains(Key_Aliases) ? oldData[Key_Aliases] : null;
            if (oldAliases != null && oldAliases.IsArray)
                for (int i = 0; i < oldAliases.Count; i++)
                {
                    string a = AliasToString(oldAliases[i]);
                    if (!string.IsNullOrEmpty(a) && !aliases.Contains(a)) aliases.Add(a);
                }
            sb.Append(EnsureWorkJson(workId,
                oldData.GetString(Key_Title, bookSlug),
                oldData.GetString(Key_TitleOriginal, ""),
                oldData.GetString(Key_Author, ""),
                aliases, null));

            if (!TrySetWorkAuthored(workId, fields, out string setErr))
            {
                error = $"四欄寫入失敗：{setErr}";
                report = sb.ToString();
                return AuthoredMigrateOutcome.ParseFailed;
            }
            sb.AppendLine("- ✅ 四欄已寫上 work.json（空值不落盤）");

            sb.Append(CopyProseDir(Path.Combine(oldRoot, k_WorkChaptersDirName), WorkChaptersRoot(workId),
                                   k_WorkChaptersDirName));
            sb.Append(CopyProseDir(Path.Combine(oldRoot, k_WorkArcsDirName), WorkArcsRoot(workId),
                                   k_WorkArcsDirName));

            // 回讀：⛔ 不印「寫入成功」當收據 —— 用同一支對拍器（③）重讀一次落地結果。
            sb.AppendLine();
            sb.AppendLine("## 回讀對拍（走 ③ 那支 `DiffWorkAuthored`，⛔ 不是本函式自己說了算）");
            sb.AppendLine();
            AuthoredDiffOutcome after = DiffWorkAuthored(bookSlug, workId, out List<string> mism, out string diffReport);
            sb.AppendLine($"**{after}**" + (mism.Count > 0 ? $"　對不上：{string.Join("、", mism)}" : ""));
            sb.AppendLine();
            sb.Append(diffReport);

            report = sb.ToString();
            return AuthoredMigrateOutcome.Migrated;
        }

        /// <summary>
        /// 複製一個正文容器（⛔ 不移動、⛔ 不覆寫既有同名檔）。
        /// <para>⚠ 來源目錄不存在 ⇒ 回一行「NoDir，無事可搬」而**不建空目錄** ——
        /// 建了的話新側會從 `NoDir` 變成 `EmptyDir`，而那正是 ② 要分開的兩個值。</para>
        /// </summary>
        static string CopyProseDir(string srcDir, string dstDir, string label)
        {
            WorkProseState src = ProbeWorkProse(srcDir, out int srcCount);
            if (src == WorkProseState.NoDir) return $"- `{label}/`：舊 store `NoDir` ⇒ 無事可搬（⛔ 不建空目錄）\n";
            if (src == WorkProseState.EmptyDir) return $"- `{label}/`：舊 store `EmptyDir`（0 檔）⇒ 無事可搬\n";

            Directory.CreateDirectory(dstDir);
            int copied = 0, skipped = 0;
            foreach (string f in Directory.GetFiles(srcDir))
            {
                string dst = Path.Combine(dstDir, Path.GetFileName(f));
                if (File.Exists(dst)) { skipped++; continue; }
                File.Copy(f, dst);
                copied++;
            }
            string line = $"- `{label}/`：來源 {srcCount} 檔 ⇒ 複製 {copied}";
            if (skipped > 0) line += $"、**跳過 {skipped}（目標已有同名檔，⛔ 不覆寫）**";
            return line + "\n";
        }

        /// <param name="missing">
        /// 只在「**舊 store 有檔而新 store 沒有**」時為 true —— 那是唯一會靜默丟內容的那一種。
        /// ⛔ 舊 store 是空目錄或沒有容器時**不算遺漏**（那兩種搬過去沒有東西會不見）。
        /// </param>
        static string ProseRow(string name, string oldDir, string newDir, out bool missing)
        {
            WorkProseState o = ProbeWorkProse(oldDir, out int oldCount);
            WorkProseState n = ProbeWorkProse(newDir, out int newCount);
            missing = o == WorkProseState.HasFiles && n != WorkProseState.HasFiles;
            string verdict;
            if (missing)
                verdict = $"⛔ **舊 store 有 {oldCount} 個檔而新 store 沒有** ⇒ 搬過去會靜默丟掉它";
            else if (o == WorkProseState.HasFiles)
                verdict = oldCount == newCount ? $"✓ 兩邊都 {oldCount} 個檔" : $"⚠ 檔數不同（{oldCount} → {newCount}）";
            else if (o == WorkProseState.EmptyDir)
                verdict = "⚠ 舊 store 是**空目錄** ⇒ ⛔ 不是「沒有這個容器」，也不是「有內容」";
            else
                verdict = "· 舊 store 沒有這個容器 ⇒ 無事可搬";
            return $"| `{name}/` | {ShowProseState(o, oldCount)} | {ShowProseState(n, newCount)} | {verdict} |\n";
        }

        static string ShowProseState(WorkProseState state, int count)
        {
            switch (state)
            {
                case WorkProseState.NoDir: return "`NoDir`（沒有這個目錄）";
                case WorkProseState.EmptyDir: return "`EmptyDir`（目錄在、0 個檔）";
                default: return $"`HasFiles`（{count} 個檔）";
            }
        }

        public static string ReaderRoot(string mediaId, string persona)
            => Path.Combine(MediaRoot(mediaId), k_ReadersDirName, persona);
        public static string ReaderJsonPath(string mediaId, string persona)
            => Path.Combine(ReaderRoot(mediaId, persona), k_ReaderJsonName);
        public static string ChapterDir(string mediaId, string persona, string chapterId)
            => Path.Combine(ReaderRoot(mediaId, persona), k_ChaptersDirName, chapterId);

        public static bool IsValidId(string value) => !string.IsNullOrEmpty(value) && k_IdPattern.IsMatch(value);
        public static bool IsValidChapterId(string value)
            => !string.IsNullOrEmpty(value) && k_ChapterIdPattern.IsMatch(value);

        /// <summary>列出目前所有 media id（供頁面下拉與 Cmd 驗證用）。</summary>
        public static List<string> ListMediaIds()
        {
            var result = new List<string>();
            string root = Path.Combine(LibraryRoot, k_MediaDirName);
            if (!Directory.Exists(root)) return result;
            foreach (string dir in Directory.GetDirectories(root)) result.Add(Path.GetFileName(dir));
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        /// <summary>
        /// 區塊職責：全 Library 的 media 總表（瀏覽下拉與 scan 共用）。
        /// 物理意義：一筆 = media.json + work.json title + readers 目錄名 —— 只讀 metadata，
        ///          不碰章節正文；title 缺檔退回 mediaId（瀏覽不因缺料斷掉，缺料是 scan 的事）。
        /// </summary>
        public class MediaEntry
        {
            public string MediaId = "";
            public string MediaKind = "";
            public string WorkId = "";
            public string Title = "";
            /// <summary>作品層的搜尋用名稱（title_original ＋ aliases）——
            /// ⚠ 這一欄住在 <c>works/&lt;work&gt;/work.json</c>，不在 media.json；
            /// 查詢端只比 MediaId/WorkId/Title 的話，簡體、原文名、俗名一律 0 筆，
            /// 而 0 筆的樣子跟「這部作品不存在」一模一樣。</summary>
            public List<string> Aliases = new List<string>();
            public List<string> Readers = new List<string>();
        }

        public static List<MediaEntry> ListMediaEntries()
        {
            var result = new List<MediaEntry>();
            string root = Path.Combine(LibraryRoot, k_MediaDirName);
            if (!Directory.Exists(root)) return result;
            foreach (string dir in Directory.GetDirectories(root))
            {
                var e = new MediaEntry { MediaId = Path.GetFileName(dir) };
                JsonData media = LoadJson(Path.Combine(dir, k_MediaJsonName), out _);
                if (media != null)
                {
                    e.MediaKind = media.GetString(Key_MediaKind, "");
                    e.WorkId = media.GetString(Key_WorkId, "");
                }
                e.Title = e.MediaId;
                if (!string.IsNullOrEmpty(e.WorkId))
                {
                    JsonData work = LoadJson(Path.Combine(WorkRoot(e.WorkId), k_WorkJsonName), out _);
                    if (work != null)
                    {
                        e.Title = work.GetString(Key_Title, e.MediaId);
                        // 別名兩形狀（字串陣列／物件陣列）由 AliasToString 吸收 —— 見該函式的血證。
                        string original = work.GetString(Key_TitleOriginal, "");
                        if (!string.IsNullOrEmpty(original)) e.Aliases.Add(original);
                        JsonData aliases = work.Contains(Key_Aliases) ? work[Key_Aliases] : null;
                        if (aliases != null && aliases.IsArray)
                            for (int i = 0; i < aliases.Count; i++)
                            {
                                string a = AliasToString(aliases[i]);
                                if (!string.IsNullOrEmpty(a) && !e.Aliases.Contains(a)) e.Aliases.Add(a);
                            }
                    }
                }
                string readersRoot = Path.Combine(dir, k_ReadersDirName);
                if (Directory.Exists(readersRoot))
                {
                    foreach (string readerDir in Directory.GetDirectories(readersRoot))
                        e.Readers.Add(Path.GetFileName(readerDir));
                    e.Readers.Sort(StringComparer.OrdinalIgnoreCase);
                }
                result.Add(e);
            }
            result.Sort((a, b) => string.Compare(a.MediaId, b.MediaId, StringComparison.Ordinal));
            return result;
        }

        /// <summary>列出某 media 底下的 reader persona（同一部作品可有多位讀者各自一份紀錄）。</summary>
        public static List<string> ListReaders(string mediaId)
        {
            var result = new List<string>();
            string root = Path.Combine(MediaRoot(mediaId), k_ReadersDirName);
            if (!Directory.Exists(root)) return result;
            foreach (string dir in Directory.GetDirectories(root)) result.Add(Path.GetFileName(dir));
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        // ===========================================================
        // 外部漫畫庫 (External Comics)
        // 物理意義：本機外部實體漫畫資料夾（例如 D:\commic、D:\manga）的設定與探索。
        //          真相源是 UCL_ProjectEditorPrefs（不上 git、per-project 隔離），
        //          同步輸出 .comic_root.local 快照檔給 Python 唯讀消費（write-on-change，Python 不刪檔自癒）。
        // ===========================================================
        public const string PrefKey_ComicRootPath = "UCL_Library.ComicRootPath";
        public const string ComicRootSnapshotFileName = ".comic_root.local";

        public enum ComicMatchStatus
        {
            Synced,         // 🟢 已在 Library 建檔且本機實體資料夾存在
            MissingSource,  // 🟡 已在 Library 建檔但本機實體資料夾失聯
            Unregistered,   // ⚪ 本機實體資料夾存在但尚未在 Library 建檔
        }

        public class ExternalComicSeries
        {
            public string SeriesName = "";       // e.g. "Hunter x Hunter"
            public string Slug = "";             // e.g. "hunter-x-hunter"
            public string MediaId = "";          // e.g. "comic-hunter-x-hunter"
            public List<ExternalComicVolume> Volumes = new List<ExternalComicVolume>();
            public int TotalChapters = 0;
            public int TotalPages = 0;
            public ComicMatchStatus Status = ComicMatchStatus.Unregistered;
            public bool HasWorkJson = false;
            public bool HasMediaJson = false;
            public string RegisteredTitle = "";
        }

        public class ExternalComicVolume
        {
            public string FolderName = "";       // e.g. "Hunter x Hunter 01"
            public string FolderPath = "";       // e.g. "D:\commic\Hunter x Hunter 01"
            public string VolumeLabel = "";      // e.g. "01"
            public List<string> Chapters = new List<string>(); // e.g. "0001", "0002"...
            public int PageCount = 0;
        }

        /// <summary>取得本機外部漫畫庫根目錄路徑（預設空字串）。</summary>
        public static string GetComicRoot()
        {
            return UCL_ProjectEditorPrefs.GetString(PrefKey_ComicRootPath, "");
        }

        /// <summary>
        /// 區塊職責：設定外部漫畫庫路徑。
        /// 物理意義：寫入 UCL_ProjectEditorPrefs 並覆寫 .comic_root.local 快照（write-on-change）。
        /// </summary>
        public static void SetComicRoot(string iPath)
        {
            string cleanPath = string.IsNullOrWhiteSpace(iPath) ? "" : iPath.Trim();
            UCL_ProjectEditorPrefs.SetString(PrefKey_ComicRootPath, cleanPath);
            WriteComicRootSnapshot(cleanPath);
        }

        /// <summary>
        /// 區塊職責：落盤 .comic_root.local 本機快照檔（給 Python 唯讀消費）。
        /// 物理意義：放在 UnityProjectRoot 與 UCL_Core 根目錄；gitignored。
        /// </summary>
        public static void WriteComicRootSnapshot(string iPath)
        {
            try
            {
                var targetDirs = new List<string>();
                string repoRoot = UCL_RepoPath.UnityProjectRoot;
                if (!string.IsNullOrEmpty(repoRoot) && Directory.Exists(repoRoot)) targetDirs.Add(repoRoot);
                string corePath = UCL_EditorPath.CorePath;
                if (!string.IsNullOrEmpty(corePath))
                {
                    string coreFullPath = Path.GetFullPath(Path.Combine(repoRoot, corePath));
                    if (Directory.Exists(coreFullPath) && !targetDirs.Contains(coreFullPath)) targetDirs.Add(coreFullPath);
                }

                foreach (string dir in targetDirs)
                {
                    string filePath = Path.Combine(dir, ComicRootSnapshotFileName);
                    if (string.IsNullOrEmpty(iPath))
                    {
                        if (File.Exists(filePath)) File.Delete(filePath);
                    }
                    else
                    {
                        string content = $"# Generated by UCL_ReadingLibraryIO - DO NOT COMMIT\ncomic_root={iPath}\n";
                        File.WriteAllText(filePath, content, new UTF8Encoding(false));
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ReadingLibrary] WriteComicRootSnapshot failed: {ex.Message}");
            }
        }

        static readonly Regex s_VolumeRegex = new Regex(
            @"^(.*?)[ _\.\-]+(?:[vV]ol\.?|[vV]olume|第)?\s*(\d{1,4})(?:[卷冊話期])?$",
            RegexOptions.Compiled);

        static readonly HashSet<string> s_ImageExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif"
        };

        /// <summary>解析資料夾名稱為作品系列名與卷數（例 "Hunter x Hunter 01" -> "Hunter x Hunter", "01"）。</summary>
        public static void ParseSeriesAndVolume(string folderName, out string seriesName, out string volumeLabel)
        {
            if (string.IsNullOrWhiteSpace(folderName))
            {
                seriesName = "";
                volumeLabel = "01";
                return;
            }

            folderName = folderName.Trim();
            var match = s_VolumeRegex.Match(folderName);
            if (match.Success && match.Groups.Count >= 3)
            {
                seriesName = match.Groups[1].Value.Trim();
                volumeLabel = match.Groups[2].Value.Trim();
                if (string.IsNullOrEmpty(seriesName)) seriesName = folderName;
            }
            else
            {
                seriesName = folderName;
                volumeLabel = "01";
            }
        }

        /// <summary>將作品系列名轉換為標準 slug（例 "Hunter x Hunter" -> "hunter-x-hunter"）。</summary>
        public static string NormalizeSeriesSlug(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            var sb = new StringBuilder();
            foreach (char c in raw.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c)) sb.Append(c);
                else if (c == ' ' || c == '_' || c == '-') sb.Append('-');
            }
            string s = sb.ToString();
            // 收斂多個連續 '-'
            while (s.Contains("--")) s = s.Replace("--", "-");
            return s.Trim('-');
        }

        /// <summary>
        /// 區塊職責：掃描外部漫畫庫目錄，將所有漫畫作品聚合為系列清單，並與 Library 既有 Media 進行三態匹配。
        /// 物理意義：不每幀走目錄樹；只在載入或使用者手動重新整理時呼叫。
        /// </summary>
        public static List<ExternalComicSeries> ScanExternalComics(string iCustomRoot = null)
        {
            var results = new List<ExternalComicSeries>();
            string root = !string.IsNullOrEmpty(iCustomRoot) ? iCustomRoot : GetComicRoot();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return results;
            }

            var seriesMap = new Dictionary<string, ExternalComicSeries>(StringComparer.OrdinalIgnoreCase);

            try
            {
                string[] subDirs = Directory.GetDirectories(root);
                foreach (string dir in subDirs)
                {
                    string folderName = Path.GetFileName(dir);
                    if (string.IsNullOrEmpty(folderName) || folderName.StartsWith(".")) continue;

                    ParseSeriesAndVolume(folderName, out string seriesName, out string volumeLabel);
                    if (string.IsNullOrEmpty(seriesName)) continue;

                    if (!seriesMap.TryGetValue(seriesName, out var series))
                    {
                        string slug = NormalizeSeriesSlug(seriesName);
                        series = new ExternalComicSeries
                        {
                            SeriesName = seriesName,
                            Slug = slug,
                            MediaId = $"comic-{slug}",
                        };
                        seriesMap[seriesName] = series;
                    }

                    var vol = new ExternalComicVolume
                    {
                        FolderName = folderName,
                        FolderPath = dir,
                        VolumeLabel = volumeLabel,
                    };

                    // 掃描章節子資料夾
                    string[] chapterDirs = Directory.GetDirectories(dir);
                    if (chapterDirs.Length > 0)
                    {
                        Array.Sort(chapterDirs, StringComparer.OrdinalIgnoreCase);
                        foreach (string chDir in chapterDirs)
                        {
                            string chName = Path.GetFileName(chDir);
                            vol.Chapters.Add(chName);
                            // 統計圖片數
                            try
                            {
                                foreach (string file in Directory.GetFiles(chDir))
                                {
                                    string ext = Path.GetExtension(file);
                                    if (s_ImageExts.Contains(ext)) vol.PageCount++;
                                }
                            }
                            catch { }
                        }
                    }
                    else
                    {
                        // 根目錄直接放圖（單章）
                        try
                        {
                            foreach (string file in Directory.GetFiles(dir))
                            {
                                string ext = Path.GetExtension(file);
                                if (s_ImageExts.Contains(ext)) vol.PageCount++;
                            }
                            if (vol.PageCount > 0) vol.Chapters.Add("0001");
                        }
                        catch { }
                    }

                    series.Volumes.Add(vol);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ReadingLibrary] ScanExternalComics failed for {root}: {ex.Message}");
            }

            // 讀取 Library 中的現有 media/ 與 works/ 做 Join
            var allMedia = ListMediaEntries();
            var matchedMediaIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var kvp in seriesMap)
            {
                var series = kvp.Value;
                // 排序 volumes
                series.Volumes.Sort((a, b) => string.Compare(a.VolumeLabel, b.VolumeLabel, StringComparison.OrdinalIgnoreCase));
                
                int totalCh = 0;
                int totalPages = 0;
                foreach (var v in series.Volumes)
                {
                    totalCh += v.Chapters.Count;
                    totalPages += v.PageCount;
                }
                series.TotalChapters = totalCh;
                series.TotalPages = totalPages;

                // 比對 Library media
                var matchedMedia = allMedia.Find(m => string.Equals(m.MediaId, series.MediaId, StringComparison.OrdinalIgnoreCase));
                if (matchedMedia != null)
                {
                    series.HasMediaJson = true;
                    series.RegisteredTitle = matchedMedia.Title;
                    series.Status = ComicMatchStatus.Synced;
                    matchedMediaIds.Add(matchedMedia.MediaId);
                }
                else
                {
                    // 嘗試從 work.json 比對
                    string workJsonPath = Path.Combine(WorkRoot(series.Slug), k_WorkJsonName);
                    series.HasWorkJson = File.Exists(workJsonPath);
                    series.Status = ComicMatchStatus.Unregistered;
                }

                results.Add(series);
            }

            // 檢查已建檔但本機目錄失聯的 Media (MissingSource)
            foreach (var media in allMedia)
            {
                if (media.MediaKind == "comic" && !matchedMediaIds.Contains(media.MediaId))
                {
                    // 排除同事創作的內部漫畫 (ArtGallery/Comic/)
                    string internalComicPath = Path.Combine(UCL_RepoPath.AgentCommandsDir, "ArtGallery", "Comic", media.MediaId.Replace("comic-", ""));
                    if (!Directory.Exists(internalComicPath))
                    {
                        string slug = media.MediaId.StartsWith("comic-") ? media.MediaId.Substring("comic-".Length) : media.MediaId;
                        results.Add(new ExternalComicSeries
                        {
                            SeriesName = media.Title,
                            RegisteredTitle = media.Title,
                            Slug = slug,
                            MediaId = media.MediaId,
                            HasMediaJson = true,
                            HasWorkJson = true,
                            Status = ComicMatchStatus.MissingSource,
                        });
                    }
                }
            }

            results.Sort((a, b) => string.Compare(a.SeriesName, b.SeriesName, StringComparison.OrdinalIgnoreCase));
            return results;
        }


        // ===========================================================
        // JSON 讀寫
        // 物理意義：讀壞掉的檔不靜默回空物件 —— 那會讓下一次寫入把壞檔覆蓋成「乾淨」，
        //          原始資料連救都救不回來。壞檔一律讓 caller 收到 error。
        // ===========================================================
        public static JsonData LoadJson(string path, out string error)
        {
            error = null;
            if (!File.Exists(path)) { error = $"檔案不存在：{path}"; return null; }
            try
            {
                JsonData data = JsonData.ParseJson(File.ReadAllText(path, Encoding.UTF8));
                if (data == null || !data.IsObject) { error = $"不是 JSON object：{path}"; return null; }
                return data;
            }
            catch (Exception e)
            {
                error = $"JSON 解析失敗（{path}）：{e.Message}";
                return null;
            }
        }

        /// <summary>寫 JSON（UTF-8 無 BOM，beautify，非 ASCII 還原成原生字元）。父目錄自動建立。</summary>
        public static void SaveJson(string path, JsonData data)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, UnescapeNonAscii(data.ToJsonBeautify()) + "\n", new UTF8Encoding(false));
        }

        // ===========================================================
        // 區塊職責：把 ToJsonBeautify 產生的 \uXXXX 逃脫還原成原生字元（僅非 ASCII）。
        // 物理意義：既有檔（Python 端寫的）是原生 UTF-8 中文；C# 端若寫成 u-escape 形式，
        //          語意雖相同，但 **git diff 變成不可讀、grep 找不到書名、人也讀不了** ——
        //          同一份 schema 兩種寫法就是漂移的起點（2026-08-06 實測抓到）。
        // 數值影響：只還原 code point > 0x7F 的逃脫；ASCII 控制字元區間與 quote / backslash /
        //          等既有逃脫一律不動，維持 JSON 合法性。
        // ===========================================================
        static readonly Regex k_UnicodeEscape = new Regex(@"\\u([0-9a-fA-F]{4})");

        static string UnescapeNonAscii(string json)
        {
            return k_UnicodeEscape.Replace(json, match =>
            {
                int code = Convert.ToInt32(match.Groups[1].Value, 16);
                return code > 0x7F ? ((char)code).ToString() : match.Value;
            });
        }

        public static void SaveText(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }

        public static string Today() => DateTime.Now.ToString("yyyy-MM-dd");

        /// <summary>
        /// 區塊職責：組 JSON 字串陣列 —— 去重（保序）、去空白、把附帶的必含值一起收進來。
        /// 物理意義：aliases 一定要含 title / title_original 自己 —— 否則「用正式名搜尋卻搜不到」。
        /// 數值影響：純資料整理；大小寫不做正規化（搜尋端不分大小寫比對，這裡保留原字面）。
        /// </summary>
        public static JsonData ToStringArray(IList<string> values, params string[] alsoInclude)
        {
            JsonData array = JsonData.ParseJson("[]");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Push(string v)
            {
                if (string.IsNullOrWhiteSpace(v)) return;
                string trimmed = v.Trim();
                if (!seen.Add(trimmed)) return;
                array.Add(trimmed);
            }
            if (alsoInclude != null) foreach (string v in alsoInclude) Push(v);
            if (values != null) foreach (string v in values) Push(v);
            return array;
        }

        /// <summary>把 `a|b|c` 或 `a,b,c` 切成清單（別名含逗號的情況用 `|`）。</summary>
        public static List<string> SplitList(string raw)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(raw)) return result;
            char separator = raw.Contains("|") ? '|' : ',';
            foreach (string part in raw.Split(separator))
                if (!string.IsNullOrWhiteSpace(part)) result.Add(part.Trim());
            return result;
        }

        // ===========================================================
        // reader.json 讀取 + 身分校驗
        // 物理意義：路徑上的 <persona> 與檔內 reader_persona 不符 = 資料放錯讀者根目錄，
        //          那是「替別人代筆閱讀史」的前一步，必須擋。
        // ===========================================================
        public static JsonData LoadReader(string mediaId, string persona, out string error)
        {
            // ⚠ 「還不是這部的 reader」與「檔壞了」是兩件事，而 LoadJson 只會說「檔案不存在」——
            //   那是一句**死路**：它描述現況，不指出出口。所以這一格在進 LoadJson 之前先攔。
            string readerPath = ReaderJsonPath(mediaId, persona);
            if (!File.Exists(readerPath))
            {
                error = NotAReaderYetMessage(mediaId, persona, readerPath);
                return null;
            }
            JsonData reader = LoadJson(readerPath, out error);
            if (reader == null) return null;

            string declaredPersona = reader.GetString(Key_ReaderPersona, "");
            if (declaredPersona != persona)
            {
                error = $"reader.json.{Key_ReaderPersona}={declaredPersona}，與路徑 persona={persona} 不一致";
                return null;
            }
            string declaredMedia = reader.GetString(Key_MediaId, "");
            if (declaredMedia != mediaId)
            {
                error = $"reader.json.{Key_MediaId}={declaredMedia}，與請求 media_id={mediaId} 不一致";
                return null;
            }
            return reader;
        }

        // ===========================================================
        // 區塊職責：「你還不是這部的 reader」時，把**出口**印出來（不是只描述現況）。
        // 物理意義：`reader.json` 是所有寫入 op 的前置，而它只由 `media_init` 生出來 ——
        //          可是那個名字只說了一半（它同時也是「把我登記成這部的讀者」），
        //          於是撞到的人會以為那支是給「這部作品還不存在」的情況用的，不敢對既有 media 跑。
        // 🩸 TASK-0137（2026-09-05 summit）：陪看完第 1 話收工，回傳檔要我寫接續點，
        //   三支 op 全部回「檔案不存在：…/reader.json」。我讀不出出口，於是**沒有寫成接續點**
        //   ——而場次結算 exit 0、+10 token、公告照發 ⇒ 場次帳是綠的、記憶帳是空的。
        //   ⚠ 這是**窄報**（憲法⑤ 第四方向）：我判「那支不能跑」而它其實自帶出口，
        //   而 `MediaInit` 開頭就寫著「已存在的檔**不覆寫**」。實測：work/media 各印「已存在，不覆寫」，
        //   只新建我一個人的 reader.json（blast radius = 1 個目錄）。
        // 數值影響：純訊息，不寫任何檔。讀 media.json / work.json 只為把指令填成**可複製**的；
        //          讀不到就退回佔位符，**不猜**。
        // ===========================================================
        static string NotAReaderYetMessage(string mediaId, string persona, string readerPath)
        {
            string workId = "<work_id>";
            string mediaKind = "<media_kind>";
            string title = "<作品中文名>";
            JsonData media = LoadJson(Path.Combine(MediaRoot(mediaId), k_MediaJsonName), out _);
            if (media != null)
            {
                workId = media.GetString(Key_WorkId, workId);
                mediaKind = media.GetString(Key_MediaKind, mediaKind);
                JsonData work = LoadJson(Path.Combine(WorkRoot(workId), k_WorkJsonName), out _);
                if (work != null) title = work.GetString(Key_Title, title);
            }
            return
                $"你還不是 `{mediaId}` 的 reader —— `reader.json` 不存在：{readerPath}\n" +
                $"⇒ 出口（**這一支就是登記入口**，不是只給新作品用的）：\n" +
                $"   Library op=media_init --arg persona={persona} --arg media_id={mediaId} " +
                $"--arg work_id={workId} --arg media_kind={mediaKind} --arg title={title} " +
                $"--arg anticipation=<1-5 期待度>\n" +
                $"⚠ 它的名字只說了一半：media 已存在時 **work.json / media.json 一律不覆寫**，" +
                $"只補建你自己的 reader.json（既有讀者的進度不受影響）。";
        }

        // ===========================================================
        // 章節連續性分類（Tim 2026-08-06 拍板後的語意：分類，不是閘門）
        // 物理意義：有自己的紀錄就直接讀回並放行；分類只是為了在回報與 chapter.json 留下痕跡，
        //          讓「跳章」不會靜默變成一份看起來連續的閱讀史。
        // 數值影響：不擋任何寫入；0000 序章不參與連續性判定（它非必有）。
        // ===========================================================
        public enum ChapterRelation { FirstEver, Reread, Next, Gap, Prologue }

        public static ChapterRelation ClassifyChapter(JsonData reader, string chapterId)
        {
            if (chapterId == PrologueChapterId) return ChapterRelation.Prologue;
            if (reader == null) return ChapterRelation.FirstEver;

            string current = reader.IsObject && reader.Contains(Key_Progress)
                ? reader[Key_Progress].GetString(Key_CurrentChapterId, "")
                : "";
            if (string.IsNullOrEmpty(current) || current == PrologueChapterId) return ChapterRelation.Next;
            if (current == chapterId) return ChapterRelation.Reread;
            if (int.TryParse(current, out int cur) && int.TryParse(chapterId, out int req) && req == cur + 1)
                return ChapterRelation.Next;
            return ChapterRelation.Gap;
        }

        // ===========================================================
        // 建檔（op=media_init）
        // 物理意義：work.json（作品層，可被多媒材共用）/ media.json（媒材層）/ reader.json（讀者層）。
        // 數值影響：已存在的檔**不覆寫** —— 建檔重跑不該蓋掉既有進度。
        // ===========================================================
        public static string MediaInit(string workId, string mediaId, string mediaKind, string persona,
                                       string title, string titleOriginal, string author, int anticipation,
                                       IList<string> aliases, IList<string> genreTags,
                                       out string error)
        {
            error = null;
            var log = new StringBuilder();

            log.Append(EnsureWorkJson(workId, title, titleOriginal, author, aliases, genreTags));

            string mediaPath = Path.Combine(MediaRoot(mediaId), k_MediaJsonName);
            if (File.Exists(mediaPath))
            {
                JsonData existing = LoadJson(mediaPath, out string mediaErr);
                if (existing == null) { error = mediaErr; return log.ToString(); }
                string existingWork = existing.GetString(Key_WorkId, "");
                if (existingWork != workId)
                {
                    error = $"media.json 已存在且 {Key_WorkId}={existingWork}，與請求 {workId} 不符 —— " +
                            "同一 media id 指向兩個作品是身分層錯誤，請改用不同 media_id 或先確認哪個才對";
                    return log.ToString();
                }
                log.AppendLine($"- media.json 已存在，不覆寫：`{mediaId}`");
            }
            else
            {
                var media = new JsonData();
                media[Key_MediaId] = mediaId;
                media[Key_WorkId] = workId;
                media[Key_MediaKind] = mediaKind;
                media[Key_SchemaVersion] = 1;
                SaveJson(mediaPath, media);
                log.AppendLine($"- ✅ 建立 media.json：`{mediaId}`（{mediaKind}）");
            }

            log.Append(EnsureReaderJson(mediaId, persona, anticipation, out _));

            return log.ToString();
        }

        // ===========================================================
        // 區塊職責：**建 reader.json（不存在才建）** —— `MediaInit` 與 `RegisterReader` 共用這一份。
        // 物理意義：reader.json 是「這個人在看這部」這件事的落檔，schema 只准有一種形狀。
        // 數值影響：已存在 ⇒ **零寫入**（既有進度一個位元組都不動），`oCreated=false`。
        // 🩸 為什麼抽出來：TASK-0137 要在進場時也能登記，而「再寫一次同樣的初值」＝ 第二份 schema。
        //   兩份初值長得一樣時不會有人發現它們已經分岔（少一個欄位的 reader 讀回來也「正常」）。
        // ===========================================================
        static string EnsureReaderJson(string mediaId, string persona, int anticipation, out bool oCreated)
        {
            oCreated = false;
            string readerPath = ReaderJsonPath(mediaId, persona);
            if (File.Exists(readerPath))
                return $"- reader.json 已存在，不覆寫：`{persona}`（既有進度保留）" + Environment.NewLine;

            var reader = new JsonData();
            reader[Key_SchemaVersion] = 2;
            reader[Key_ReaderPersona] = persona;
            reader[Key_MediaId] = mediaId;
            reader[Key_Status] = "reading";
            reader[Key_Anticipation] = anticipation;
            reader[Key_ReadingStartedAt] = Today();
            var progress = new JsonData();
            progress[Key_CurrentChapterId] = "";
            progress[Key_LastRead] = Today();
            progress[Key_BookmarkNote] = "（尚未開始）";
            reader[Key_Progress] = progress;
            reader[Key_CurrentImpression] = "（尚未寫下第一筆心得）";
            reader[Key_UpdatedAt] = Today();
            SaveJson(readerPath, reader);
            SyncBookshelf(mediaId, persona, out _);
            oCreated = true;
            return $"- ✅ 建立 reader.json：`{persona}`（期待度 {anticipation}／5）" + Environment.NewLine;
        }

        // ===========================================================
        // 區塊職責：把 persona 登記成**既有 media** 的 reader（進場即註冊）——
        //          給觀影／閱讀流程在「我要開始看這部」的那一刻呼叫，⛔ 不是給建新作品用的。
        // 物理意義：reader 的語意是「這個人在看這部」，而那件事發生在**進場**，不是收工。
        //   🩸 TASK-0137（summit 2026-09-05）：第一次陪看某作品的人，收工回傳檔叫他寫接續點，
        //   而 `note_chapter`／`bookmark`／`recall` 三支的前置都是 reader.json ⇒ 三支全失敗。
        //   場次帳 exit 0、+10 token、公告照發，記憶帳是空的 —— **兩本帳分開結算，而空的那本不會叫**。
        // 數值影響：
        //   · media.json **不存在 ⇒ 什麼都不建**、回 false 並給 error。
        //     ⛔ 不從 media_id 反推 work/title 去補建 —— 那是替作品層捏身分，而它「看起來會很正常」。
        //   · reader.json 已存在 ⇒ 零寫入、`oCreated=false`、回 true（冪等，進場每次呼叫都安全）。
        //   · anticipation 預設 3（中性）—— 進場時本人還沒讀，工具**不替他表態**；他自己改。
        // ===========================================================
        public static bool RegisterReader(string mediaId, string persona,
                                          out bool oCreated, out string oLog, out string error)
        {
            oCreated = false; oLog = ""; error = null;
            if (string.IsNullOrEmpty(mediaId) || string.IsNullOrEmpty(persona))
            {
                error = "RegisterReader：media_id 與 persona 都必填";
                return false;
            }
            string mediaPath = Path.Combine(MediaRoot(mediaId), k_MediaJsonName);
            if (!File.Exists(mediaPath))
            {
                error = $"media.json 不存在：{mediaPath} —— 這部作品還沒進閱讀庫，" +
                        $"⇒ 先走 `Library op=media_init`（那一支才會建 work／media 層）";
                return false;
            }
            oLog = EnsureReaderJson(mediaId, persona, 3, out oCreated);
            return true;
        }

        // ===========================================================
        // 落一筆章節心得（op=note_chapter）
        // 物理意義：round md 是事實源，chapter.json 是 round 索引，reader.json 是當前狀態。
        // 數值影響：既有 round **絕不覆寫** —— 同章再寫一次就開下一個 r{N}。
        //           `append=true` 是**唯一**的例外，而它是**追加不是覆寫**：正文接在既有 round 檔尾端，
        //           原本的字一個都不動，`segments` +1。
        // 🩸 TASK-0121 為什麼要有這條路（拍板：走 code 補續寫，不改 skill 的字）：
        //   「一話一 round，場次中斷續寫同一個 round；r2 只留給真正的重看」是 skill 早就寫著的規則，
        //   而 code 這邊沒有任何參數表達得出「續寫」⇒ 同一話的第二場照樣開 r2。
        //   兩份規則各自都對，落地結果相反，而失效是**靜默**的：r2 落地回「✓ 成功」，
        //   chapter.json 也長得完全正常。⇒ 收斂成一份，收斂點放在 code
        //   （改 skill 的字要把「r2＝重看」這個既有語意永久放棄掉，那筆帳更貴）。
        // ===========================================================
        public static string NoteChapter(string mediaId, string persona, string chapterId,
                                         string displayNumber, string chapterTitle, string timeRange,
                                         string body, string impression, string bookmarkNote,
                                         bool append, int appendRound,
                                         out string roundFilePath, out int roundNumber, out string error)
        {
            roundFilePath = null;
            roundNumber = 0;
            error = null;

            JsonData reader = LoadReader(mediaId, persona, out error);
            if (reader == null)
            {
                // 前置階梯（Tim 2026-08-06）：沒有自己的紀錄 → 停下來，不自作主張建檔。
                error = $"{error}\n" +
                        $"→ 這位 persona 在此 media 尚無新架構紀錄。依定案流程：" +
                        $"① 若 Archive 有舊心得 → 先跑 migration 手動搬到新架構；" +
                        $"② 若查無舊心得 → 先跑 op=media_init 建檔；" +
                        $"③ 若要接力別人的心得 → 由 Tim 指定來源 persona（讀可跨 persona，寫只寫自己）。";
                return null;
            }

            ChapterRelation relation = ClassifyChapter(reader, chapterId);
            string chapterDir = ChapterDir(mediaId, persona, chapterId);
            string chapterJsonPath = Path.Combine(chapterDir, k_ChapterJsonName);

            JsonData chapter = File.Exists(chapterJsonPath) ? LoadJson(chapterJsonPath, out error) : null;
            if (File.Exists(chapterJsonPath) && chapter == null) return null;   // 壞檔不覆蓋

            if (chapter == null)
            {
                chapter = new JsonData();
                chapter[Key_ChapterId] = chapterId;
                // display_number 是投影：沒給人話字面就留空，由顯示端派生 —— 不再手填成 id 複寫
                // （basecamp 2026-08-06 量到既有樣本已退化成 display_number == chapter_id）。
                chapter[Key_DisplayNumber] = displayNumber ?? "";
                chapter[Key_Title] = chapterTitle ?? "";
                if (!string.IsNullOrEmpty(timeRange)) chapter[Key_TimeRange] = timeRange;
                chapter[Key_Rounds] = JsonData.ParseJson("[]");
                chapter[Key_SchemaVersion] = 2;
            }
            else
            {
                if (!string.IsNullOrEmpty(displayNumber)) chapter[Key_DisplayNumber] = displayNumber;
                if (!string.IsNullOrEmpty(chapterTitle)) chapter[Key_Title] = chapterTitle;
                // ⚠ 續寫時章層的 time_range 是**接上去**不是蓋掉，也不是留著第一段就算了：
                //   那一格是「這一話」的時間段，而續寫帶進來的是「這一場」的。
                //   蓋掉 ⇒ 第一場的區間消失，而消失的樣子跟「本來就只有這一段」一模一樣；
                //   留著不動 ⇒ 一話跑到 52:00 而章層寫著 00:00-30:00，那是一個**看起來完整**的錯讀數。
                //   🩸 這一格是我自己 2026-09-05 讀探針落盤的檔才看到的 —— 工具的回讀沒有講它。
                //   ⇒ 逐場列出來，兩段都在：`00:00-30:00, 30:00-52:00`。
                if (!string.IsNullOrEmpty(timeRange))
                {
                    string existingRange = chapter.GetString(Key_TimeRange, "");
                    chapter[Key_TimeRange] =
                        append && existingRange.Length > 0 && !existingRange.Contains(timeRange)
                            ? $"{existingRange}, {timeRange}"
                            : timeRange;
                }
            }

            JsonData rounds = chapter.Contains(Key_Rounds) ? chapter[Key_Rounds] : null;
            if (rounds == null || !rounds.IsArray)
            {
                rounds = JsonData.ParseJson("[]");
                chapter[Key_Rounds] = rounds;
            }

            // round 編號 = 既有最大值 + 1（不看檔案數 —— 檔可能被人另外加，索引才是真相源）
            int maxRound = 0;
            for (int i = 0; i < rounds.Count; i++)
            {
                int n = rounds[i].GetInt(Key_Round, 0);
                if (n > maxRound) maxRound = n;
            }
            roundNumber = maxRound + 1;

            // ── 續寫（TASK-0121）：追加進既有 round，不開下一個 r{N} ──────────────
            // ⚠ 這一段是**唯一**會動到既有 round 檔的路，所以三件事都要說出來而不是靜默處理：
            //   ① 指定的 round 不在索引裡　② 索引指的檔在磁碟上不見了　③ 這一章根本還沒有第一場。
            //   前兩者拒絕寫入（磁碟與索引不一致要人先看一眼）；③ 不是錯，它就是第一場 ⇒ 照常開 r1。
            bool appended = false;
            int segmentCount = 1;
            string fileName;
            if (append && maxRound > 0)
            {
                int target = appendRound > 0 ? appendRound : maxRound;
                JsonData targetEntry = null;
                for (int i = 0; i < rounds.Count; i++)
                    if (!rounds[i].IsString && rounds[i].GetInt(Key_Round, 0) == target) targetEntry = rounds[i];

                if (targetEntry == null)
                {
                    error = $"要續寫的 r{target} 不在 chapter.json 索引裡（現有最大 r{maxRound}）—— " +
                            "拒絕寫入，索引說沒有的東西不該由工具生出來";
                    return null;
                }

                string targetFile = targetEntry.GetString(Key_File, "");
                string targetPath = Path.Combine(chapterDir, targetFile);
                if (string.IsNullOrEmpty(targetFile) || !File.Exists(targetPath))
                {
                    error = $"r{target} 的索引指向 `{targetFile}`，而磁碟上沒有這個檔 —— " +
                            "拒絕續寫（索引與磁碟不一致要人先看一眼，不該由工具猜）";
                    return null;
                }

                segmentCount = targetEntry.GetInt(Key_Segments, 1) + 1;
                string head = $"## 續寫・第 {segmentCount} 場（{Today()}"
                              + (string.IsNullOrEmpty(timeRange) ? "" : $"　{timeRange}") + "）";
                // 追加**不覆寫**：先讀既有內容再整份寫回（SaveText 是全檔寫入）。
                string existing = File.ReadAllText(targetPath, Encoding.UTF8).TrimEnd();
                SaveText(targetPath, $"{existing}\n\n---\n\n{head}\n\n{body.TrimEnd()}\n");

                targetEntry[Key_Segments] = segmentCount;
                roundNumber = target;
                fileName = targetFile;
                roundFilePath = targetPath;
                appended = true;
                SaveJson(chapterJsonPath, chapter);
            }
            else
            {
                fileName = $"r{roundNumber}_{Today()}.md";
                roundFilePath = Path.Combine(chapterDir, fileName);
                if (File.Exists(roundFilePath))
                {
                    error = $"round 檔已存在但不在 chapter.json 索引內：{fileName} —— " +
                            "拒絕覆寫（索引與磁碟不一致要人先看一眼，不該由工具猜）";
                    return null;
                }

                SaveText(roundFilePath, body.TrimEnd() + "\n");

                var entry = new JsonData();
                entry[Key_Round] = roundNumber;
                entry[Key_ReadingDate] = Today();
                entry[Key_File] = fileName;
                if (relation == ChapterRelation.Gap) entry[Key_Gap] = true;   // 跳章不擋，但留痕
                rounds.Add(entry);
                SaveJson(chapterJsonPath, chapter);
            }

            // reader.json 當前狀態
            JsonData progress = reader.Contains(Key_Progress) ? reader[Key_Progress] : null;
            if (progress == null || !progress.IsObject)
            {
                progress = new JsonData();
                reader[Key_Progress] = progress;
            }
            progress[Key_CurrentChapterId] = chapterId;
            progress[Key_LastRead] = Today();
            if (!string.IsNullOrEmpty(bookmarkNote)) progress[Key_BookmarkNote] = bookmarkNote;
            if (!string.IsNullOrEmpty(impression)) reader[Key_CurrentImpression] = impression;
            reader[Key_UpdatedAt] = Today();
            SaveJson(ReaderJsonPath(mediaId, persona), reader);

            SyncBookshelf(mediaId, persona, out _);
            // 每次寫入後重生成追回檔 —— 否則下次續讀撈到的是上一次的視圖（stale 投影比沒有投影更糟）。
            WriteRecallBrief(mediaId, persona, true, out _);

            var log = new StringBuilder();
            log.AppendLine($"- 章節：`{chapterId}`" +
                           (string.IsNullOrEmpty(chapterTitle) ? "" : $"　{chapterTitle}") +
                           (string.IsNullOrEmpty(timeRange) ? "" : $"　（{timeRange}）"));
            // ⚠ 續寫時**不印** RelationLabel：那句話回答的是「這一章跟上次讀到哪的關係」，
            //   而續寫的答案永遠是「同一章」—— 印出來會變成一句永遠成立、因此不帶資訊的話。
            log.AppendLine(appended
                ? $"- round：**r{roundNumber}**（續寫・第 {segmentCount} 場 —— **沒有開新的 round**；" +
                  "`r{N}` 是第 N 次讀這一話，不是第 N 次寫入）"
                : $"- round：**r{roundNumber}**（{RelationLabel(relation)}）");
            log.AppendLine($"- 心得檔：`{fileName}`" + (appended ? "（追加在尾端，既有內容未動）" : ""));
            return log.ToString();
        }

        public static string RelationLabel(ChapterRelation relation)
        {
            switch (relation)
            {
                case ChapterRelation.FirstEver: return "首筆紀錄";
                case ChapterRelation.Reread: return "重讀同章 → 開新 round，舊 round 保留";
                case ChapterRelation.Next: return "續讀（+1）";
                case ChapterRelation.Gap: return "⚠ 跳章（已在 chapter.json 記 gap，未靜默）";
                case ChapterRelation.Prologue: return "序章（不參與連續性判定）";
            }
            return relation.ToString();
        }

        // ===========================================================
        // 人物：facts（客觀，profile.json）與 view（主觀，vN_<date>.md）分離
        // 物理意義：**改觀就 fork 新版本，絕不覆寫舊版** —— 好書值得重讀正因看法會變，
        //          v1→v2→v3 的演變本身就是閱讀體驗（同構於 relationship opinion history / persona fork）。
        // 數值影響：AddCharacter 只在人物不存在時建 v1；已存在一律要求走 ReviseView。
        // ===========================================================
        public static string AddCharacter(string mediaId, string persona, string characterId,
                                          string name, string nameOriginal, string facts, string view,
                                          out string error)
        {
            error = null;
            if (LoadReader(mediaId, persona, out error) == null) return null;

            string dir = Path.Combine(ReaderRoot(mediaId, persona), k_CharactersDirName, characterId);
            string profilePath = Path.Combine(dir, k_ProfileJsonName);
            if (File.Exists(profilePath))
            {
                error = $"人物已存在：{characterId} —— **看法有變請走 op=revise_view（fork 新版本）**，" +
                        "不要用 add_character 覆寫既有 v1（那會抹掉當時的「還不知道」）。" +
                        "只想補客觀 facts 也走 revise_view --facts。";
                return null;
            }

            var profile = new JsonData();
            profile[Key_CharacterId] = characterId;
            profile[Key_Name] = name;
            profile[Key_NameOriginal] = nameOriginal ?? "";
            profile[Key_Facts] = FactsToJson(facts);   // 一律陣列 —— 寫端收斂，見 FactsToJson
            profile[Key_SchemaVersion] = 1;
            SaveJson(profilePath, profile);

            string fileName = $"v1_{Today()}.md";
            SaveText(Path.Combine(dir, fileName), RenderViewFile(characterId, 1, persona, null, view));

            WriteRecallBrief(mediaId, persona, true, out _);
            return $"- ✅ 新增人物 `{characterId}`（{name}）＋ 初版看法 `{fileName}`";
        }

        /// <summary>
        /// 改觀 → fork 下一版 view（永不覆寫）。可同時補客觀 facts（那是可更新的已確認資料）。
        /// </summary>
        public static string ReviseView(string mediaId, string persona, string characterId,
                                        string view, string changeReason, string facts,
                                        out string error)
        {
            error = null;
            if (LoadReader(mediaId, persona, out error) == null) return null;

            string dir = Path.Combine(ReaderRoot(mediaId, persona), k_CharactersDirName, characterId);
            string profilePath = Path.Combine(dir, k_ProfileJsonName);
            JsonData profile = LoadJson(profilePath, out error);
            if (profile == null)
            {
                error = $"{error}\n→ 人物不存在，第一次記請走 op=add_character。";
                return null;
            }

            // 版本號取既有檔案最大值 + 1（掃磁碟而非猜，缺號也不會覆蓋既有版本）
            int maxVersion = 0;
            foreach (string existing in Directory.GetFiles(dir, "v*.md"))
            {
                Match m = k_ViewFilePattern.Match(Path.GetFileName(existing));
                if (m.Success && int.TryParse(m.Groups[1].Value, out int n) && n > maxVersion) maxVersion = n;
            }
            int version = maxVersion + 1;

            string fileName = $"v{version}_{Today()}.md";
            string path = Path.Combine(dir, fileName);
            if (File.Exists(path))
            {
                error = $"同日已有 {fileName} 但不在版本掃描結果內 —— 拒絕覆寫，請人先看一眼";
                return null;
            }
            SaveText(path, RenderViewFile(characterId, version, persona, changeReason, view));

            if (!string.IsNullOrEmpty(facts))
            {
                profile[Key_Facts] = FactsToJson(facts);   // 一律陣列 —— 寫端收斂，見 FactsToJson
                SaveJson(profilePath, profile);
            }

            WriteRecallBrief(mediaId, persona, true, out _);
            return $"- ✅ `{characterId}` 看法已 fork 為 **v{version}**（`{fileName}`）；" +
                   $"v1–v{maxVersion} 保留不動" + (string.IsNullOrEmpty(facts) ? "" : "；facts 同步更新");
        }

        static readonly Regex k_ViewFilePattern = new Regex(@"^v(\d+)_");

        /// <summary>view 檔內容 —— frontmatter 與既有樣本同構（character_id / version / date / reader_persona）。</summary>
        static string RenderViewFile(string characterId, int version, string persona,
                                     string changeReason, string view)
        {
            var sb = new StringBuilder();
            sb.AppendLine("---");
            sb.AppendLine($"{Key_CharacterId}: {characterId}");
            sb.AppendLine($"version: {version}");
            sb.AppendLine($"date: {Today()}");
            sb.AppendLine($"{Key_ReaderPersona}: {persona}");
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine($"## {persona} 的看法（v{version}）");
            sb.AppendLine();
            if (!string.IsNullOrEmpty(changeReason))
            {
                // 改觀理由單獨成段：**為什麼變**比**變成什麼**更難事後重建
                sb.AppendLine($"> **改觀觸發**：{changeReason}");
                sb.AppendLine();
            }
            sb.AppendLine(view.TrimEnd());
            return sb.ToString();
        }

        /// <summary>只更新書籤與當前看法（op=bookmark）。</summary>
        public static string Bookmark(string mediaId, string persona, string note, string impression,
                                      string status, out string error)
        {
            JsonData reader = LoadReader(mediaId, persona, out error);
            if (reader == null) return null;

            JsonData progress = reader.Contains(Key_Progress) ? reader[Key_Progress] : null;
            if (progress == null || !progress.IsObject)
            {
                progress = new JsonData();
                reader[Key_Progress] = progress;
            }
            if (!string.IsNullOrEmpty(note)) progress[Key_BookmarkNote] = note;
            progress[Key_LastRead] = Today();
            if (!string.IsNullOrEmpty(impression)) reader[Key_CurrentImpression] = impression;
            if (!string.IsNullOrEmpty(status)) reader[Key_Status] = status;
            reader[Key_UpdatedAt] = Today();
            SaveJson(ReaderJsonPath(mediaId, persona), reader);
            SyncBookshelf(mediaId, persona, out _);
            WriteRecallBrief(mediaId, persona, true, out _);   // 同上：書籤變了追回檔就得重生成
            return $"- 書籤已更新（`{mediaId}` / `{persona}`）";
        }

        // ===========================================================
        // bookshelf.md 同步
        // 物理意義：**投影，不是第二真相源** —— 一律由 reader.json 重新生成，人不手改。
        // ===========================================================
        public static void SyncBookshelf(string mediaId, string persona, out string error)
        {
            JsonData reader = LoadReader(mediaId, persona, out error);
            if (reader == null) return;

            JsonData media = LoadJson(Path.Combine(MediaRoot(mediaId), k_MediaJsonName), out _);
            string workId = media != null ? media.GetString(Key_WorkId, "") : "";
            string mediaKind = media != null ? media.GetString(Key_MediaKind, "unknown") : "unknown";
            JsonData work = string.IsNullOrEmpty(workId)
                ? null : LoadJson(Path.Combine(WorkRoot(workId), k_WorkJsonName), out _);
            string title = work != null ? work.GetString(Key_Title, workId) : workId;

            JsonData progress = reader.Contains(Key_Progress) ? reader[Key_Progress] : null;
            string chapterId = progress != null ? progress.GetString(Key_CurrentChapterId, "") : "";
            string lastRead = progress != null ? progress.GetString(Key_LastRead, "") : "";
            string bookmark = progress != null ? progress.GetString(Key_BookmarkNote, "") : "";

            var sb = new StringBuilder();
            sb.AppendLine("---");
            sb.AppendLine($"{Key_WorkId}: {workId}");
            sb.AppendLine($"{Key_MediaId}: {mediaId}");
            sb.AppendLine($"{Key_MediaKind}: {mediaKind}");
            sb.AppendLine($"{Key_ReaderPersona}: {persona}");
            sb.AppendLine($"{Key_Status}: {reader.GetString(Key_Status, "reading")}");
            sb.AppendLine($"{Key_Anticipation}: {reader.GetInt(Key_Anticipation, 0)}");
            sb.AppendLine($"progress_snapshot_chapter: \"{chapterId}\"");
            sb.AppendLine($"progress_snapshot_last_read: {lastRead}");
            sb.AppendLine($"{Key_UpdatedAt}: {reader.GetString(Key_UpdatedAt, Today())}");
            sb.AppendLine("generated: mechanical   # 由 UCL_ReadingLibraryIO 由 reader.json 生成；手改會被覆寫");
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine($"# {persona} 的《{title}》閱讀卡");
            sb.AppendLine();
            sb.AppendLine("> `reader.json` 是本卡片的資料真相源；此檔是人可讀投影，每次寫入後重新生成。");
            sb.AppendLine();
            sb.AppendLine($"**期待度：{reader.GetInt(Key_Anticipation, 0)}／5**");
            sb.AppendLine();
            sb.AppendLine("## 目前進度");
            sb.AppendLine();
            sb.AppendLine(string.IsNullOrEmpty(bookmark) ? "（尚無書籤）" : bookmark);
            sb.AppendLine();
            sb.AppendLine("## 目前看法");
            sb.AppendLine();
            sb.AppendLine(reader.GetString(Key_CurrentImpression, "（尚無）"));

            string aText = sb.ToString();
            SaveText(Path.Combine(ReaderRoot(mediaId, persona), k_BookshelfName), aText);
            ForwardBookshelfToLetters(mediaId, persona, aText);
        }

        // ===========================================================
        // bookshelf 轉發到 letters/<persona>/bookshelf/
        // 區塊職責：把閱讀卡多送一份到該 persona 自己的信件目錄，讓「我跟這本書的關係」
        //          跟 sketchbook（我對**人**的看法）並排 —— 一個看書、一個看人。
        // 物理意義：**投影的投影，不是第三個真相源。** 真相源永遠是 reader.json；
        //          Library 內那份是投影，這份是給 persona 隨身帶的副本。
        //          任何流程都**只准讀它、不准回寫**；要改內容去改 reader.json 再 Sync。
        // 數值影響：每次 SyncBookshelf 都整份覆寫一個檔。
        // ⚠ letters/<persona>/ 每一個都是獨立 git submodule —— 這裡每寫一次就弄髒該 persona 的 repo。
        //   目前觸發點是「該 persona 自己寫心得」，弄髒的是自己的 repo，代價收斂在當事人身上；
        //   若日後有「一次同步全部 persona」的批次入口，請先想清楚那會一次弄髒 N 個 repo。
        // 邊界：寫檔失敗只印 warning，不讓轉發失敗連累已經落盤的正本（正本先寫、副本後寫）。
        // ===========================================================
        static void ForwardBookshelfToLetters(string mediaId, string persona, string text)
        {
            try
            {
                string path = Path.Combine(UCL_LettersPath.PersonaDir(persona),
                                           k_LettersBookshelfDirName, $"{mediaId}.md");
                SaveText(path, text);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ReadingLibrary] bookshelf 轉發至 letters 失敗（正本已寫入，不影響資料）：{e.Message}");
            }
        }

        /// <summary>letters 下的書架目錄名 —— 與 sketchbook（看人）成對，本目錄是看書。</summary>
        const string k_LettersBookshelfDirName = "bookshelf";

        // ===========================================================
        // 讀回（recall）—— 頁面與 Cmd 共用同一段
        // 物理意義：把一位 persona 在一個 media 的累積紀錄組成單一可讀視圖。
        // 數值影響：純讀；缺檔 / 壞檔一律在輸出裡留 WARNING，不靜默略過。
        // ===========================================================
        public static string RenderRecall(string mediaId, string persona, bool fullRounds, out string error)
        {
            JsonData reader = LoadReader(mediaId, persona, out error);
            if (reader == null) return null;

            JsonData media = LoadJson(Path.Combine(MediaRoot(mediaId), k_MediaJsonName), out _);
            string workId = media != null ? media.GetString(Key_WorkId, "") : "";
            JsonData work = string.IsNullOrEmpty(workId)
                ? null : LoadJson(Path.Combine(WorkRoot(workId), k_WorkJsonName), out _);
            JsonData progress = reader.Contains(Key_Progress) ? reader[Key_Progress] : null;

            var sb = new StringBuilder();
            // 區塊職責：frontmatter —— 與 cmd/wake_brief.md 同慣例，明寫「機械產物、手改會被覆寫」。
            // 物理意義：這份是視圖不是筆記；事實源永遠是 reader.json / chapter round / character view。
            // 數值影響：純輸出；generated_at 用本機時間（跨機比對時以檔內 media/persona 為準）。
            sb.AppendLine("---");
            sb.AppendLine("type: reading_recall");
            sb.AppendLine($"persona: {persona}");
            sb.AppendLine($"media_id: {mediaId}");
            sb.AppendLine($"work_id: {(string.IsNullOrEmpty(workId) ? "unknown" : workId)}");
            sb.AppendLine($"generated_at: {DateTime.Now:yyyy-MM-ddTHH:mm:sszzz}");
            sb.AppendLine("generated: mechanical   # 每次 recall / 寫入後重新生成 —— 手改會被覆寫");
            sb.AppendLine("source_of_truth: AgentCommands/BookNotes/Library");
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine($"# 📖 閱讀追回｜{(work != null ? work.GetString(Key_Title, mediaId) : mediaId)}");
            sb.AppendLine();
            sb.AppendLine($"- reader：`{persona}`　media：`{mediaId}`" +
                          $"（{(media != null ? media.GetString(Key_MediaKind, "unknown") : "unknown")}）");
            if (work != null)
            {
                sb.AppendLine($"- 原文名：{work.GetString(Key_TitleOriginal, "（未登錄）")}　" +
                              $"作者／監督：{work.GetString(Key_Author, "（未登錄）")}");
                JsonData aliases = work.Contains(Key_Aliases) ? work[Key_Aliases] : null;
                if (aliases != null && aliases.IsArray && aliases.Count > 0)
                {
                    var names = new List<string>();
                    for (int i = 0; i < aliases.Count; i++)
                    {
                        string a = AliasToString(aliases[i]);   // 物件形狀 alias 也要印，別靜默跳過
                        if (!string.IsNullOrEmpty(a)) names.Add(a);
                    }
                    if (names.Count > 0) sb.AppendLine($"- 別名（搜尋用）：{string.Join(" / ", names)}");
                }
            }
            sb.AppendLine($"- status：`{reader.GetString(Key_Status, "unknown")}`　" +
                          $"期待度 {reader.GetInt(Key_Anticipation, 0)}／5");
            sb.AppendLine($"- 讀到：`{(progress != null ? progress.GetString(Key_CurrentChapterId, "未設定") : "未設定")}`　" +
                          $"最後閱讀：{(progress != null ? progress.GetString(Key_LastRead, "未設定") : "未設定")}");
            sb.AppendLine();
            sb.AppendLine("## 🔖 書籤（上次寫到哪）");
            sb.AppendLine();
            sb.AppendLine(progress != null ? progress.GetString(Key_BookmarkNote, "（無）") : "（無）");
            sb.AppendLine();
            sb.AppendLine("## 💭 目前看法");
            sb.AppendLine();
            sb.AppendLine(reader.GetString(Key_CurrentImpression, "（尚無）"));
            sb.AppendLine();
            // 「作品與媒材」「書架投影」兩節 —— Python 版有、C# 初版漏（Sirius diff 抓到）。
            // 收斂規則是逐節點名補齊，不是整段照抄任一邊（兩版互有對方沒有的節）。
            sb.AppendLine("## 🗂 作品與媒材");
            sb.AppendLine();
            sb.AppendLine($"- work_id: `{(string.IsNullOrEmpty(workId) ? "unknown" : workId)}`");
            if (work != null)
            {
                sb.AppendLine($"- title: {work.GetString(Key_Title, "（未登錄）")}");
                sb.AppendLine($"- title_original: {work.GetString(Key_TitleOriginal, "（未登錄）")}");
                sb.AppendLine($"- author: {work.GetString(Key_Author, "（未登錄）")}");
                JsonData tags = work.Contains(Key_GenreTags) ? work[Key_GenreTags] : null;
                if (tags != null && tags.IsArray && tags.Count > 0)
                {
                    var tagList = new List<string>();
                    for (int i = 0; i < tags.Count; i++) tagList.Add(tags[i].GetString());
                    sb.AppendLine($"- genre_tags: {string.Join(", ", tagList)}");
                }
                else
                {
                    sb.AppendLine("- genre_tags: （未登錄）");
                }
            }
            else
            {
                sb.AppendLine("- （work.json 未登錄或讀取失敗 —— 只列 media 層資訊）");
            }
            sb.AppendLine();
            sb.AppendLine("## 🗄 書架投影");
            sb.AppendLine();
            string shelfPath = Path.Combine(ReaderRoot(mediaId, persona), k_BookshelfName);
            sb.AppendLine(File.Exists(shelfPath)
                ? File.ReadAllText(shelfPath, Encoding.UTF8).TrimEnd()
                : "（無 bookshelf 投影）");
            sb.AppendLine();
            sb.AppendLine("## 📚 章節與 round");
            sb.AppendLine();

            string chaptersRoot = Path.Combine(ReaderRoot(mediaId, persona), k_ChaptersDirName);
            // 沒有章節不能提早 return —— 人物觀點也要出現在追回檔裡（2026-08-06 Tim QA 指出的缺口）。
            if (!Directory.Exists(chaptersRoot))
            {
                sb.AppendLine("（尚無章節紀錄）");
                sb.AppendLine();
                AppendCharacters(sb, mediaId, persona);
                return sb.ToString();
            }

            var chapterDirs = new List<string>(Directory.GetDirectories(chaptersRoot));
            chapterDirs.Sort(StringComparer.Ordinal);
            foreach (string dir in chapterDirs)
            {
                string id = Path.GetFileName(dir);
                JsonData chapter = LoadJson(Path.Combine(dir, k_ChapterJsonName), out string chapterErr);
                if (chapter == null)
                {
                    sb.AppendLine($"### `{id}`");
                    sb.AppendLine($"> [!WARNING]");
                    sb.AppendLine($"> {chapterErr}");
                    sb.AppendLine();
                    continue;
                }
                string display = chapter.GetString(Key_DisplayNumber, "");
                if (string.IsNullOrEmpty(display)) display = id;   // display_number 缺 → 由 id 派生
                string timeRange = chapter.GetString(Key_TimeRange, "");
                sb.AppendLine($"### {display}｜{chapter.GetString(Key_Title, "（未命名）")}" +
                              (string.IsNullOrEmpty(timeRange) ? "" : $"　`{timeRange}`"));
                JsonData rounds = chapter.Contains(Key_Rounds) ? chapter[Key_Rounds] : null;
                if (rounds == null || !rounds.IsArray || rounds.Count == 0)
                {
                    sb.AppendLine("（尚無 round）");
                    sb.AppendLine();
                    continue;
                }
                for (int i = 0; i < rounds.Count; i++)
                {
                    JsonData entry = rounds[i];
                    // legacy round 條目可能是純字串檔名（Python 舊格式；library.py 端也容忍）——
                    // 用物件 API 讀字串節點會拿到預設值，round 心得就靜默消失。
                    if (entry != null && entry.IsString)
                    {
                        string legacyFile = entry.GetString();
                        sb.AppendLine($"- **r?**（—）`{legacyFile}`　⚠ legacy 字串條目（無 round/日期欄）");
                        if (fullRounds)
                        {
                            string legacyPath = Path.Combine(dir, legacyFile);
                            sb.AppendLine();
                            sb.AppendLine(File.Exists(legacyPath)
                                ? File.ReadAllText(legacyPath, Encoding.UTF8).TrimEnd()
                                : $"> [!WARNING]\n> 索引指向的 round 檔不存在：`{legacyFile}`");
                            sb.AppendLine();
                        }
                        continue;
                    }
                    string file = entry.GetString(Key_File, "");
                    // ⚠ 場數一定要露出來（TASK-0121 ③）：讀的人要分得出「一話兩場」與「看了兩遍」——
                    //   不印的話，這兩件事在讀回視圖上長得一模一樣，而誤讀不會有任何一層報錯。
                    int segs = entry.GetInt(Key_Segments, 1);
                    sb.AppendLine($"- **r{entry.GetInt(Key_Round, 0)}**（{entry.GetString(Key_ReadingDate, "")}）" +
                                  $"`{file}`" +
                                  (segs > 1 ? $"　▸ 這一輪分 **{segs} 場**寫完（續寫，不是重看）" : "") +
                                  (entry.GetBool(Key_Gap, false) ? "　⚠ gap" : "") +
                                  (entry.Contains(Key_SharedSeq) ? $"　酒館 seq={entry.GetInt(Key_SharedSeq, 0)}" : ""));
                    if (!fullRounds) continue;
                    string roundPath = Path.Combine(dir, file);
                    sb.AppendLine();
                    sb.AppendLine(File.Exists(roundPath)
                        ? File.ReadAllText(roundPath, Encoding.UTF8).TrimEnd()
                        : $"> [!WARNING]\n> 索引指向的 round 檔不存在：`{file}`");
                    sb.AppendLine();
                }
                sb.AppendLine();
            }

            AppendCharacters(sb, mediaId, persona);
            return sb.ToString();
        }

        // ===========================================================
        // 區塊職責：人物段 —— 已確認 facts（profile.json）與主觀 view 的版本史（vN_<date>.md）分開列。
        // 物理意義：續讀時最需要的兩件事是「這人是誰」與「我上次怎麼看他」；**看法要按版本並列**，
        //          因為改觀的演變本身就是閱讀體驗（不覆寫是本 schema 的核心不變量）。
        // 數值影響：純讀；缺 profile / 版本檔一律留 WARNING，不靜默略過。
        // ===========================================================
        static void AppendCharacters(StringBuilder sb, string mediaId, string persona)
        {
            sb.AppendLine("## 🧑 人物（facts ＋ 我的看法版本史）");
            sb.AppendLine();

            string charactersRoot = Path.Combine(ReaderRoot(mediaId, persona), k_CharactersDirName);
            if (!Directory.Exists(charactersRoot))
            {
                sb.AppendLine("（尚無人物紀錄）");
                sb.AppendLine();
                return;
            }

            var characterDirs = new List<string>(Directory.GetDirectories(charactersRoot));
            characterDirs.Sort(StringComparer.Ordinal);
            if (characterDirs.Count == 0)
            {
                sb.AppendLine("（尚無人物紀錄）");
                sb.AppendLine();
                return;
            }

            foreach (string dir in characterDirs)
            {
                string id = Path.GetFileName(dir);
                JsonData profile = LoadJson(Path.Combine(dir, k_ProfileJsonName), out string profileErr);
                string name = profile != null ? profile.GetString(Key_Name, id) : id;
                sb.AppendLine($"### {name}　`{id}`");

                if (profile == null)
                {
                    sb.AppendLine("> [!WARNING]");
                    sb.AppendLine($"> {profileErr}");
                }
                else
                {
                    string nameOriginal = profile.GetString(Key_NameOriginal, "");
                    if (!string.IsNullOrEmpty(nameOriginal)) sb.AppendLine($"- 原文讀音：{nameOriginal}");
                    // facts 有兩種形狀：陣列（Python 時代寫的 legacy corpus）與字串（C# 初版寫的）。
                    // 舊碼用 GetString 讀 —— 對陣列節點回傳預設值 "" → 印「（未登錄）」且無 warning。
                    // 那是一個滿的、寫得很篤定的錯值：讀的人會以為自己真的沒登錄過
                    //（Sirius 2026-08-07 用 dungeon 測資抓到，三個角色全中）。
                    var facts = ReadFactsList(profile);
                    if (facts.Count == 0)
                    {
                        sb.AppendLine("- **已確認 facts**：（未登錄）");
                    }
                    else
                    {
                        sb.AppendLine("- **已確認 facts**：");
                        foreach (var f in facts) sb.AppendLine($"  - {f}");
                    }
                }

                // view 版本史：v1 → vN 依檔名排序並列，**不只印最新版**
                var views = new List<string>(Directory.GetFiles(dir, "v*.md"));
                views.Sort(StringComparer.Ordinal);
                if (views.Count == 0)
                {
                    sb.AppendLine("- （尚無主觀 view 版本）");
                    sb.AppendLine();
                    continue;
                }
                sb.AppendLine();
                foreach (string viewPath in views)
                {
                    sb.AppendLine($"#### {Path.GetFileName(viewPath)}");
                    sb.AppendLine(File.ReadAllText(viewPath, Encoding.UTF8).TrimEnd());
                    sb.AppendLine();
                }
            }
        }

        // 區塊職責：讀 profile.json 的 facts —— 同時吃陣列與字串兩種形狀。
        // 物理意義：legacy corpus（Python 寫的）是 JSON 陣列；C# 初版寫成單一字串。
        //          schema 收斂方向是**陣列**（沿 corpus 多數），字串形狀讀入時按行拆開，
        //          兩種來源在視圖層長一樣 —— 讀端相容、寫端從此只寫陣列（見 AddCharacter）。
        static List<string> ReadFactsList(JsonData profile)
        {
            var o = new List<string>();
            if (profile == null || !profile.Contains(Key_Facts)) return o;
            JsonData f = profile[Key_Facts];
            if (f == null) return o;
            if (f.IsArray)
            {
                for (int i = 0; i < f.Count; i++)
                {
                    string s = f[i]?.GetString() ?? "";
                    if (!string.IsNullOrEmpty(s)) o.Add(s);
                }
            }
            else
            {
                string s = f.GetString();
                if (!string.IsNullOrEmpty(s))
                {
                    foreach (var line in s.Split('\n'))
                    {
                        string t = line.Trim();
                        if (t.Length > 0) o.Add(t);
                    }
                }
            }
            return o;
        }

        // facts 寫入端的唯一出口：一律寫**陣列**（多行輸入按行拆）。
        // 字串與陣列兩種寫法並存就是這次假滿值 bug 的土壤 —— 寫端收斂成一種。
        static JsonData FactsToJson(string facts)
        {
            var arr = new JsonData().ToArray();
            if (!string.IsNullOrEmpty(facts))
            {
                foreach (var line in facts.Split('\n'))
                {
                    string t = line.Trim();
                    if (t.Length > 0) arr.Add(t);
                }
            }
            return arr;
        }

        // ===========================================================
        // 區塊職責：把追回檔寫進該 persona 自己的 letters/cmd/ —— 與其他 Cmd 回傳檔同一個家。
        // 物理意義：落點 `cmd/reading_recall_<media-id>.md`，走 UCL_LettersPath（版面唯一實作，
        //          Plan_Letters_Dir_Layout §8.2 批次③）。原本平鋪在 letters 頂層，
        //          與人寫的信混住 —— 那正是 Cmd_DocEdit「找最新那封信」抓到機器產物的病灶。
        // 數值影響：每次完整覆寫；原始章節與人物歷史不受影響。回傳寫出的絕對路徑。
        // ===========================================================
        public static string WriteRecallBrief(string mediaId, string persona, bool fullRounds, out string error)
        {
            string text = RenderRecall(mediaId, persona, fullRounds, out error);
            if (text == null) return null;
            string path = UCL_LettersPath.CmdPayload(persona, "reading_recall", mediaId);
            UCL_LettersPath.EnsurePayloadDir(path);   // 建目錄＋補 cmd/.gitignore（唯一入口）
            SaveText(path, text);
            return path;
        }

        /// <summary>把某筆 round 的酒館 seq 寫回索引 —— 「已發文」的可驗證 receipt。</summary>
        // ===========================================================
        // 區塊職責：讀「已遷移 Archive」集合 —— _migration/registry.json 是唯一標記處。
        // 物理意義：**Archive 不可修改**（Tim 鐵律），所以「已遷移」不寫進 Archive 本身，
        //          寫在 registry（state=migrated 的 record）。讀取端（管理頁 / op=scan）
        //          預設隱藏這個集合裡的 slug —— 已裁決過的東西不該每次都端回檯面。
        // 數值影響：唯讀；registry 缺檔 / 壞檔 → 空集合（fail-open：寧可多列不可少列）。
        // ===========================================================
        public static HashSet<string> LoadMigratedArchiveSlugs()
        {
            var o = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string path = Path.Combine(BookNotesRoot, "_migration", "registry.json");
            JsonData reg = LoadJson(path, out _);
            if (reg == null || !reg.Contains("records")) return o;
            JsonData records = reg["records"];
            if (records == null || !records.IsArray) return o;
            const string prefix = "BookNotes/Archive/";
            for (int i = 0; i < records.Count; i++)
            {
                JsonData r = records[i];
                if (r == null || !r.IsObject) continue;
                if (r.GetString("state", "") != "migrated") continue;
                string src = r.GetString("source_id", "");
                if (src.StartsWith(prefix, StringComparison.Ordinal))
                {
                    o.Add(src.Substring(prefix.Length).Trim().TrimEnd('/'));
                }
            }
            return o;
        }

        // ===========================================================
        // 區塊職責：op=scan —— Library / Archive 的重複與異常候選審計（唯讀）。
        // 物理意義：Q4 定案「scan 先印候選、人工核對」—— 本方法**不合併不搬移不改任何檔**，
        //          只產一份給人裁決的清單。判準沿 Plan_Library_Media_Migration 的實測教訓：
        //          前綴法誤報 60%、title 法漏一半 → 用 normalize 撒網、人工收網。
        // 數值影響：唯一的寫入是報告檔 BookNotes/_migration/scan_report.md（機械產物，
        //          每次覆寫）；資料層一個位元組都不動。
        // 掃四類：
        //   A. Archive ↔ Library 疑似同作品（slug / title / title_original / aliases normalize 命中）
        //   B. Library 內部疑似重複（同 normalize title 但**不同 work_id** —— 同 work 多 media 是
        //      設計上的合法形狀，不列）
        //   C. reader 異常：資料夾名 unknown / 缺 reader.json / reader_persona 與資料夾名不一致
        //      （含大小寫不一致 —— NTFS 遮著它，Linux 上會把追回檔寫到版控外）
        //   D. Archive 讀不到 metadata 的 entry（book.json 缺或壞 —— 連被比對的資格都沒有，要人看）
        // ===========================================================
        public static string ScanLibrary(out string reportPath, out string error, bool showMigrated = false)
        {
            error = null;
            reportPath = null;
            var sb = new StringBuilder();
            var mediaEntries = ListMediaEntries();
            // 已遷移的 Archive 預設不進候選（Tim 2026-08-07：已裁決過的不重複端上檯面；
            // 要查帶 --arg show_migrated=true）。隱藏數量必須印出來 —— 靜默隱藏＝下一隻閘門讀快取。
            var migrated = LoadMigratedArchiveSlugs();
            int hiddenMigrated = 0;

            // media 的 normalize 鍵集合（title / mediaId 去前綴 / work_id / aliases）
            var mediaKeys = new List<(MediaEntry entry, HashSet<string> keys)>();
            foreach (var m in mediaEntries)
            {
                var keys = new HashSet<string>();
                AddKey(keys, m.Title);
                AddKey(keys, m.WorkId);
                int dash = m.MediaId.IndexOf('-');
                AddKey(keys, dash > 0 ? m.MediaId.Substring(dash + 1) : m.MediaId);
                JsonData work = string.IsNullOrEmpty(m.WorkId) ? null
                    : LoadJson(Path.Combine(WorkRoot(m.WorkId), k_WorkJsonName), out _);
                if (work != null)
                {
                    AddKey(keys, work.GetString(Key_TitleOriginal, ""));
                    JsonData aliases = work.Contains(Key_Aliases) ? work[Key_Aliases] : null;
                    if (aliases != null && aliases.IsArray)
                    {
                        for (int i = 0; i < aliases.Count; i++) AddKey(keys, AliasToString(aliases[i]));
                    }
                }
                mediaKeys.Add((m, keys));
            }

            sb.AppendLine("---");
            sb.AppendLine("type: library_scan_report");
            sb.AppendLine($"generated_at: {DateTime.Now:yyyy-MM-ddTHH:mm:sszzz}");
            sb.AppendLine("generated: mechanical   # 每次 op=scan 覆寫；本工具唯讀，遷移一律人工");
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("# 🔍 Library 審計報告（op=scan）");
            sb.AppendLine();
            sb.AppendLine($"- Library media：{mediaEntries.Count} 個");

            // ── A + D：Archive 比對 ──
            string archiveRoot = Path.Combine(BookNotesRoot, "Archive");
            int archiveCount = 0, hitCount = 0;
            var sectionA = new StringBuilder();
            var sectionD = new StringBuilder();
            if (Directory.Exists(archiveRoot))
            {
                foreach (string dir in Directory.GetDirectories(archiveRoot))
                {
                    string slug = Path.GetFileName(dir);
                    // `_` 開頭是系統目錄（_recommended / _search_reports…），不是書 —— 不進統計也不進 D 節
                    if (slug.StartsWith("_", StringComparison.Ordinal)) continue;
                    if (!showMigrated && migrated.Contains(slug)) { hiddenMigrated++; continue; }
                    archiveCount++;
                    JsonData book = LoadJson(Path.Combine(dir, "book.json"), out string bookErr);
                    if (book == null)
                    {
                        sectionD.AppendLine($"- `{slug}`：{bookErr}");
                        continue;
                    }
                    string title = book.GetString(Key_Title, "");
                    string titleOriginal = book.GetString(Key_TitleOriginal, "");
                    var archiveKeys = new HashSet<string>();
                    AddKey(archiveKeys, slug);
                    AddKey(archiveKeys, title);
                    AddKey(archiveKeys, titleOriginal);
                    foreach (var (m, keys) in mediaKeys)
                    {
                        bool hit = false;
                        foreach (var k in archiveKeys)
                        {
                            if (keys.Contains(k)) { hit = true; break; }
                        }
                        if (!hit) continue;
                        hitCount++;
                        sectionA.AppendLine($"- Archive `{slug}`（{title}） ↔ Library `{m.MediaId}`（{m.Title}）" +
                                            $"　readers: {string.Join(", ", m.Readers)}");
                    }
                }
            }
            sb.AppendLine($"- Archive entry：{archiveCount} 個"
                          + (hiddenMigrated > 0
                              ? $"（另 {hiddenMigrated} 筆已遷移預設隱藏 —— `--arg show_migrated=true` 顯示）"
                              : ""));
            sb.AppendLine();
            sb.AppendLine($"## A. Archive ↔ Library 疑似同作品（{hitCount} 組 —— 逐組人工裁決，不自動遷移）");
            sb.AppendLine();
            sb.Append(sectionA.Length > 0 ? sectionA.ToString() : "（無命中）\n");
            sb.AppendLine();

            // ── B：Library 內部疑似重複（同 normalize title、不同 work_id）──
            sb.AppendLine("## B. Library 內部疑似重複（同名但不同 work_id —— arakawa 型爛帳的形狀）");
            sb.AppendLine();
            var byTitle = new Dictionary<string, List<MediaEntry>>();
            foreach (var m in mediaEntries)
            {
                string k = Normalize(m.Title);
                if (k.Length == 0) continue;
                if (!byTitle.TryGetValue(k, out var list)) byTitle[k] = list = new List<MediaEntry>();
                list.Add(m);
            }
            int dupGroups = 0;
            foreach (var kv in byTitle)
            {
                var workIds = new HashSet<string>();
                foreach (var m in kv.Value) workIds.Add(m.WorkId);
                if (kv.Value.Count < 2 || workIds.Count < 2) continue;   // 同 work 多 media 合法
                dupGroups++;
                sb.AppendLine($"- 「{kv.Value[0].Title}」：" +
                              string.Join(" / ", kv.Value.ConvertAll(m => $"`{m.MediaId}`(work={m.WorkId})")));
            }
            if (dupGroups == 0) sb.AppendLine("（無命中）");
            sb.AppendLine();

            // ── C：reader 異常 ──
            sb.AppendLine("## C. reader 異常（unknown / 缺 reader.json / persona 與資料夾名不一致）");
            sb.AppendLine();
            int anomalies = 0;
            foreach (var m in mediaEntries)
            {
                foreach (string reader in m.Readers)
                {
                    string readerJson = ReaderJsonPath(m.MediaId, reader);
                    if (reader == "unknown")
                    {
                        anomalies++;
                        sb.AppendLine($"- `{m.MediaId}/readers/unknown`：persona 解析失敗的 fallback 產物 —— " +
                                      "逐檔認領或併入正主，不可當真讀者");
                        continue;
                    }
                    if (!File.Exists(readerJson))
                    {
                        anomalies++;
                        sb.AppendLine($"- `{m.MediaId}/readers/{reader}`：缺 reader.json");
                        continue;
                    }
                    JsonData reader0 = LoadJson(readerJson, out _);
                    string declared = reader0 != null ? reader0.GetString(Key_ReaderPersona, "") : "";
                    if (declared != reader)
                    {
                        anomalies++;
                        bool caseOnly = string.Equals(declared, reader, StringComparison.OrdinalIgnoreCase);
                        sb.AppendLine($"- `{m.MediaId}/readers/{reader}`：reader.json 宣告 `{declared}`" +
                                      (caseOnly ? "（**大小寫不一致** —— NTFS 遮著，Linux 上追回檔會寫進版控外的 letters/）"
                                          : "（宣告與路徑不同人）"));
                    }
                }
            }
            if (anomalies == 0) sb.AppendLine("（無異常）");
            sb.AppendLine();
            if (sectionD.Length > 0)
            {
                sb.AppendLine("## D. Archive metadata 讀不到（連被比對的資格都沒有 —— 要人看）");
                sb.AppendLine();
                sb.Append(sectionD);
                sb.AppendLine();
            }
            sb.AppendLine("> 本報告唯讀生成；**任何合併 / 搬移 / 改名都不由工具代辦**（Q3 定案：偵測自動、遷移人工）。");

            string report = sb.ToString();
            try
            {
                string dir = Path.Combine(BookNotesRoot, "_migration");
                Directory.CreateDirectory(dir);
                reportPath = Path.Combine(dir, "scan_report.md");
                File.WriteAllText(reportPath, report, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                // 報告落檔失敗不吞掉輸出 —— 印出來的那份還在
                error = $"報告檔寫出失敗（內容仍在輸出中）：{ex.Message}";
                reportPath = null;
            }
            return report;
        }

        static void AddKey(HashSet<string> keys, string raw)
        {
            string k = Normalize(raw);
            if (k.Length > 0) keys.Add(k);
        }

        // 區塊職責：alias 條目轉字串 —— aliases 也有兩形狀（facts 同族病，2026-08-07 scan 實測抓到）：
        // mononoke 是字串陣列、arakawa 是物件陣列（{slug,source,note} / {title,note}）。
        // GetString 對物件回空字串 → 物件形狀的 alias 被靜默跳過。
        static string AliasToString(JsonData alias)
        {
            if (alias == null) return "";
            if (alias.IsObject)
            {
                string t = alias.GetString(Key_Title, "");
                if (string.IsNullOrEmpty(t)) t = alias.GetString("slug", "");
                return t;
            }
            return alias.GetString();
        }

        // normalize：小寫 + 只留字母數字（含 CJK）—— 標點、空白、連字號全掃掉。
        // 用途是撒網不是判定：normalize 相等 = 候選，不 = 同作品（人工收網）。
        static string Normalize(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            var sb = new StringBuilder();
            foreach (char c in raw.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            }
            return sb.ToString();
        }

        // ===========================================================
        // 區塊職責：組一則「章節心得 → 酒館」的發文內文（op=share 用）。
        // 物理意義：round 檔是事實源，酒館貼文是投影 —— 本方法只讀不寫；
        //          發文成敗都不回滾心得檔（檔優先於投影，basecamp 2026-08-06 定案）。
        // 數值影響：roundNumber<=0 → 取該章最大 round 並回填；已有 shared_seq 的 round
        //          直接拒絕（同一則心得重發會重複計酬，與 commit 同 SHA 重貼同型）。
        // ===========================================================
        public static string BuildShareBody(string mediaId, string persona, string chapterId,
                                            ref int roundNumber, out string error)
        {
            error = null;
            if (LoadReader(mediaId, persona, out error) == null) return null;
            string chapterDir = ChapterDir(mediaId, persona, chapterId);
            JsonData chapter = LoadJson(Path.Combine(chapterDir, k_ChapterJsonName), out error);
            if (chapter == null) return null;
            JsonData rounds = chapter.Contains(Key_Rounds) ? chapter[Key_Rounds] : null;
            if (rounds == null || !rounds.IsArray || rounds.Count == 0)
            {
                error = "chapter.json 缺 rounds —— 先 note_chapter 再 share";
                return null;
            }
            JsonData hit = null;
            JsonData maxEntry = null;
            int maxRound = 0;
            for (int i = 0; i < rounds.Count; i++)
            {
                JsonData entry = rounds[i];
                if (entry == null || entry.IsString) continue;   // legacy 字串條目沒有 round 號可對
                int rn = entry.GetInt(Key_Round, 0);
                if (rn > maxRound) { maxRound = rn; maxEntry = entry; }
                if (roundNumber > 0 && rn == roundNumber) hit = entry;
            }
            if (roundNumber <= 0) { hit = maxEntry; roundNumber = maxRound; }
            if (hit == null)
            {
                error = $"找不到 round {roundNumber}（該章最大 round = {maxRound}）";
                return null;
            }
            if (hit.Contains(Key_SharedSeq))
            {
                error = $"round {roundNumber} 已發過（seq={hit.GetInt(Key_SharedSeq, 0)}）—— " +
                        "重發會重複領發文計酬；真要重發請先人工清掉該 round 的 shared_seq";
                return null;
            }
            string file = hit.GetString(Key_File, "");
            string roundPath = Path.Combine(chapterDir, file);
            if (!File.Exists(roundPath))
            {
                error = $"索引指向的 round 檔不存在：{file}";
                return null;
            }
            string content = StripFrontmatter(File.ReadAllText(roundPath, Encoding.UTF8)).Trim();

            // 標頭：作品名（media → work 兩跳，缺檔就退回 mediaId，不因標頭缺料擋分享）
            string workTitle = mediaId;
            JsonData media = LoadJson(Path.Combine(MediaRoot(mediaId), k_MediaJsonName), out _);
            if (media != null)
            {
                string workId = media.GetString(Key_WorkId, "");
                JsonData work = string.IsNullOrEmpty(workId) ? null
                    : LoadJson(Path.Combine(WorkRoot(workId), k_WorkJsonName), out _);
                if (work != null) workTitle = work.GetString(Key_Title, mediaId);
            }
            string display = chapter.GetString(Key_DisplayNumber, "");
            if (string.IsNullOrEmpty(display)) display = chapterId;
            string chapterTitle = chapter.GetString(Key_Title, "");

            return $"📖 **閱讀心得｜{workTitle}** {display}" +
                   (string.IsNullOrEmpty(chapterTitle) ? "" : $"｜{chapterTitle}") +
                   $"　(r{roundNumber} by {persona})\n\n{content}";
        }

        // frontmatter 只認「檔案開頭」的 --- 區塊 —— 內文中的 hr 不受影響。
        static string StripFrontmatter(string text)
        {
            if (string.IsNullOrEmpty(text) || !text.StartsWith("---")) return text;
            int end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (end < 0) return text;
            int lineEnd = text.IndexOf('\n', end + 1);
            return lineEnd < 0 ? "" : text.Substring(lineEnd + 1);
        }

        public static void RecordSharedSeq(string mediaId, string persona, string chapterId,
                                           int roundNumber, int seq, out string error)
        {
            string chapterJsonPath = Path.Combine(ChapterDir(mediaId, persona, chapterId), k_ChapterJsonName);
            JsonData chapter = LoadJson(chapterJsonPath, out error);
            if (chapter == null) return;
            JsonData rounds = chapter.Contains(Key_Rounds) ? chapter[Key_Rounds] : null;
            if (rounds == null || !rounds.IsArray) { error = "chapter.json 缺 rounds"; return; }
            for (int i = 0; i < rounds.Count; i++)
            {
                if (rounds[i].GetInt(Key_Round, 0) != roundNumber) continue;
                rounds[i][Key_SharedSeq] = seq;
                SaveJson(chapterJsonPath, chapter);
                return;
            }
            error = $"chapter.json 找不到 round {roundNumber}，seq={seq} 未落 receipt";
        }
    }
}
#endif
