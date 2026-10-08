// 區塊職責：**跨 process 的互斥**，給「讀整個檔 → 改 → 寫回整個檔」那種臨界區用（`queue.json`）。
// 物理意義：TASK-0263。queue 的寫入端全都是 read-modify-write ⇒ 兩邊各自讀到同一份舊內容、各自寫回，
//           **後寫的那份把先寫的那一筆整個吃掉**。
//
// 🩸 為什麼既有的「atomic replace ＋ 重試」擋不住：
//   temp → move 保護的是「寫到一半的檔」，不是「讀到一半的世界」。
//   ⇒ 名字叫 atomic 的是那次替換，⛔ 不是整段讀改寫。兩者中間隔著一整個決策。
//   實測（60 筆併發委派）：落盤 55，而 60 顆 client **全部 exit 0**。
//
// ⭐ 為什麼是「開著的檔案握把」而不是「一顆標記檔」：
//   標記檔要自己處理**持有者死掉**（留下一顆永遠拿不到的鎖），而那需要 stale 判定 ——
//   一個新的、會誤判的讀數。開著的握把由 OS 在行程結束時收回，⇒ 那一整族問題不存在。
//
// ⛔ 本鎖**不保證**臨界區裡的內容正確 —— 它只保證同一時刻只有一個人在那段裡。
//   讀改寫還是要真的**重讀**：拿著鎖去寫一份在拿鎖之前載入的舊副本，鎖一點忙都幫不上。
using System;
using System.IO;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>
    /// 跨 process 的檔案級互斥鎖。<c>using</c> 進出，<see cref="Dispose"/> 放開。
    /// <para>⚠ 它鎖的是**旁邊那顆 `.lock` 檔**，不是目標檔本身 ——
    /// 鎖住目標檔會讓不參與這個協議的讀取端（例如診斷指令）也讀不到。</para>
    /// </summary>
    public sealed class UCL_FileLock : IDisposable
    {
        /// <summary>鎖檔的副檔名。⚠ 與目標同目錄 ⇒ 目標目錄要先存在。</summary>
        public const string LockSuffix = ".lock";

        /// <summary>預設等鎖秒數。⚠ 超過就丟例外，⛔ 不「拿不到就照做」。</summary>
        public const double DefaultTimeoutSec = 20;

        FileStream m_Stream;

        UCL_FileLock(FileStream iStream) { m_Stream = iStream; }

        /// <summary>這一顆目標檔的鎖檔路徑。</summary>
        public static string LockPathFor(string iTargetPath) => iTargetPath + LockSuffix;

        /// <summary>
        /// 取得 <paramref name="iTargetPath"/> 的互斥鎖；拿不到就**等**，等過頭**丟 <see cref="IOException"/>**。
        /// <para>⛔ 逾時不回 <c>null</c>、不降級 —— 「拿不到鎖還是寫下去」正是 TASK-0263 要根治的病。</para>
        /// </summary>
        /// <param name="iTimeoutSec">等多久。⚠ 要比臨界區本身長很多：這裡的臨界區是
        /// 「讀一顆 json ＋ 寫回」，毫秒級；等不到 20 秒代表**有人卡住**，那是該出聲的事。</param>
        public static UCL_FileLock Acquire(string iTargetPath, double iTimeoutSec = DefaultTimeoutSec)
        {
            string aLockPath = LockPathFor(iTargetPath);
            string aDir = Path.GetDirectoryName(aLockPath) ?? "";
            if (aDir.Length > 0) Directory.CreateDirectory(aDir);

            var aSw = System.Diagnostics.Stopwatch.StartNew();
            Exception aLast = null;
            int aTries = 0;
            while (true)
            {
                try
                {
                    var aStream = new FileStream(aLockPath, FileMode.OpenOrCreate,
                                                 FileAccess.ReadWrite, FileShare.None);
                    return new UCL_FileLock(aStream);
                }
                catch (IOException e)
                {
                    // 被別人握著 ⇒ 等。⚠ 其餘 IO 失敗（路徑不存在／磁碟滿）也會走到這裡，
                    //   而它們等再久也不會好 —— 逾時那一刻連同 inner 一起丟出去，成因不換掉。
                    aLast = e;
                }
                catch (UnauthorizedAccessException e) { aLast = e; }

                ++aTries;
                if (aSw.Elapsed.TotalSeconds >= iTimeoutSec)
                    throw new IOException("等不到檔案鎖（" + iTimeoutSec.ToString("0.#") + "s，試了 " + aTries + " 次）："
                                          + aLockPath + "　⇒ 有人握著它沒放，或那個路徑本身有問題（成因見 inner）。"
                                          + "　⛔ 本呼叫**沒有**寫入任何東西。", aLast);

                // 退讓：前幾次很短（常態是毫秒級臨界區），之後拉長避免空轉。
                System.Threading.Thread.Sleep(aTries < 10 ? 2 : 20);
            }
        }

        public void Dispose()
        {
            FileStream aStream = m_Stream;
            m_Stream = null;
            if (aStream == null) return;
            try { aStream.Dispose(); }
            catch (IOException) { /* 放鎖失敗不該蓋掉臨界區裡真正的結果 */ }
        }
    }
}
