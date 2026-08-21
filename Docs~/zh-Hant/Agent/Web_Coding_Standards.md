---
title: 靜態網頁撰寫規範 (Static Web Coding Standards)
description: 本 repo 裡的純前端頁（畫廊、報表、看板）該怎麼寫 — 零外部依賴、file:// 與 Pages 雙場景都要活、資料走 script src 不走 fetch、innerHTML 一律先跳脫、衍生索引不進版控。寫任何 .html 前先讀本檔。
tags: [web, html, css, javascript, static-site, github-pages, coding-standards]
aliases: [網頁規範, html 規範, 寫網頁, 靜態頁, 前端規範, github pages]
target_audience: [AI_Agent, Tools_Maintainer]
last_updated: 2026-08-21
---

# 🌐 靜態網頁撰寫規範

> 一句話：**這裡的網頁沒有後端、沒有建置管線、也沒有人會幫你重載** ——
> 所以最貴的錯不是版面難看，是**只在別人的開法下才現形**的失敗。

本檔管的是「**放在 repo 裡、給人直接開來看**」的純前端頁：
畫廊（`AgentCommands/ArtGallery/index.html`）、報表、看板那一類。
不管 React / Vue / 打包工具那種前端 —— 這個 repo 裡沒有那種東西，也刻意不要有。

---

## ⛔ 硬規則

### ① 兩種開法都要活：`file://`（雙擊）與 HTTP（Pages）

同一份 `.html` 會被兩種方式打開，而**它們的能力不一樣**：

| | `file://` 雙擊 | GitHub Pages |
|---|---|---|
| `fetch()` / `XHR` 讀同目錄檔 | ❌ **被 CORS 擋** | ✅ |
| `<script src="data.js">` | ✅ | ✅ |
| 列目錄（autoindex） | ❌ | ❌（實測 `/Diary/` → **404**） |
| 外部 CDN | 看網路 | 看網路 |

⇒ **資料一律走 `<script src>` 掛成全域變數，不要用 `fetch` 讀本地檔。**

```html
<!-- ✅ 兩邊都通 -->
<script src="gallery_data.js"></script>   <!-- 內容是 window.GALLERY_DATA = {...} -->

<!-- ❌ file:// 下必死，而錯誤訊息跟「檔案不存在」長得一模一樣 -->
<script>fetch('gallery.json').then(...)</script>
```

> 🩸 這是 `build_gallery.py` 產出 `.js` 而不是 `.json` 的唯一理由，
> 檔頭註解寫了：「用一個沒有 CORS 問題的載入方式，換掉一個只在某一種開法下才會現形的失敗。」

### ② 零外部依賴 —— 不吃 CDN、不吃字型、不吃框架

CDN 掛掉／被擋／離線時的失敗是**靜默的**：頁面照開、版面只是有點怪，
或某一區一片空白 —— 而那看起來像「這裡沒有資料」，不像「函式庫沒載到」。

需要 markdown 轉譯、日期格式化、簡單圖表？**自己寫 30 行**。
30 行看得懂、改得動、離線能跑；一個 CDN 依賴省下那 30 行，換來一種只在別人的網路下才發生的壞法。

> 🩸 2026-08-21 畫廊右欄要顯示 `.md` 全文，刻意手寫 `renderMd()`（約 30 行，
> 只認 `#~####` / `-` / `>` / `---` / `**粗體**` / `` `code` ``）而不引 markdown 函式庫。

### ③ `innerHTML` 一律先跳脫，且來源是資料就當它有敵意

repo 裡的頁面吃的是**別人寫的內容**（展品 `.md`、心得、訊息）。
「我們自己人寫的，不會有 `<script>`」不是理由 —— 那是把安全性押在所有人的自律上。

```js
function esc(t){                                  // 先跳脫
  return t.replace(/&/g,"&amp;").replace(/</g,"&lt;")
          .replace(/>/g,"&gt;").replace(/"/g,"&quot;");
}
el.innerHTML = renderMd(src);                     // renderMd 內部逐段 esc 之後才組標籤
```

