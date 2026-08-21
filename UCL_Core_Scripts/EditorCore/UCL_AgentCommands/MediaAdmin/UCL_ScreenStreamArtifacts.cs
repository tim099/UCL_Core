// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 08/21 2026
// ScreenStream 管線的 python 產物 typed model（螢幕清單 / STT 轉錄 / OCR 單幀 / STT 狀態）。
#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Text;
using UCL.Core.JsonLib;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.MediaAdmin
{
    // ===========================================================
    // 區塊職責：`_screenstream/` 底下**由 python 寫、C# 只讀**的四種產物的資料模型。
    // 物理意義：這些檔的 schema 是 python 端的輸出契約（`screenstream_daemon.py` /
    //          `audio_transcribe.py` / `subtitle_ocr.py`）。C# 這邊過去逐鍵讀 ——
    //          而鍵名打錯不會編譯錯、也不會執行錯：`GetString("txt")` 拿到空字串，
    //          畫面上就是「這一幀沒有字幕」，跟真的沒字幕**一模一樣**。
    // 數值影響：**全部唯讀** —— 本檔的 model 沒有任何寫入端。
    //          因此刻意不做 bool 原生化 override（那條規則是給「C# 會寫回去、python 會讀」的檔用的），
    //          也不做未知鍵保留（不寫回去就不會吃掉任何鍵）。
    // ⚠ 欄位名＝JSON 鍵名（`FieldNameUnityVer` 只脫 `m_`）⇒ 刻意不走 `m_PascalCase`。
    //   改名等於改契約，要同時改對應的 python 輸出端。
    // 📖 規範全文：`ucl_core:Docs~/{lang}/Agent/Json_Coding_Standards.md`
    // ===========================================================

    /// <summary>一塊實體螢幕（`_monitors.json` 的 `monitors[]` 一筆）。座標同 virtual desktop 空間。</summary>
    public class UCL_ScreenStreamMonitor : UnityJsonSerializable
    {
        public int index = 0;
        public int x = 0;
        public int y = 0;
        public int w = 0;
        public int h = 0;
        /// <summary>
        /// 是否為主螢幕。
        /// <para>⚠ **要在這裡加寫入端的人請先看這句**：本 model 目前唯讀，
        /// 所以沒有做 bool 原生化 override —— 直接 <c>SerializeToJson()</c> 會把它寫成
        /// 字串 <c>"True"</c>（2026-08-21 實測 dump 確認）。而 `_monitors.json` 的讀取端是
        /// **python daemon**，那邊 <c>if m.get("primary")</c> 讀到字串一律成立 ⇒
        /// 每一塊螢幕都會變成主螢幕，且不報錯。
        /// ⇒ 真要寫回這份檔，先照 `Json_Coding_Standards.md` §3.4 加 override。</para>
        /// </summary>
        public bool primary = false;
        /// <summary>OS 給的顯示器名稱（例 `\\.\DISPLAY2`）。空的時候呼叫端自己補 `DISPLAY{n}`。</summary>
        public string name = "";
    }

    /// <summary>螢幕清單（`_screenstream/_monitors.json`）—— daemon 以 `--enumerate-only` 短命行程寫出。</summary>
    public class UCL_ScreenStreamMonitors : UnityJsonSerializable
    {
        public List<UCL_ScreenStreamMonitor> monitors = new List<UCL_ScreenStreamMonitor>();

        /// <summary>讀螢幕清單；檔案不存在或解析失敗回 null（呼叫端據此維持原清單，不清成空）。</summary>
        public static UCL_ScreenStreamMonitors Load(string iPath) => UCL_ScreenStreamArtifactIO.Load<UCL_ScreenStreamMonitors>(iPath);
    }

    /// <summary>STT 的一段轉錄（`stt/stt_&lt;epoch&gt;.json` 的 `segments[]` 一筆）。</summary>
    public class UCL_SttSegment : UnityJsonSerializable
    {
        public double start_epoch = 0;
        public double end_epoch = 0;
        public string text = "";
    }

    /// <summary>一個 STT chunk 的轉錄結果（`stt/stt_&lt;epoch&gt;.json`）。</summary>
    /// <remarks>巢狀 `List&lt;UCL_SttSegment&gt;` 由序列化器自動存取 —— 不手刻解析。</remarks>
    public class UCL_SttTranscript : UnityJsonSerializable
    {
        public double start_epoch = 0;
        public double end_epoch = 0;
        /// <summary>產生這份轉錄的 whisper model（`small` / `medium` …）。</summary>
        public string model = "";
        public List<UCL_SttSegment> segments = new List<UCL_SttSegment>();

        public static UCL_SttTranscript Load(string iPath) => UCL_ScreenStreamArtifactIO.Load<UCL_SttTranscript>(iPath);
    }

    /// <summary>STT worker 的狀態（`stt/_status.json`）。</summary>
    /// <remarks>
    /// ⚠ `error` 是**禁靜默失敗的 UI 出口** —— worker 掛掉時 python 寫在這裡，
    /// 頁面把它印出來。空字串＝沒有錯誤（不是「還沒回報」）。
    /// </remarks>
    public class UCL_SttStatus : UnityJsonSerializable
    {
        public string error = "";
        /// <summary>已轉錄到的最後時刻（epoch 秒）。</summary>
        public double latest_end_epoch = 0;
        public string model = "";
        public double updated_at = 0;

        public static UCL_SttStatus Load(string iPath) => UCL_ScreenStreamArtifactIO.Load<UCL_SttStatus>(iPath);
    }

    /// <summary>單幀 OCR 結果（`ocr/frame_NNNN.json`）。</summary>
    /// <remarks>
    /// ⚠ 刻意**不宣告 `regions` 欄位**：它是 `[[y,h,xc,w], …]` 的巢狀陣列形，
    /// 而 C# 這邊沒有任何消費端 —— 宣告一個沒人用的欄位只是多一個要維護的形狀。
    /// 本 model 唯讀（不寫回檔案）⇒ 未宣告的鍵不會因此消失。
    /// </remarks>
    public class UCL_OcrFrameResult : UnityJsonSerializable
    {
        /// <summary>來源 frame 的 mtime（epoch 秒）—— OCR 沒有內容時刻可讀，時間軸只能靠它。</summary>
        public double mtime = 0;
        /// <summary>辨識出的文字；空字串＝這一幀沒有字幕（**是讀數，不是失敗**）。</summary>
        public string text = "";
        /// <summary>實際完成 OCR 的時刻（epoch 秒）。缺席時呼叫端退回 <see cref="mtime"/>。</summary>
        public double ocr_at = 0;

        public static UCL_OcrFrameResult Load(string iPath) => UCL_ScreenStreamArtifactIO.Load<UCL_OcrFrameResult>(iPath);
    }

    // ===========================================================
    // 區塊職責：本檔四個唯讀 model 的共用載入器。
    // 數值影響：讀不到 / 壞檔一律回 null —— **不回一個空實例**。
    //          空實例會讓「檔案不存在」與「內容真的是空的」變成同一件事，
    //          而這兩者在畫面上該說不同的話（前者是還沒產出，後者是沒有字幕）。
    // ⚠ 這些檔由 python 每 0.5–1 秒重寫，讀到寫入中的半份是正常的 ⇒ 失敗只回 null 不噴 log，
    //   否則 console 會被「暫時讀不到」淹掉（而真正的錯誤就藏在裡面）。
    // ===========================================================
    static class UCL_ScreenStreamArtifactIO
    {
        public static T Load<T>(string iPath) where T : UnityJsonSerializable, new()
        {
            try
            {
                if (string.IsNullOrEmpty(iPath) || !File.Exists(iPath)) return null;
                var aJd = JsonData.ParseJson(File.ReadAllText(iPath, Encoding.UTF8));
                if (aJd == null) return null;
                var aOut = new T();
                aOut.DeserializeFromJson(aJd);
                return aOut;
            }
            catch { return null; }
        }
    }
}
#endif
