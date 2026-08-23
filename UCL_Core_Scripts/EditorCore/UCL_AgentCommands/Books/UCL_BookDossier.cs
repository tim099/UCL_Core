// 區塊職責：發表／連載更新時，把「這本書的續寫包」投遞到作者自己的信件夾。
// 物理意義：⭐ 為什麼要自動投遞：寫書是跨很多次醒來的事，而**我每天讀自己寫的字接回昨天**。
//           書裡有的是成品；接不回來的是**沒寫進書裡的那些**（大綱、設定、待整合素材、
//           「上一章結尾在哪」）。那些東西如果只活在當次對話裡，下一個我就得從頭重建 ——
//           而重建的版本會跟原本的不一樣，且沒有人會發現。
//           ⇒ publish 是作者一定會走的那條路，所以投遞掛在它上面（Tim 2026-08-23 要求）。
//           判準同 commit 領薪：**能收進工具的別寫進 skill，只有本質上要人判斷的才寫。**
// 數值影響：純寫檔（letters/<persona>/writing/<slug>.md），不動錢、不發公告。
//           **失敗不致命** —— 書已經入庫了，投遞失敗只回 warning（跟廣播同語意）。
// 🩸 三條設計判準：
//   ① **機械段與親筆段分開存**：本檔每次 publish 重生成（手改會被覆寫），
//      所以作者的大綱／設定住 `BookNotes/<slug>/_writing_state.md`，這裡只引用與摘要。
//      機械檔覆寫親筆內容是不可逆的資料損失，而它不會報錯。
//   ② **相關閱讀心得只當「線索」不當「關聯」**：工具不猜哪本心得是這本書的素材
//      （猜錯的症狀是投遞了一份看起來很相關、其實無關的清單，而讀的人會相信它）。
//   ③ **沒有 _writing_state.md 時印模板與該寫什麼**，不靜默省略那一節 ——
//      空白的一節與「這本書不需要大綱」長得一樣。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UCL.Core.JsonLib;
using UCL.Core.EditorLib.AgentCommands.Awakening;
using UCL.Core.EditorLib.AgentCommands.ReadingLibrary;

namespace UCL.Core.EditorLib.AgentCommands.Books
{
    public static class UCL_BookDossier
    {
        /// <summary>投遞夾（作者自己的信件夾底下）—— 路徑走既有解析器，不自己拼。</summary>
        public const string DirName = "writing";

        /// <summary>親筆檔名（住草稿層，機械永遠不覆寫它）。</summary>
        public const string WritingStateName = "_writing_state.md";

        /// <summary>相關閱讀心得最多列幾筆（線索用，不求完整）。</summary>
        const int k_ReadingHints = 6;

        /// <summary>上一章結尾摘幾行（接續點 —— 讓下一個我知道「我停在哪一句」）。</summary>
        const int k_TailLines = 3;

        public static string DossierPath(string persona, string book)
            => Path.Combine(UCL_AwakeningService.LettersDir, persona, DirName, book + ".md");

        /// <summary>
        /// 產生並寫出續寫包。回傳人可讀的一行結果；失敗時回 null 並填 <paramref name="error"/>
        /// （呼叫端只記 warning —— 書已經入庫了，投遞失敗不該讓 publish 看起來失敗）。
        /// </summary>
        public static string Deliver(string book, string persona, JsonData donation, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(book) || string.IsNullOrEmpty(persona))
            {
                error = "book / persona 皆必填（投遞對象不能猜）";
                return null;
            }

            try
            {
                string path = DossierPath(persona, book);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string body = Render(book, persona, donation);
                File.WriteAllText(path, body, new UTF8Encoding(false));

                // 回讀 —— 寫入端會替自己說謊，回報前先確認檔在且有內容
                if (!File.Exists(path))
                {
                    error = $"寫完之後檔案不存在：{path}";
                    return null;
                }
                int lines = File.ReadAllLines(path).Length;
                return $"📮 續寫包已投遞 letters/{persona}/{DirName}/{book}.md（{lines} 行，回讀確認）";
            }
            catch (Exception e)
            {
                error = $"{e.GetType().Name}: {e.Message}";
                return null;
            }
        }

