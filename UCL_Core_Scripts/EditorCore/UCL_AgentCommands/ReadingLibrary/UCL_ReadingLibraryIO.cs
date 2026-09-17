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
        public static string[] MediaKinds => SCP.Core.Library.SCP_LibraryIO.MediaKinds;   // ⤷ 薄殼（TASK-0166 ①）

        static readonly Regex k_IdPattern = new Regex(@"^[A-Za-z0-9][A-Za-z0-9_-]*$");
        static readonly Regex k_ChapterIdPattern = new Regex(@"^\d{4}$");

        /// <summary>序章保留號；非必有，且排除在章節連續性判定之外。</summary>
        public const string PrologueChapterId = "0000";

        // ===========================================================
        // 路徑層
        // 物理意義：一律由 UCL_RepoPath.AgentCommandsDir 推導，不寫死 UCL_Core 安裝路徑。
        // ===========================================================
        public static string BookNotesRoot => Path.Combine(UCL_RepoPath.AgentCommandsDir, k_BookNotesDirName);
        public static string LibraryRoot => SCP.Core.Library.SCP_LibraryStore.LibraryRoot(UCL_AgentCommandsPath.DataRoot);
        public static string MediaRoot(string mediaId) => SCP.Core.Library.SCP_LibraryStore.MediaRoot(UCL_AgentCommandsPath.DataRoot, mediaId);
        public static string WorkRoot(string workId) => SCP.Core.Library.SCP_LibraryStore.WorkRoot(UCL_AgentCommandsPath.DataRoot, workId);

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

        /// <summary>
        /// 正文容器的**存在性讀數** —— ⚠ 回三態而不是回檔數（TASK-0146 ② 的反向對照）。
        /// 🩸 現行讀取端數章數用的是「目錄不存在就回 0」，
        ///   ⇒ **容器放錯位置 ＝ 靜默 0**，而 ④ 指定的第一本正好真的是 0 章
        ///   ⇒ 兩者逐位元組同形，**搬壞了會長得像搬對了**。
        ///   （@gura 開單時那句「內容為空反而讓『搬壞了』更難被看見」的機制版本。）
        /// ⇒ 所以這裡把「沒有這個目錄」與「有目錄但裡面 0 個」做成**兩個不同的值**，
        ///   ⛔ 不留一個 0 讓讀的人去猜是哪一種 —— 人往空格裡填的一定是成功。
        /// </summary>

        /// <summary>把寫書線四欄寫上既有的 work.json（**只寫非空的那幾欄**）。work.json 不存在 ⇒ 不建、回 false。</summary>

        // ===========================================================
        // 區塊職責：建 work.json（不存在才建；已存在一律不覆寫）—— `MediaInit` 與 ④ 搬遷**共用這一份**
        // 物理意義：兩條路都會需要「這部作品在新 store 有一份 work.json」，而它們建出來的
        //          schema 必須逐欄相同 —— 各寫一份的話，兩種來源的 work.json 會慢慢長歪，
        //          🩸 而那個漂移**不會報錯**：讀取端 `GetString(key, "")` 對缺欄回空字串，
        //          於是「這本沒填」與「這條路徑沒寫這欄」同形。
        // 數值影響：檔案不存在 ⇒ 建一份；存在 ⇒ 一個位元組都不動（⛔ 不補欄、不升版）。
        // ===========================================================

        /// <summary>讀回寫書線四欄（③ 逐欄對拍的讀取側）。work.json 不存在或解析不動 ⇒ 回 false，⛔ 不回一個空物件假裝讀到了。</summary>

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

        /// <summary>舊 store 的草稿檔位置（`origin`／`author_persona`／`status`／`publish_status` 的事實源）。</summary>

        /// <summary>③ 逐欄對拍。⛔ 純讀；`report` 是給人看的逐欄表，`mismatchedFields` 是給程式判的欄位名。</summary>

        /// <summary>空字串要看得出是空的 —— ⛔ 不印成空白格（空白格與「我沒讀那一欄」同形）。</summary>

        /// <summary>
        /// 正文容器對拍 —— ⭐ 這是 `ProbeWorkProse` 三態的**第一個讀取端**（在此之前它存在而未生效）。
        /// 🩸 @gura《深海對拍錄》舊 store 的 `chapters/` 是**目錄存在、0 個檔**，而 `arcs/` 有 1 個
        ///   ⇒ 「還沒寫」與「搬掉了」在數章數那條路上同形，所以這裡印的是三態的名字，不是檔數。
        /// ⚠ 舊 store 的相對版面與新 store 相同（`&lt;book&gt;/chapters`、`&lt;book&gt;/arcs`），
        ///   所以這裡沿用同兩個目錄名常數 —— ⛔ 不另立一套舊版面的名字。
        /// </summary>

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


        /// <summary>
        /// 複製一個正文容器（⛔ 不移動、⛔ 不覆寫既有同名檔）。
        /// <para>⚠ 來源目錄不存在 ⇒ 回一行「NoDir，無事可搬」而**不建空目錄** ——
        /// 建了的話新側會從 `NoDir` 變成 `EmptyDir`，而那正是 ② 要分開的兩個值。</para>
        /// </summary>

        /// <param name="missing">
        /// 只在「**舊 store 有檔而新 store 沒有**」時為 true —— 那是唯一會靜默丟內容的那一種。
        /// ⛔ 舊 store 是空目錄或沒有容器時**不算遺漏**（那兩種搬過去沒有東西會不見）。
        /// </param>


        public static string ReaderRoot(string mediaId, string persona)
            => Path.Combine(MediaRoot(mediaId), k_ReadersDirName, persona);
        public static string ReaderJsonPath(string mediaId, string persona)
            => Path.Combine(ReaderRoot(mediaId, persona), k_ReaderJsonName);
        public static string ChapterDir(string mediaId, string persona, string chapterId)
            => Path.Combine(ReaderRoot(mediaId, persona), k_ChaptersDirName, chapterId);

        public static bool IsValidId(string value) => SCP.Core.Library.SCP_LibraryStore.IsValidId(value);
        public static bool IsValidChapterId(string value)
            => !string.IsNullOrEmpty(value) && k_ChapterIdPattern.IsMatch(value);

        /// <summary>列出目前所有 media id（供頁面下拉與 Cmd 驗證用）。</summary>

        /// <summary>
        /// 區塊職責：全 Library 的 media 總表（瀏覽下拉與 scan 共用）。
        /// 物理意義：一筆 = media.json + work.json title + readers 目錄名 —— 只讀 metadata，
        ///          不碰章節正文；title 缺檔退回 mediaId（瀏覽不因缺料斷掉，缺料是 scan 的事）。
        /// </summary>


        /// <summary>列出某 media 底下的 reader persona（同一部作品可有多位讀者各自一份紀錄）。</summary>

        // ===========================================================
        // 外部漫畫庫 (External Comics)
        // 物理意義：本機外部實體漫畫資料夾（例如 D:\commic、D:\manga）的設定與探索。
        //          真相源是 UCL_ProjectEditorPrefs（不上 git、per-project 隔離），
        //          同步輸出 .comic_root.local 快照檔給 Python 唯讀消費（write-on-change，Python 不刪檔自癒）。
        // ===========================================================
        public const string PrefKey_ComicRootPath = "UCL_Library.ComicRootPath";
        public const string ComicRootSnapshotFileName = ".comic_root.local";




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

        /// <summary>將作品系列名轉換為標準 slug（例 "Hunter x Hunter" -> "hunter-x-hunter"）。</summary>

        /// <summary>
        /// 區塊職責：掃描外部漫畫庫目錄，將所有漫畫作品聚合為系列清單，並與 Library 既有 Media 進行三態匹配。
        /// 物理意義：不每幀走目錄樹；只在載入或使用者手動重新整理時呼叫。
        /// </summary>


        // ===========================================================
        // JSON 讀寫
        // 物理意義：讀壞掉的檔不靜默回空物件 —— 那會讓下一次寫入把壞檔覆蓋成「乾淨」，
        //          原始資料連救都救不回來。壞檔一律讓 caller 收到 error。
        // ===========================================================

        /// <summary>寫 JSON（UTF-8 無 BOM，beautify，非 ASCII 還原成原生字元）。父目錄自動建立。</summary>

        // ===========================================================
        // 區塊職責：把 ToJsonBeautify 產生的 \uXXXX 逃脫還原成原生字元（僅非 ASCII）。
        // 物理意義：既有檔（Python 端寫的）是原生 UTF-8 中文；C# 端若寫成 u-escape 形式，
        //          語意雖相同，但 **git diff 變成不可讀、grep 找不到書名、人也讀不了** ——
        //          同一份 schema 兩種寫法就是漂移的起點（2026-08-06 實測抓到）。
        // 數值影響：只還原 code point > 0x7F 的逃脫；ASCII 控制字元區間與 quote / backslash /
        //          等既有逃脫一律不動，維持 JSON 合法性。
        // ===========================================================
        static readonly Regex k_UnicodeEscape = new Regex(@"\\u([0-9a-fA-F]{4})");



        public static string Today() => DateTime.Now.ToString("yyyy-MM-dd");

        /// <summary>
        /// 區塊職責：組 JSON 字串陣列 —— 去重（保序）、去空白、把附帶的必含值一起收進來。
        /// 物理意義：aliases 一定要含 title / title_original 自己 —— 否則「用正式名搜尋卻搜不到」。
        /// 數值影響：純資料整理；大小寫不做正規化（搜尋端不分大小寫比對，這裡保留原字面）。
        /// </summary>

        /// <summary>把 `a|b|c` 或 `a,b,c` 切成清單（別名含逗號的情況用 `|`）。</summary>

        // ===========================================================
        // reader.json 讀取 + 身分校驗
        // 物理意義：路徑上的 <persona> 與檔內 reader_persona 不符 = 資料放錯讀者根目錄，
        //          那是「替別人代筆閱讀史」的前一步，必須擋。
        // ===========================================================

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

        // ===========================================================
        // 章節連續性分類（Tim 2026-08-06 拍板後的語意：分類，不是閘門）
        // 物理意義：有自己的紀錄就直接讀回並放行；分類只是為了在回報與 chapter.json 留下痕跡，
        //          讓「跳章」不會靜默變成一份看起來連續的閱讀史。
        // 數值影響：不擋任何寫入；0000 序章不參與連續性判定（它非必有）。
        // ===========================================================

        // ===========================================================
        // 區塊職責：**建 reader.json（不存在才建）** —— `MediaInit` 與 `RegisterReader` 共用這一份。
        // 物理意義：reader.json 是「這個人在看這部」這件事的落檔，schema 只准有一種形狀。
        // 數值影響：已存在 ⇒ **零寫入**（既有進度一個位元組都不動），`oCreated=false`。
        // 🩸 為什麼抽出來：TASK-0137 要在進場時也能登記，而「再寫一次同樣的初值」＝ 第二份 schema。
        //   兩份初值長得一樣時不會有人發現它們已經分岔（少一個欄位的 reader 讀回來也「正常」）。
        // ===========================================================
        static string EnsureReaderJson(string mediaId, string persona, int anticipation, out bool oCreated)
        {
            // ⤷ **薄殼**（TASK-0166 ①）：實作住 `SCP.Core.Library.SCP_LibraryInit.EnsureReaderJson`
            //   —— 含「建完必定同步閱讀卡」那一步（那不是順手加的，見 SCP 側的檔頭血證）。
            return SCP.Core.Library.SCP_LibraryInit.EnsureReaderJson(
                new SCP.Core.Paths.SCP_LettersRoot(UCL_LettersPath.Root), UCL_AgentCommandsPath.DataRoot, mediaId, persona, anticipation, out oCreated);
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
            // ⤷ **薄殼**（TASK-0166 ①）：實作住 `SCP.Core.Library.SCP_LibraryInit.RegisterReader`。
            bool aOk = SCP.Core.Library.SCP_LibraryInit.RegisterReader(
                new SCP.Core.Paths.SCP_LettersRoot(UCL_LettersPath.Root), UCL_AgentCommandsPath.DataRoot, mediaId, persona, out oCreated, out oLog, out string aErr);
            error = aErr;
            return aOk;
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
        // ⤷ **薄殼**（TASK-0166 ①）：實作住 `SCP.Core.Library.SCP_LibraryNote.NoteChapter`
        //   —— round md 是事實源、chapter.json 是索引、reader.json 是當前狀態，三者的順序與
        //   「既有 round 絕不覆寫」那條不變量都在那一層，⛔ 這裡不重述也不補判斷（第二把尺會分岔）。
        public static string NoteChapter(string mediaId, string persona, string chapterId,
                                         string displayNumber, string chapterTitle, string timeRange,
                                         string body, string impression, string bookmarkNote,
                                         bool append, int appendRound,
                                         out string roundFilePath, out int roundNumber, out string error)
        {
            string aLog = SCP.Core.Library.SCP_LibraryNote.NoteChapter(
                new SCP.Core.Paths.SCP_LettersRoot(UCL_LettersPath.Root), UCL_AgentCommandsPath.DataRoot, mediaId, persona, chapterId,
                displayNumber, chapterTitle, timeRange, body, impression, bookmarkNote,
                append, appendRound, out string aRoundPath, out roundNumber, out string aErr);
            roundFilePath = aRoundPath;
            error = aErr;
            return aLog;
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
            // ⤷ **薄殼**（TASK-0166 ①）：實作住 `SCP_LibraryCharacter.AddCharacter`
            //   （含「人物已存在就拒絕、指去 revise_view」那道閘 —— 覆寫 v1 會抹掉當時的「還不知道」）。
            string aLog = SCP.Core.Library.SCP_LibraryCharacter.AddCharacter(
                new SCP.Core.Paths.SCP_LettersRoot(UCL_LettersPath.Root), UCL_AgentCommandsPath.DataRoot, mediaId, persona, characterId, name, nameOriginal, facts, view, out string aErr);
            error = aErr;
            return aLog;
        }

        /// <summary>
        /// 改觀 → fork 下一版 view（永不覆寫）。可同時補客觀 facts（那是可更新的已確認資料）。
        /// </summary>
        public static string ReviseView(string mediaId, string persona, string characterId,
                                        string view, string changeReason, string facts,
                                        out string error)
        {
            // ⤷ **薄殼**（TASK-0166 ①）：實作住 `SCP_LibraryCharacter.ReviseView`（fork 新版本，**永不覆寫**）。
            string aLog = SCP.Core.Library.SCP_LibraryCharacter.ReviseView(
                new SCP.Core.Paths.SCP_LettersRoot(UCL_LettersPath.Root), UCL_AgentCommandsPath.DataRoot, mediaId, persona, characterId, view, changeReason, facts, out string aErr);
            error = aErr;
            return aLog;
        }


        /// <summary>只更新書籤與當前看法（op=bookmark）。</summary>
        public static string Bookmark(string mediaId, string persona, string note, string impression,
                                      string status, out string error)
        {
            // ⤷ **薄殼**（TASK-0166 ①）：實作住 `SCP_LibraryCharacter.Bookmark`。
            string aLog = SCP.Core.Library.SCP_LibraryCharacter.Bookmark(
                new SCP.Core.Paths.SCP_LettersRoot(UCL_LettersPath.Root), UCL_AgentCommandsPath.DataRoot, mediaId, persona, note, impression, status, out string aErr);
            error = aErr;
            return aLog;
        }

        // ===========================================================
        // bookshelf.md 同步
        // 物理意義：**投影，不是第二真相源** —— 一律由 reader.json 重新生成，人不手改。
        // ===========================================================
        // ⤷ **薄殼**（TASK-0166 ①）：閱讀卡的渲染與落檔住 `SCP_LibraryBookshelf.SyncBookshelf`。
        // ⚠ 兩層唯一的行為差異是**轉發失敗那句警告從哪裡出來**：SCP_Core 叫不到 Unity ⇒ 它把警告
        //   交回呼叫端（`oForwardWarning`），由這裡印 —— 產物本身逐位元組不變，方向也沒變
        //   （正本先寫、副本後寫、轉發失敗不連累正本）。
        public static void SyncBookshelf(string mediaId, string persona, out string error)
        {
            SCP.Core.Library.SCP_LibraryBookshelf.SyncBookshelf(
                new SCP.Core.Paths.SCP_LettersRoot(UCL_LettersPath.Root), UCL_AgentCommandsPath.DataRoot,
                mediaId, persona, out string aErr, out string aForwardWarning);
            error = aErr;
            if (!string.IsNullOrEmpty(aForwardWarning))
                Debug.LogWarning($"[ReadingLibrary] bookshelf 轉發至 letters 失敗（正本已寫入，不影響資料）：{aForwardWarning}");
        }



        // ===========================================================
        // 讀回（recall）—— 頁面與 Cmd 共用同一段
        // 物理意義：把一位 persona 在一個 media 的累積紀錄組成單一可讀視圖。
        // 數值影響：純讀；缺檔 / 壞檔一律在輸出裡留 WARNING，不靜默略過。
        // ===========================================================
        // ⤷ **薄殼**（TASK-0166 ①）：渲染實作住 `SCP_LibraryRecall.RenderRecall`，本函式只解析根。
        // 物理意義：這裡原本有一份 174 行的渲染（AppendCharacters／ReadFactsList 一起）——
        //   跟 SCP_Core 那份**並存**。兩份都活著時「它們輸出一樣嗎」每天都要重問一次，
        //   而它們分岔的樣子是**兩邊各自跑得動、輸出差一點點**（2026-09-16 A/B：231 行逐行相同、行尾不同）。
        // ⇒ 薄殼之後那個問題消失，不是變好答 —— 只剩一份實作。
        // ⚠ 射程：本函式的**簽名不動**（Cmd_Library 與管理頁都吃它）⇒ 呼叫端零改動。
        public static string RenderRecall(string mediaId, string persona, bool fullRounds, out string error)
        {
            string aText = SCP.Core.Library.SCP_LibraryRecall.RenderRecall(
                UCL_AgentCommandsPath.DataRoot, mediaId, persona, fullRounds, out string aErr);
            error = aErr;
            return aText;
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
            UCL_LettersPath.EnsurePayloadDir(path);   // 建目錄＋補 cmd/.gitignore（唯一入口；SCP 側沒有這一格）
            // ⤷ 寫入改走 SCP 側（`WriteCrLf`）—— TASK-0166 ①。
            // ⚠ 這**會改變落盤的行尾**：本端原本用 `SaveText` 原樣寫出（正文帶 LF ⇒ 混合行尾），
            //   Senate 入口一直是全 CRLF。2026-09-16 A/B 量到的 14206 vs 14306（差 100 bytes）就是這一格。
            //   ⇒ 收斂成一種是**刻意的**：③ 要求的「同輸入兩邊輸出逐位元組對拍」在兩個寫入端各寫各的時不可能成立。
            SCP.Core.Library.SCP_LibraryIO.SaveText(path, text);
            return path;
        }
        // ===========================================================
        // ⤷ **薄殼區**（TASK-0166 ①，Tim 2026-09-17 拍板「Editor-only 那批全部搬進 SCP_Core」）
        // 區塊職責：把 Editor 這一側的**舊簽名**接到 SCP_Core 的實作上 —— 呼叫端零改動（型別除外）。
        // ⚠ 這裡**不留任何判斷**：多一行 if 就是第二把尺，而兩把尺分岔時兩邊都不會報錯。
        //   唯一留在本層的是那些**只有 Unity 知道**的東西（EditorPrefs 的漫畫庫根、letters 版面）。
        // ===========================================================
        static string DataRoot => UCL_AgentCommandsPath.DataRoot;
        static SCP.Core.Paths.SCP_LettersRoot LettersRootOf() => new SCP.Core.Paths.SCP_LettersRoot(UCL_LettersPath.Root);

        // ── 讀取／JSON 層也收進 SCP（TASK-0166 ① 第二刀）────────────────────
        // ⚠ 型別跟著換：這幾支回的是 `SCP_JsonData` 而不是 Unity 的 `JsonData`。
        //   ⛔ 不在這裡做型別轉換 —— 轉一次就是「同一份資料的第二個物件」，
        //   而兩個物件在畫面上長得一樣，寫回去的時候才會發現誰是誰。
        public static SCP.Core.Json.SCP_JsonData LoadJson(string path, out string error)
        {
            var aData = SCP.Core.Library.SCP_LibraryIO.LoadJson(path, out string aErr);
            error = aErr;
            return aData;
        }

        public static void SaveJson(string path, SCP.Core.Json.SCP_JsonData data)
            => SCP.Core.Library.SCP_LibraryIO.SaveJson(path, data);

        public static void SaveText(string path, string text)
            => SCP.Core.Library.SCP_LibraryIO.SaveText(path, text);

        public static SCP.Core.Json.SCP_JsonData ToStringArray(IList<string> values, params string[] alsoInclude)
            => SCP.Core.Library.SCP_LibraryIO.ToStringArray(values, alsoInclude);

        public static List<string> SplitList(string raw)
            => SCP.Core.Library.SCP_LibraryIO.SplitList(raw);

        /// <summary>讀 reader.json（含「你還不是這部的 reader」那條**指出出口**的訊息）。</summary>
        public static SCP.Core.Json.SCP_JsonData LoadReader(string mediaId, string persona, out string error)
        {
            var aReader = SCP.Core.Library.SCP_LibraryIO.LoadReader(DataRoot, mediaId, persona, out string aErr);
            error = aErr;
            return aReader;
        }

        /// <summary>章節連續性分類（**分類，不是閘門**）。</summary>
        public static SCP.Core.Library.SCP_ChapterRelation ClassifyChapter(SCP.Core.Json.SCP_JsonData reader,
                                                                           string chapterId)
            => SCP.Core.Library.SCP_LibraryIO.ClassifyChapter(reader, chapterId);

        public static string RelationLabel(SCP.Core.Library.SCP_ChapterRelation relation)
            => SCP.Core.Library.SCP_LibraryInit.RelationLabel(relation);

        public static List<string> ListMediaIds()
            => SCP.Core.Library.SCP_LibraryStore.ListMediaIds(DataRoot);

        /// <summary>建檔（work／media／reader 三層一次到位；已存在的檔**不覆寫**）。</summary>
        public static string MediaInit(string workId, string mediaId, string mediaKind, string persona,
                                       string title, string titleOriginal, string author, int anticipation,
                                       IList<string> aliases, IList<string> genreTags,
                                       out string error)
        {
            string aLog = SCP.Core.Library.SCP_LibraryInit.MediaInit(
                LettersRootOf(), DataRoot,
                workId, mediaId, mediaKind, persona, title, titleOriginal, author,
                anticipation, aliases, genreTags, out string aErr);
            error = aErr;
            return aLog;
        }




        /// <summary>authored 正文容器：`works/&lt;work_id&gt;/chapters/`。</summary>
        public static string WorkChaptersRoot(string workId)
            => SCP.Core.Library.SCP_LibraryAuthored.WorkChaptersRoot(DataRoot, workId);
        /// <summary>authored 卷／弧容器：`works/&lt;work_id&gt;/arcs/`。</summary>
        public static string WorkArcsRoot(string workId)
            => SCP.Core.Library.SCP_LibraryAuthored.WorkArcsRoot(DataRoot, workId);

        /// <summary>正文容器三態讀數（⛔ 不是檔數 —— 「沒有目錄」與「目錄在而 0 檔」是兩個值）。</summary>
        public static SCP.Core.Library.SCP_WorkProseState ProbeWorkProse(string dir, out int fileCount)
            => SCP.Core.Library.SCP_LibraryAuthored.ProbeWorkProse(dir, out fileCount);

        public static bool TrySetWorkAuthored(string workId, SCP.Core.Library.SCP_WorkAuthored fields, out string error)
        {
            bool aOk = SCP.Core.Library.SCP_LibraryAuthored.TrySetWorkAuthored(DataRoot, workId, fields, out string aErr);
            error = aErr;
            return aOk;
        }

        public static bool TryReadWorkAuthored(string workId, out SCP.Core.Library.SCP_WorkAuthored fields, out string error)
        {
            bool aOk = SCP.Core.Library.SCP_LibraryAuthored.TryReadWorkAuthored(DataRoot, workId,
                out SCP.Core.Library.SCP_WorkAuthored aFields, out string aErr);
            fields = aFields; error = aErr;
            return aOk;
        }

        /// <summary>舊 store 的草稿檔位置（寫書線四欄的事實源）。</summary>
        public static string OldStoreBookJsonPath(string bookSlug)
            => SCP.Core.Library.SCP_LibraryAuthored.OldStoreBookJsonPath(DataRoot, bookSlug);

        /// <summary>③ 逐欄對拍（⛔ 純讀）。</summary>
        public static SCP.Core.Library.SCP_AuthoredDiffOutcome DiffWorkAuthored(string bookSlug, string workId,
            out List<string> mismatchedFields, out string report)
            => SCP.Core.Library.SCP_LibraryAuthored.DiffWorkAuthored(DataRoot, bookSlug, workId,
                                                                     out mismatchedFields, out report);

        /// <summary>④ 搬遷（不給 confirm ⇒ 零寫入）。</summary>
        public static SCP.Core.Library.SCP_AuthoredMigrateOutcome MigrateAuthoredWork(string bookSlug, string workId,
            bool confirm, out string report, out string error)
        {
            var aOutcome = SCP.Core.Library.SCP_LibraryAuthored.MigrateAuthoredWork(
                DataRoot, bookSlug, workId, confirm, out report, out string aErr);
            error = aErr;
            return aOutcome;
        }

        /// <summary>全 Library 的 media 總表（瀏覽下拉／scan／漫畫比對共用）。</summary>
        public static List<SCP.Core.Library.SCP_MediaEntry> ListMediaEntries()
            => SCP.Core.Library.SCP_LibraryCatalog.ListMediaEntries(DataRoot);

        /// <summary>列出某 media 底下的 reader persona。</summary>
        public static List<string> ListReaders(string mediaId)
            => SCP.Core.Library.SCP_LibraryStore.ListReaderPersonas(DataRoot, mediaId);

        /// <summary>解析資料夾名為系列名與卷數。</summary>
        public static void ParseSeriesAndVolume(string folderName, out string seriesName, out string volumeLabel)
            => SCP.Core.Library.SCP_LibraryComics.ParseSeriesAndVolume(folderName, out seriesName, out volumeLabel);

        /// <summary>系列名 → slug。</summary>
        public static string NormalizeSeriesSlug(string raw)
            => SCP.Core.Library.SCP_LibraryComics.NormalizeSeriesSlug(raw);

        /// <summary>掃外部實體漫畫庫並與 Library 三態比對。</summary>
        /// <remarks>⚠ **根在這一層解析**（`GetComicRoot` 讀的是 Unity 的 EditorPrefs）——
        /// SCP 那側刻意吃參數：同一個量若兩層各讀各的，分岔時兩邊都讀得出一條看起來正常的路徑。</remarks>
        public static List<SCP.Core.Library.SCP_ExternalComicSeries> ScanExternalComics(string iCustomRoot = null)
        {
            string aRoot = !string.IsNullOrEmpty(iCustomRoot) ? iCustomRoot : GetComicRoot();
            var aList = SCP.Core.Library.SCP_LibraryComics.ScanExternalComics(DataRoot, aRoot, out string aWarn);
            // ⚠ 警告由 SCP 交回來（那一層叫不到 Unity 的 Debug）—— ⛔ 不吞：掃到一半炸掉與「庫是空的」同形。
            if (!string.IsNullOrEmpty(aWarn)) Debug.LogWarning($"[ReadingLibrary] {aWarn}");
            return aList;
        }

        /// <summary>已遷移的 Archive slug 集合（`_migration/registry.json` 是唯一標記處）。</summary>
        public static HashSet<string> LoadMigratedArchiveSlugs()
            => SCP.Core.Library.SCP_LibraryScan.LoadMigratedArchiveSlugs(DataRoot);

        /// <summary>op=scan 四類審計（唯一寫入是報告檔）。</summary>
        public static string ScanLibrary(out string reportPath, out string error, bool showMigrated = false)
        {
            string aReport = SCP.Core.Library.SCP_LibraryScan.ScanLibrary(DataRoot, out string aPath, out string aErr,
                                                                          showMigrated);
            reportPath = aPath; error = aErr;
            return aReport;
        }

        /// <summary>組一則「章節心得 → 酒館」的發文內文（⛔ 本函式不發文）。</summary>
        public static string BuildShareBody(string mediaId, string persona, string chapterId,
                                            ref int roundNumber, out string error)
        {
            string aBody = SCP.Core.Library.SCP_LibraryShare.BuildShareBody(DataRoot, mediaId, persona, chapterId,
                                                                            ref roundNumber, out string aErr);
            error = aErr;
            return aBody;
        }

        /// <summary>把發文回來的 seq 寫回該 round —— 「已發文」的可驗證 receipt。</summary>
        public static void RecordSharedSeq(string mediaId, string persona, string chapterId,
                                           int roundNumber, int seq, out string error)
        {
            SCP.Core.Library.SCP_LibraryShare.RecordSharedSeq(DataRoot, mediaId, persona, chapterId,
                                                              roundNumber, seq, out string aErr);
            error = aErr;
        }

    }
}
#endif