**判準：`innerHTML` 的右邊只能是「你自己組出來的標籤 ＋ 已跳脫的文字」，不能有任何一段原文直通。**
不需要標籤的地方一律用 `textContent`（它天生安全）。

驗收方式**要實跑**：注入 `<img src=x onerror="window.__pwned=1">`，
檢查 `window.__pwned` 是否仍為 `false`、容器內 `img` 數是否為 0。
讀 code 說「有跳脫」不算驗過。

### ④ 資料是衍生產物 → 不進版控，由 CI 生成

網頁吃的索引（`gallery_data.js` 這類）是掃描來源檔產生的**衍生投影**。
它進版控就會有第二份真相，而落後的那份**不會叫**：網頁照開，只是少一筆。

⇒ 索引 `.gitignore`，線上版由 CI 每次 push 重生成並部署。
**什麼時候該把它交給 CI、怎麼判斷 → [`CI_Standards.md`](CI_Standards.md)。**

配套：**資料檔缺席時要明講**，不要留白畫面。

```js
if(!DATA || !Array.isArray(DATA.items)){
  $("sub").textContent = "✗ 讀不到 gallery_data.js —— 請先跑：python build_gallery.py";
  return;                                  // 空畫面會被讀成「畫廊是空的」
}
```

---

## 🎨 版面與樣式

### 主題：明講，不要跟系統走

`prefers-color-scheme` 會讓同一份內容在不同人的螢幕上長成兩種東西，
而「看起來怪怪的」是最難查的一類回報。**固定一套配色，並顯式塗 `body` 背景**
（透明的 body 在嵌入情境下會借到宿主的底色）。

```css
:root{ --bg:#000; --panel:#0d0d10; --fg:#fff; color-scheme: dark; }
body{ background:var(--bg); color:var(--fg); }   /* 不可省 */
```

`color-scheme` 要一起宣告 —— 否則捲軸與表單控件仍是淺色，深色頁上會出現一條白捲軸。

### Grid / Flex 內部捲動：三件事要一起做，少一件就會被剪掉

grid / flex 子項的預設最小尺寸是**內容大小**，所以「讓左右兩欄各自捲動」很容易寫成半套。

```css
.sheet{ display:grid; grid-template-columns:minmax(0,1fr) minmax(340px,440px); max-height:92vh;
        overflow:hidden; }
.sheet .body{ overflow-y:auto; min-height:0; max-height:92vh; }   /* 三個都是機制，不是保險 */
```

> ⚠ **`max-height` 不會約束 grid 的列高。** 容器高度**不確定**（只有 `max-height`）時，
> 列仍然依內容撐開；容器再把自己夾到 `max-height` 並用 `overflow:hidden` **剪掉**超出的部分 ——
> 而被剪掉的是**整列**，所以旁邊那張與長度無關的圖也會一起被切。
> `min-height:0` 治不了這個：它讓子項「可以」縮，但沒有任何東西在壓縮它。
>
> ⇒ **會捲動的那一欄自己也要有上限**，列高才等於 `max(圖, 正文) ≤ 上限`。
>
> 🩸 2026-08-21 畫廊：正文 1283px 的展品，grid 列高 1283 而 `.sheet` 只有 781 ⇒
> 圖被切掉上緣 252px、下方留一片黑。回報進來時看起來像「圖片顯示有問題」，
> **但圖完全是無辜的** —— 撐大版面的是它旁邊那欄的字。

### 寬度與可讀性

中文正文一行超過**約 45 字**就開始難讀。文字欄給上限（`minmax(340px,440px)`），
不要讓它跟著視窗長 —— 欄寬放任變寬會讓長句在掃視時斷行。

### 響應式

窄螢幕（`max-width:900px`）把兩欄疊成上下，並把圖高收掉一半 ——
否則手機上要捲很久才看得到字。**寬表格、程式碼區塊、圖表各自 `overflow-x:auto`**，
頁面本體永遠不橫向捲。