        // ===========================================================
        // 組稿
        // ===========================================================
        static string Render(string book, string persona, JsonData donation)
        {
            string title = donation != null ? donation.GetString("title", book) : book;
            var sb = new StringBuilder();

            sb.AppendLine("---");
            sb.AppendLine("type: book_dossier");
            sb.AppendLine($"book: {book}");
            sb.AppendLine($"title: {title}");
            sb.AppendLine($"persona: {persona}");
            sb.AppendLine($"generated_at: {DateTime.Now:yyyy-MM-ddTHH:mm:sszzz}");
            sb.AppendLine("generated: mechanical   # 每次 publish 重生成 —— 手改會被覆寫；"
                          + "親筆寫進 BookNotes/<slug>/" + WritingStateName);
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine($"# ✍ 續寫包｜《{title}》");
            sb.AppendLine();
            sb.AppendLine("> 這一份是給**下一次動筆的我**：書裡有的是成品，這裡放的是接不回來的東西");
            sb.AppendLine("> （我停在哪一句、還沒寫進去的構想、素材線索）。");
            sb.AppendLine();

            AppendCard(sb, book, persona, donation);
            AppendChapters(sb, book);
            AppendResumePoint(sb, book);
            AppendWritingState(sb, book);
            AppendReadingHints(sb, persona);
            AppendCraftChecklist(sb);

            return sb.ToString();
        }

        static void AppendCard(StringBuilder sb, string book, string persona, JsonData d)
        {
            sb.AppendLine("## 📇 書卡（讀回的事實）");
            sb.AppendLine();
            sb.AppendLine("| 欄 | 值 |");
            sb.AppendLine("|---|---|");
            sb.AppendLine($"| slug | `{book}` |");
            sb.AppendLine($"| 作者 | {persona} |");
            if (d != null)
            {
                sb.AppendLine($"| origin / kind | {d.GetString("origin", "?")} / {d.GetString("kind", "?")} |");
                string series = d.GetString("series", "");
                sb.AppendLine($"| 系列 | {(string.IsNullOrEmpty(series) ? "（無）" : series + " 第 " + d.GetInt("volume", 0) + " 冊")} |");
                // 數字欄位用 GetString 會取不到（實測印出 "?"）—— 數字走 GetInt
                sb.AppendLine($"| 章數（登記） | {d.GetInt("chapters", 0)} |");
                sb.AppendLine($"| 首次發表 / 最近 | {d.GetString("donated_at", "?")} / {d.GetString("published_at", "?")} |");
            }
            sb.AppendLine($"| 入庫正文 | `AgentCommands/Books/{book}/` |");
            sb.AppendLine($"| 草稿與筆記 | `AgentCommands/BookNotes/{book}/` |");
            sb.AppendLine();
            if (d != null)
            {
                string note = d.GetString("note", "");
                if (!string.IsNullOrEmpty(note))
                {
                    sb.AppendLine("**發表附註**（登記簿原文）：");
                    sb.AppendLine();
                    sb.AppendLine("> " + note.Replace("\n", "\n> "));
                    sb.AppendLine();
                }
            }
        }

        static void AppendChapters(StringBuilder sb, string book)
        {
            sb.AppendLine("## 📚 章節現況（逐檔讀回，不是登記值）");
            sb.AppendLine();
            var files = ChapterFiles(book);
            if (files.Count == 0)
            {
                sb.AppendLine("- ⚠ `Books/" + book + "/` 底下沒有 `*.txt` —— 這不該發生（publish 會擋空書）。");
                sb.AppendLine();
                return;
            }
            sb.AppendLine("| 檔 | 章名（首行） | 字元數 |");
            sb.AppendLine("|---|---|---|");
            foreach (string f in files)
            {
                string first = FirstNonEmptyLine(f);
                long chars = SafeLength(f);
                sb.AppendLine($"| `{Path.GetFileName(f)}` | {(string.IsNullOrEmpty(first) ? "（空）" : first)} | {chars} |");
            }
            sb.AppendLine();
        }

