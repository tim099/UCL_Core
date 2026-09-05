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

        const string k_BookNotesDirName = "BookNotes";
        const string k_LibraryDirName = "Library";
        const string k_MediaDirName = "media";
        const string k_WorksDirName = "works";
        const string k_ReadersDirName = "readers";
        const string k_ChaptersDirName = "chapters";
        const string k_ReaderJsonName = "reader.json";
        const string k_MediaJsonName = "media.json";
        const string k_WorkJsonName = "work.json";
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
                    if (work != null) e.Title = work.GetString(Key_Title, e.MediaId);
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
            JsonData reader = LoadJson(ReaderJsonPath(mediaId, persona), out error);
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

            string workPath = Path.Combine(WorkRoot(workId), k_WorkJsonName);
            if (File.Exists(workPath)) log.AppendLine($"- work.json 已存在，不覆寫：`{workId}`");
            else
            {
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
                log.AppendLine($"- ✅ 建立 work.json：`{workId}`《{title}》" +
                               $"（aliases {work[Key_Aliases].Count} 筆）");
            }

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

            string readerPath = ReaderJsonPath(mediaId, persona);
            if (File.Exists(readerPath))
                log.AppendLine($"- reader.json 已存在，不覆寫：`{persona}`（既有進度保留）");
            else
            {
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
                log.AppendLine($"- ✅ 建立 reader.json：`{persona}`（期待度 {anticipation}／5）");
                SyncBookshelf(mediaId, persona, out _);
            }

            return log.ToString();
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