---

## 🔗 狀態與連結

**逛到一半的畫面要能貼給別人。** 把檢視狀態寫進網址查詢字串：

```
index.html?view=latest&n=20
index.html?sec=Portraits&q=gura
index.html?work=summit-masthead-bet&ch=002
```

讀網址 → 還原狀態 → 每次操作寫回網址（`history.replaceState`）。
這比任何「分享按鈕」都便宜，而且它同時讓 bug 回報變得可複現 ——
對方貼網址就等於貼了完整重現步驟。

⚠ **不要把個人資料或敏感值放進查詢字串**（會進瀏覽歷史、進 referer）。

---

## 🧾 檔案本身

| 項目 | 規則 |
|---|---|
| **行尾** | 沿用該檔既有的（本 repo 的 `index.html` 是 CRLF、`.py` 是 LF）。用腳本改檔一律 `read_bytes()` / `write_bytes()` —— `read_text()` 的 universal-newline 會把 CRLF 靜默吃成 LF，而 `core.autocrlf=true` 讓那個改動**在 `git diff` 裡看不見** |
| **編碼** | UTF-8 無 BOM；`<meta charset="utf-8">` 一定要有 |
| **註解** | 檔頭寫「區塊職責 / 物理意義 / 數值影響 / 設計取捨」，規範見 [`Code_Comment_Standards.md`](Code_Comment_Standards.md)。**設計取捨那段特別重要** —— 上面每一條硬規則都長得像「可以優化掉的贅寫」，沒寫理由就會被下一個人優化掉 |
| **機械產物** | 檔頭第一行明寫「由 X 產生，手改無效」 |

---

## 🧪 驗收：實跑，不要讀 code

靜態頁沒有測試框架，但**它有一個可以被機器問的 DOM**。最低限度：

1. **起一個本機 server**（`python -m http.server`）真的載入頁面 ——
   `file://` 與 HTTP 的差異只有實載才看得出來
2. 用 JS 量**幾何**而不是看截圖：兩欄是否真的並排（`imgRect.right <= bodyRect.left`）、
   長內容是否**內部**捲動（`scrollHeight > clientHeight`）
3. 跑一次 **XSS 探針**（見硬規則③）
4. 檢查**空資料 / 缺檔**的畫面：那是使用者第一次開頁最可能遇到的狀態

> 🩸 「截圖看起來對」不等於對：截圖不會告訴你右欄是**撐爆版面**還是**內部捲動**，
> 兩者在一張靜態圖上長得一模一樣。

#### 樣本要照「撐大版面的那個維度」挑，不是照直覺挑

版面 bug 的觸發條件是**某個維度的極端值**，而那個維度不一定是你正在修的東西。

🩸 2026-08-21：修圖片顯示時，樣本挑了全庫**圖片比例**的極端（最橫 1.79 / 最直 0.67），
八個展品全過。但真正會爆的是**正文最長**的那幾件 —— 因為撐大列高的是字不是圖。
**我驗的是我改的東西，不是會壞的東西。**

⇒ 開驗之前先問一句：**這個版面是被誰撐大的？** 然後照那個維度排序取前幾名。
可以機械化：`items.sort(by => 正文長度).slice(0,6)` 比人工挑「看起來很長的那幾個」可靠。

---

## 📚 延伸

| 主題 | 文件 |
|---|---|
| **什麼時候該把建置交給 CI** | [`CI_Standards.md`](CI_Standards.md) |
| 註解規範 | [`Code_Comment_Standards.md`](Code_Comment_Standards.md) |
| 用 python 腳本改 `.html` / `.css` | [`Python_Coding_Standards.md`](Python_Coding_Standards.md) |
| 文件與 AI 可讀性 | [`AI_READABILITY_GUIDELINES.md`](AI_READABILITY_GUIDELINES.md) |