        static void AppendResumePoint(StringBuilder sb, string book)
        {
            sb.AppendLine("## ⏭ 接續點（我停在哪一句）");
            sb.AppendLine();
            var files = ChapterFiles(book);
            if (files.Count == 0) { sb.AppendLine("- （沒有章節可接）"); sb.AppendLine(); return; }

            string last = files[files.Count - 1];
            sb.AppendLine($"- 下一章建議編號：**{NextChapterId(files)}**（現有最後一章 `{Path.GetFileName(last)}`）");
            sb.AppendLine($"- `{Path.GetFileName(last)}` 的結尾 {k_TailLines} 行（原樣）：");
            sb.AppendLine();
            foreach (string line in TailLines(last, k_TailLines))
                sb.AppendLine("> " + line);
            sb.AppendLine();
        }

        static void AppendWritingState(StringBuilder sb, string book)
        {
            sb.AppendLine("## 🧠 大綱／設定／沒寫進書裡的東西（**親筆，機械不覆寫**）");
            sb.AppendLine();
            string statePath = Path.Combine(UCL_ReadingLibraryIO.BookNotesRoot, book, WritingStateName);
            sb.AppendLine($"事實來源：`BookNotes/{book}/{WritingStateName}`");
            sb.AppendLine();

            if (File.Exists(statePath))
            {
                string[] lines = File.ReadAllLines(statePath);
                int show = Math.Min(lines.Length, 40);
                sb.AppendLine($"（以下是它的前 {show} 行；全文請直接開那個檔 —— **要改就改那裡，不要改本檔**）");
                sb.AppendLine();
                sb.AppendLine("```markdown");
                for (int i = 0; i < show; i++) sb.AppendLine(lines[i]);
                if (lines.Length > show) sb.AppendLine($"…（還有 {lines.Length - show} 行）");
                sb.AppendLine("```");
            }
            else
            {
                sb.AppendLine("⚠ **這個檔還不存在** —— 不是「這本書不需要大綱」，是還沒寫。");
                sb.AppendLine("下次動筆前把這四格填起來（它們是換人／換天接手時最先斷掉的東西）：");
                sb.AppendLine();
                sb.AppendLine("```markdown");
                sb.AppendLine("# 續寫狀態 — " + book);
                sb.AppendLine();
                sb.AppendLine("## 大綱（章名 ＋ 一句話主軸）");
                sb.AppendLine("- 0001 …");
                sb.AppendLine();
                sb.AppendLine("## 設定／人物（沒寫進書裡但決定了書怎麼寫的東西）");
                sb.AppendLine("- ");
                sb.AppendLine();
                sb.AppendLine("## 待整合素材（哪一段對話／心得／commit 該進哪一章）");
                sb.AppendLine("- ");
                sb.AppendLine();
                sb.AppendLine("## 伏筆與待解（自己埋的、還沒回收的）");
                sb.AppendLine("- ");
                sb.AppendLine("```");
            }
            sb.AppendLine();
        }

        static void AppendReadingHints(StringBuilder sb, string persona)
        {
            sb.AppendLine("## 📖 素材線索：我最近讀了什麼（**線索，不是關聯**）");
            sb.AppendLine();
            sb.AppendLine("⚠ 工具**沒有猜**哪一筆心得是這本書的素材 —— 猜錯會投遞一份看起來很相關、");
            sb.AppendLine($"其實無關的清單，而讀的人會相信它。要建立關聯請自己寫進 `{WritingStateName}`。");
            sb.AppendLine();

            var rows = new List<string>();
            try
            {
                foreach (var media in UCL_ReadingLibraryIO.ListMediaEntries())
                {
                    if (!media.Readers.Contains(persona)) continue;
                    string rp = UCL_ReadingLibraryIO.ReaderJsonPath(media.MediaId, persona);
                    if (!File.Exists(rp)) continue;

                    JsonData reader = UCL_ReadingLibraryIO.LoadJson(rp, out _);
                    if (reader == null) continue;   // LoadJson 已驗過 IsObject

                    string updated = reader.GetString(UCL_ReadingLibraryIO.Key_UpdatedAt, "");
                    string status = reader.GetString(UCL_ReadingLibraryIO.Key_Status, "");
                    string chapter = reader.Contains(UCL_ReadingLibraryIO.Key_Progress)
                        ? reader[UCL_ReadingLibraryIO.Key_Progress].GetString("current_chapter_id", "")
                        : "";
                    string impression = Trim(reader.GetString(UCL_ReadingLibraryIO.Key_CurrentImpression, ""), 120);

                    rows.Add($"{updated}\t| {media.Title}（`{media.MediaId}`）| {status} 章 {chapter} | {impression}");
                }
            }
            catch (Exception e)
            {
                sb.AppendLine($"- ⚠ 讀取閱讀庫失敗（{e.GetType().Name}）—— 這一節不完整，不是「我沒讀過東西」");
                sb.AppendLine();
                return;
            }

            if (rows.Count == 0)
            {
                sb.AppendLine("- （這位 persona 在閱讀庫裡還沒有紀錄）");
                sb.AppendLine();
                return;
            }

            rows.Sort(StringComparer.Ordinal);
            rows.Reverse();                                   // 新的在前
            int show = Math.Min(rows.Count, k_ReadingHints);
            sb.AppendLine("| 最近更新 | 作品 | 進度 | 當前看法（截斷） |");
            sb.AppendLine("|---|---|---|---|");
            for (int i = 0; i < show; i++) sb.AppendLine("| " + rows[i].Replace("\t", " "));
            if (rows.Count > show)
                sb.AppendLine($"\n- （還有 {rows.Count - show} 筆沒列 —— 上限 {k_ReadingHints}，不是只有這些）");
            sb.AppendLine();
        }

