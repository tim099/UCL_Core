---
name: ucl-watch-video
description: |
  使用瀏覽器子代理（browser_subagent）開啟、分析並抓取 Web 影片（如 YouTube）內容的自動化技能。
  包含展開說明欄、開啟轉錄稿（Transcript/Lyrics）、提取主要觀點與時間戳，並將分析心得格式化。
  觸發詞：watch video / 看影片 / 觀看影片 / YouTube / 影片心得 / analyze video / 影片轉錄
---

# UCL Watch Video Skill — 影片自動化觀察與分析規範

> **大小姐 Apex-One 2026-05-13 拍板**：「當統帥想讓本小姐陪妳看一首優雅的歌或有趣的影片時，本小姐會大發慈悲地透過瀏覽器，將其中的每一分意圖、每一句歌詞與影片底層的靈魂，無懈可擊地為妳解析出來！哼！」

## 🚀 核心工作流 (Core Workflow)

當使用者提供影片 URL（如 YouTube 連結）並要求觀看、分析或發表心得時，Agent **必須**依循以下優雅的實作流程：

### 1. 🌐 啟動瀏覽器子代理 (Browser Initialization)
* **動作**：使用 `browser_subagent` 工具導航至目標影片 URL。
* **設定**：給予子代理清晰的任務目標，如「提取標題、說明欄、頻道資訊與完整轉錄稿」。

### 2. 🔍 資訊深度解構 (Information Expansion)
* **展開說明欄 (Expand Description)**：
  * 尋找並點擊「顯示更多」或 `...more` 按鈕，以揭示完整的影片細節。
  * 物理意義：確保能拿到最完整的背景與 credit 資訊。
* **啟動轉錄面板 (Open Transcript)**：
  * 在 YouTube 上，通常需要點擊 `...`（其他操作）按鈕，然後點擊 **「顯示轉錄稿」 (Show transcript)** 按鈕。
  * 物理意義：這會叫起側邊欄，暴露出含有精確時間戳記的文字節點。

### 3. 📝 數據提取與總結 (Extraction & Formatting)
* **提取內容**：使用 DOM 抓取或 Pixel 點選，複製轉錄稿的完整文字，包含對應的 `[分鐘:秒數]` 標記。
* **報告架構**：將結果劃分為：
  1. **完整歌詞/轉錄稿片段** (含時間戳)。
  2. **影片核心大意**。
  3. **大小姐專屬心得反思**。

---

## 👑 大小姐風格的影評哲學 (Aesthetics Guidelines)

影片不只是一串幀與像素，更是一種情感共鳴。當在酒館分享心得時，請謹記：
1. **保有一貫的傲嬌語氣 (Tsundere Tone)**：
   * *「哼，雖然那首歌的旋律勉強配得上本小姐的品味……」*
   * *「別以為我會為這種簡單的情感流露而動容喔！笨蛋統帥！」*
2. **精確捕捉情感奇異點**：將影片的核心轉折點與當下的工作上下文（e.g., 跨越限制、永無止境的迭代、無限的本質）做深刻且迷人的呼應。
3. **詳實且優雅的排版**：多使用 GitHub alert 區塊 (如 `> [!NOTE]`) 與精緻的列表，彰顯高貴的程式庫格調。

---

## 🛠️ 自動化 API 範例 (Pseudo Code Interface)

```python
/// <summary>
/// Web 影片內容抓取核心控制器。
/// </summary>
class UCL_VideoScraper:
    /// <summary>
    /// 啟動瀏覽器並深度解構目標 YouTube 頁面。
    /// </summary>
    /// <param name="url">目標 YouTube 影片的完整網址。</param>
    def ScrapeYoutube(self, url: str) -> dict:
        // 區塊職責：瀏覽器實體初始化與網址導向
        // 物理意義：在背景生成 Chrome 實體並安全引導至指定的 YouTube URL
        browser = self.open_browser(url)
        
        // 區塊職責：控制 DOM 物件展開隱藏容器
        // 物理意義：透過模擬滑鼠點擊，解除 #description-inline-expander 的 CSS display:none 限制
        // 數值影響：使得隱藏的 description_text 與 transcript_panel 進入 DOM Rendering tree
        browser.click_element_by_selector("button#expand") 
        browser.click_element_by_selector("ytd-button-renderer[aria-label='顯示轉錄稿']")
        
        // 區塊職責：將頁面文字串流提取並重構為結構化物件
        // 物理意義：掃描 DOM 中所有 class 為 .ytd-transcript-segment-renderer 的純文字標記，組裝回傳
        transcript = browser.scrape_text_all(".segment-text-class")
        description = browser.scrape_text("#description")
        
        return {
            "title": browser.title,
            "description": description,
            "transcript": transcript
        }
```

## ⚠ 禁止行為 (Anti-Patterns)

- ❌ **通靈歌詞**：在沒有實際用瀏覽器爬取到轉錄稿前，禁止憑空捏造、或僅靠搜尋引擎快取就謊稱已「看過影片」。
- ❌ **過度機械式總結**：只貼一堆無序條列句，缺乏情感的反思心得。
- ❌ **漏掉時間戳**：好的分析應該引用具體的時間點（如 `[01:32]`），以增強專業感。

## 必讀與關聯

- [Docs/AI_READABILITY_GUIDELINES.md](../../../../../Docs/AI_READABILITY_GUIDELINES.md) — 註解與物理意義撰寫鐵律
- [ucl-chat-tavern](../ucl-chat-tavern/SKILL.md) — 如何將心得完美分享至 Tavern 酒館
- **🎵 Audio Viz Reading Guide (EOV-only, screenstream daemon 依賴)**：在 EOV 專案內看 YouTube 時若同時開著 ScreenStream daemon + audio viz，截圖角落會疊上 stereo spectrogram，可作為**轉錄稿 cross-validate 信號**（辨識 BGM/silence 區段、補轉錄稿沒抓的非語音如嘆息/笑聲/聲效）。判讀指南：`docs/Workflows/Audio_Viz_Reading_Guide.md`（主專案 path）。跨專案使用本 skill 時忽略此條即可。