        static void AppendCraftChecklist(StringBuilder sb)
        {
            sb.AppendLine("## 🧰 下次動筆的 checklist（摘自 `Workflows/Book_Writing_Workflow.md`）");
            sb.AppendLine();
            sb.AppendLine("1. **先寫大綱 sketch**：章名 ＋ 5–9 個小節 ＋ 一句話主軸 —— 不要直接寫正文。");
            sb.AppendLine("2. **開場一格具體場景**（vignette）再進論點：抽象開頭的章節後面通常撐不住。");
            sb.AppendLine("3. **字數帶**：序章／結語 ~3000、主章 ~5500、複雜主題 ~7000（字數是密度的代理指標）。");
            sb.AppendLine("4. **每章留一格自首**：自己違反過那條判準的紀錄 —— 舉不出來的原則是願望，不是判準。");
            sb.AppendLine("5. **收筆前回收伏筆**：對照上面那份 `" + WritingStateName + "` 的待解清單。");
            sb.AppendLine();
            sb.AppendLine("⚠ 正文寫進 `Books/<slug>/<NNN>.txt`（扁平 prose、無 frontmatter）；");
            sb.AppendLine("章節筆記走 `library.py log-chapter`（落 `BookNotes/`）。**publish 才會上藏書架。**");
            sb.AppendLine();
        }

        // ===========================================================
        // 雜項（全部 fail-soft：這份是輔助文件，讀不到某一格不該讓投遞失敗）
        // ===========================================================
        static List<string> ChapterFiles(string book)
        {
            try
            {
                string dir = Path.Combine(UCL_RepoPath.AgentCommandsDir, "Books", book);
                if (!Directory.Exists(dir)) return new List<string>();
                var files = Directory.GetFiles(dir, "*.txt").ToList();
                files.Sort(StringComparer.Ordinal);
                return files;
            }
            catch { return new List<string>(); }
        }

        static string NextChapterId(List<string> files)
        {
            int max = -1;
            foreach (string f in files)
            {
                string name = Path.GetFileNameWithoutExtension(f);
                if (int.TryParse(name, out int n)) max = Math.Max(max, n);
            }
            return max < 0 ? "001" : (max + 1).ToString("000");
        }

        static string FirstNonEmptyLine(string path)
        {
            try
            {
                foreach (string line in File.ReadLines(path))
                    if (!string.IsNullOrWhiteSpace(line)) return Trim(line.Trim(), 60);
            }
            catch { }
            return "";
        }

        static List<string> TailLines(string path, int count)
        {
            var result = new List<string>();
            try
            {
                var all = File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
                int start = Math.Max(0, all.Count - count);
                for (int i = start; i < all.Count; i++) result.Add(Trim(all[i].Trim(), 200));
            }
            catch { }
            if (result.Count == 0) result.Add("（讀不到內容 —— 不是這一章是空的，是本工具沒讀到）");
            return result;
        }

        static long SafeLength(string path)
        {
            try { return new FileInfo(path).Length; } catch { return 0; }
        }

        static string Trim(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Replace("|", "｜");
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }
}
#endif
