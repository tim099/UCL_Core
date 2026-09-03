---
title: JSON 讀寫規範 (JSON / UCL_JsonData Coding Standards)
description: UCL_Core 的 JSON 讀寫硬規則 — 已知 schema 一律 typed model、JsonData 安全 getter 對照、UnityJsonSerializable 的六個坑（bool 變字串／空 List 鍵消失／巢狀不必手刻…）與 round-trip 驗收協議。動任何 JSON 讀寫前先讀本檔。
tags: [json, jsondata, unityjsonserializable, typed-model, serialization, coding-standards]
aliases: [json 規範, JsonData, UCL_JsonData, typed model, 序列化規範, 反序列化, round-trip]
target_audience: [AI_Agent, Gameplay_Programmer, Tools_Maintainer]
last_updated: 2026-08-30
related:
  - Coding_Standards.md | C# Coding Standards | C# 其他共用規範
  - Python_Coding_Standards.md | Python 撰寫規範 | python 端讀同一批 JSON 的規則
  - Code_Comment_Standards.md | 程式碼註解規範 | 註解與文件化原則
---

# 🧱 JSON 讀寫規範

> 一句話：**JSON 的錯幾乎都不會叫。**
> 鍵名打錯不是編譯錯、也不是執行錯 —— 只會讀回 `0` / `""` / `false`，
> 而那些預設值長得跟「這件事沒發生」一模一樣。
> ⇒ 本檔的每一條規則，目的都是**把靜默的錯換成大聲的錯**（最好是編譯期就喊）。

適用範圍：UCL_Core 與消費端 repo 裡所有 C# 的 JSON 讀寫。
python 端讀同一批檔的規則見 [`Python_Coding_Standards.md`](Python_Coding_Standards.md)；
**跨語言的檔（C# 寫、python 讀）額外受本檔 §3.4 的 bool 規則約束。**

> [!WARNING]
> ⛔ **`SCP_Core/**` 與 Senate 不適用本檔的 API 部分** —— 那邊沒有 Unity，
> `UnityJsonSerializable` 與 `UCL.Core.JsonLib.JsonData` 都不存在，`System.Text.Json` 也用不了
> （Unity 不吃 NuGet）。⇒ **那側一律走 `SCP_Json`**，規範在
> **`<SCP_Core>/Docs~/Coding_Standards.md` §2**。
>
> **判準是「這段碼將來會不會進 SCP_Core」，不是「它現在放在哪」** —— 會的話現在就用 `SCP_Json`，
> 不要「搬的時候再改」（搬家那天要同時處理「換 JSON 層」與「拆宿主依賴」，而兩者的失敗互相遮蔽）。
>
> ⭐ 但本檔的**判準**兩邊都成立，而且 `SCP_Json` 是照著它們設計的：已知 schema 走 typed model
> （`SCP_JsonMapper`）、讀不到要能跟「讀到空值」分辨（`SCP_JsonData` 的 `Missing` 是型別不是空值）、
> 未知欄位要原樣寫回。**換的是 API，不是規則。**

---

## 1. 硬規則：已知 schema 一律 typed model

- 已知 schema **一律**用具名 C# class 承載（繼承 `UCL.Core.JsonLib.UnityJsonSerializable`），
  讓欄位、預設值與使用點可以被編譯器檢查。
- **不要**在業務流程裡裸用 `JsonData` 的字串索引逐鍵讀寫已知欄位。
- 同一個結構**只准有一個 class**。兩處各定義一份 ＝ 兩份真相，而兩邊都不會報錯。
- class 放哪：**只有一個 Cmd 用 ⇒ 放那個 Cmd 檔內**（Tim 2026-08-21 拍板）；
  跨 Cmd／跨 Page 共用 ⇒ 獨立檔案，放在**資料的主人**旁邊（例：`_screenstream/_config.json`
  的 model 放 `MediaAdmin/`，因為 daemon 是它的另一個讀寫端）。

```csharp
// ✅ 已知欄位走 typed model —— 打錯欄位名是編譯錯
var aCfg = UCL_ScreenStreamConfig.Load(aPath);
if (aCfg != null && aCfg.enabled) StartMontage();

// ❌ 逐鍵讀：打錯 "enabled" 不會有任何人喊，只會永遠當成沒在錄影
bool aOn = aJd != null && aJd.Contains("enabled") && aJd.GetBool("enabled");
```

> 🩸 為什麼值得一條硬規則：`_screenstream/_config.json` 曾有**四個 C# 讀寫端各自逐鍵解析**
> （Page 讀＋寫、`Cmd_StreamWatch` 讀 4 處、OCR supervisor、STT supervisor）。
> 同一個鍵在四處各打一次字，而其中一處的舊檔遷移（`ocr_y_pct` → `ocr_y_bottom_pct`）
> **只有 Page 做** ⇒ 同一份 config，畫面上顯示的辨識帶與 worker 實際吃的不是同一條。
> 兩邊都能運作、都不報錯。（2026-08-21 收斂成一份 model。）

### 什麼時候仍然可以用 `JsonData`（邊界層）

三種，且**必須在註解寫明理由**：

| 情況 | 例 |
|---|---|
| 解析**外部**產物且形狀不穩定 | 引擎 stdout、第三方 API 回應 |
| **保存未知／可擴充欄位**（無損 round-trip） | 有別的寫入端會加鍵（見 §3.6） |
| migration 期間兩種形狀並存 | 舊陣列形 vs 新物件形（見 §3.3） |

⚠ 外部產物的形狀「不穩定」不等於「不該有 model」——
`sculpt.py` 的 stdout 有明確契約，就該用 model 寫下來（見 §5 血證）。
判準是「**這個形狀有沒有一個擁有者**」，不是「它從哪裡來」。

---

## 2. `JsonData` API：安全 getter vs 過時隱式轉換

### 2.1 一律用具名 getter（帶預設值）

| 要拿 | 用 | 缺鍵／型別不符時 |
|---|---|---|
| `bool` | `GetBool(key, def)` | 回 `def`（**不丟例外**） |
| `int` | `GetInt(key, def)` | 回 `def` |
| `float` | `GetFloat(key, def)` | 回 `def` |
| `double` | `GetDouble(key, def)` | 回 `def` |
| `string` | `GetString(key, def)` | 回 `def` |

### 2.2 ⛔ 隱式轉換已 `[Obsolete]`（`CS0618`）—— 不准再用

```csharp
bool aOn = (bool)aJd["enabled"];      // ❌ CS0618；型別不是 Boolean 時**丟 InvalidCastException**
bool aOn = aJd.GetBool("enabled");    // ✅
```

`implicit operator bool/int/float/double(JsonData)` 全部標了 `[Obsolete]`，
理由寫在 `UCL_JsonData.cs`：**implicit 轉換丟例外是 C# 設計禁忌**
（`int x = jsonData;` 靜靜編過、runtime 才炸）。下個 major 會翻成 `explicit`。

⚠ 換寫法時要知道**行為差一格**：舊路徑型別不符會 throw（呼叫端多半 `try/catch` 吞成 `false`），
新路徑直接回預設值。最終結果相同，差別是不再用例外當控制流。

> 🩸 2026-08-21：全 repo 有 **12 處** `CS0618: implicit operator bool` —— 分布在
> `Cmd_FreeTime` / `UCL_FreeTimeGating` / `UCL_OcrWorkerSupervisor`(×4) /
> `UCL_SttWorkerSupervisor` / `Cmd_Sculpture` / `UCL_ScreenStreamPage`(×4) /
> `Cmd_StreamWatch`(×3)。當天全部清成 **0**。

### 2.3 其他常用成員

| 成員 | 用途 | ⚠ |
|---|---|---|
| `JsonData.ParseJson(text)` | 字串 → JsonData | 失敗回 null／丟例外，**要接** |
| `Contains(key)` | 鍵在不在 | 「缺席」與「值為 false／空」是兩件事，需要分辨時只能靠它 |
| `IsArray` / `IsObject` | 形狀判定 | 寬容解析舊格式時用 |
| `new JsonData().ToArray()` | 造**空陣列** | 不呼叫 `ToArray()` 的空 JsonData 序列化時會整個消失（見 §3.5） |
| `ToJson()` / `ToJsonBeautify()` | 序列化 | **jsonl 一行一筆要用 `ToJson()`**；設定檔用 beautify |
| `GetJsonDic()` | 走訪所有鍵 | 未知鍵 passthrough 用（見 §3.6） |

---

## 3. `UnityJsonSerializable` 的六個坑

> 改用 typed model **不是純粹的重構** —— 序列化器的行為跟手搭 `JsonData` 不一樣，
> 而差異全部落在「編譯過、看起來對、但 wire format 變了」這一格。

### 3.1 欄位名就是 JSON 鍵名

`UnityJsonSerializable` 走 `FieldNameUnityVer`，它**只脫 `m_` 前綴**，其餘原樣輸出。
要沿用既有檔的鍵名（例 `session_id`），欄位就得叫 `session_id` ——
這時**刻意不走 `m_PascalCase` 慣例**，而且必須在 class 註解寫明為什麼，
否則下一個人會把它「修正」成 `m_SessionId`，然後鍵名跟著改。

⇒ 改欄位名 ＝ 改 wire format ＝ 改跨語言契約。有 python 讀取端時要同時改那邊。

### 3.2 巢狀結構**交給序列化器**，不要手刻解析

`List<T>`（T 也是 model）、`Dictionary<string,string>`、巢狀 model 欄位
**全部內建支援**：存取兩端都由 `JsonConvert` 遞迴處理。

```csharp
public class UCL_StreamWatchPrepared : UnityJsonSerializable
{
    public Dictionary<string, string> catchup_map = new Dictionary<string, string>();
    public List<string> catchup_unfilled = new List<string>();
}
public class UCL_StreamWatchHotspots : UnityJsonSerializable
{
    public List<UCL_StreamWatchHotspot> hotspots = new List<UCL_StreamWatchHotspot>();
}
```

> 🩸 2026-08-21：我在 `UCL_ScreenStreamConfig` 自己寫了一支 `ParseRegions` 逐筆解析
> `ocr_extra_regions` —— **Tim 當場指出那是序列化器本來就會做的事**。
> 手刻的代價不是多打幾行，是**同一個形狀多了第二種解讀**：
> 那支手刻版對 `h_pct` 缺席落 `0`，而 OCR supervisor 那邊落 `0.12`，兩邊各自都能跑。

**唯一該手刻的**是序列化器**認不出來的形狀**（migration shim），而且要窄：

```csharp
// 舊檔把一筆寫成 [y,h] 陣列 ⇒ base 眼裡沒有任何已知欄位、全部落 0，
// 而 h_pct=0 是一條沒有面積的辨識帶：worker 照跑、永遠零產出，
// 看起來跟「這段沒字幕」一模一樣。⇒ 要嘛正確轉換，要嘛出聲。
MigrateLegacyRegionArrays(iJson);   // 只處理 IsArray 的元素，物件形不碰
```

### 3.3 缺席的鍵 ⇒ 欄位保留初始值（這是「預設值只有一份」的來源）

`LoadFieldFromJson` 對 `!iData.Contains(fieldName)` 直接跳過 ⇒ 欄位維持宣告時的初值。

⇒ **預設值寫在 model 的欄位初值上，呼叫端不要再各自帶一份**。
呼叫端要不同的落點時，顯式寫在呼叫端**並附理由**（否則就是兩份預設值）。

⚠ 副作用：model 分不出「缺席」與「值等於預設值」。真的需要分辨時（例：遷移判定），
在 `DeserializeFromJson` 裡自己記一份 present-key 集合：

```csharp
[ATTR.UCL_HideInJson] HashSet<string> m_PresentKeys = new HashSet<string>();
public bool HasKey(string iKey) => m_PresentKeys != null && m_PresentKeys.Contains(iKey);
```

### 3.4 `bool` 會被寫成 `"True"` / `"False"` **字串**

UCL_Json 的舊慣例如此（序列化端走 `aValue.ToString()`）。
**C# 載入端雙接所以看不出差別** —— `LoadFieldFromJson` 的 bool 分支同時吃原生 bool
與 `"true"`（case-insensitive）。但**跨語言讀取端看得出**：
python `json.loads` 拿到字串 `"False"`，而它在 Python 裡是 **truthy**。

⇒ **檔案有非 C# 讀取端時，`override SerializeToJson()` 把 bool 寫回原生：**

```csharp
public override JsonData SerializeToJson()
{
    var aData = base.SerializeToJson();
    aData["enabled"] = new JsonData(enabled);      // 原生 bool，不是 "True"/"False"
    return aData;
}
```

| 血證 | 讀數 |
|---|---|
| 2026-08-18 `FreeTime/sessions/*.json` | 改 typed model 後 `"active":"False"` ⇒ `freetime.py` 的 `if not s.get("active")` 通過 ⇒ **提前收工的人被判成還在自由時間**，且完全不報錯（該 python 讀取端已於 2026-08-26 退役 —— 血證留著：教訓在「bool 序列化」不在那支工具） |
| 2026-08-21 `_screenstream/_config.json` | daemon 是 `if cfg.get("enabled")` ⇒ 若寫成字串就是**停不掉的錄影**。9 個 bool 全部 override 回原生，實跑回讀 0 個字串 bool |
| 2026-08-21 `prepared/*.json` | `auto_export` 被 `library.py export-watch` 讀 ⇒ 「刻意關掉」會被讀成「開著」 |

⛔ **別把這個 override 當樣板無腦套**：純 C# 內部使用的資料沿用舊慣例即可（載入端雙接）。
**判準是「有沒有別的語言在讀」**；而既有檔已是原生 bool 時，override 也是為了**不改變 wire format**。

### 3.5 空 `List<>` 會讓整個鍵**消失**

`SaveDataToJson` 的 IList 分支不會把空的 `JsonData` 標成 array ⇒ 該鍵被丟掉。

```csharp
// 只在空的時候補一個空陣列（非空交給 base，不重做一次）
if (ocr_extra_regions == null || ocr_extra_regions.Count == 0)
    aData["ocr_extra_regions"] = new JsonData().ToArray();
```

> 🩸 2026-08-21 round-trip 實測：原檔 `"ocr_extra_regions": []`，寫回後**鍵不見了**。
> 後果不會叫（python 端 `cfg.get(...,[])` 照樣拿到 `[]`）——
> 但檔案少了一個欄位而沒有人會發現，而下一次有人要加區域時，
> 「這個鍵原本在不在」已經無從對帳。

### 3.6 未知鍵會被**靜默吃掉**（有別的寫入端時必須處理）

typed model 只認得自己宣告的欄位。若別的寫入端（python daemon、外掛）加了新鍵，
而 C# 照 model 寫回去，那個鍵就消失了 —— 對方下次讀不到只好退回自己的預設值，
**而「退回預設值」看起來跟「本來就沒設定」一模一樣**。

```csharp
[ATTR.UCL_HideInJson] JsonData m_Unknown = null;    // 反射走訪會跳過 [UCL_HideInJson]

public override void DeserializeFromJson(JsonData iJson)
{
    base.DeserializeFromJson(iJson);
    m_Unknown = new JsonData();
    var aDic = iJson?.GetJsonDic();
    if (aDic != null)
    {
        var aKnown = KnownKeys();                    // 用反射列自己的欄位，不維護第二份清單
        foreach (var kv in aDic) if (!aKnown.Contains(kv.Key)) m_Unknown[kv.Key] = kv.Value;
    }
}

public override JsonData SerializeToJson()
{
    var aData = base.SerializeToJson();
    var aDic = m_Unknown?.GetJsonDic();
    if (aDic != null)
        foreach (var kv in aDic) if (!aData.Contains(kv.Key)) aData[kv.Key] = kv.Value;   // 已宣告欄位以 model 為準
    return aData;
}
```

### 3.6b 補充：欄位順序會變、「空就不寫鍵」會變成「一律寫」

- **鍵序**：base/derived 拆開後衍生類欄位可能排到最前面。兩端都按鍵取值，
  但 **diff 會整片變** —— 別把它誤讀成內容變了。
- **空值欄位**：手搭時常寫成「值為空就不寫這個鍵」，typed model 會**一律寫出預設值**。
  ⇒ 檔案會多出幾個鍵。**加鍵是相容的**（讀取端本來就用預設值判空），
  **少鍵才是查不出來的那種差異** —— 所以這個方向的變動可以接受，反向不行。

---

## 4. 驗收協議：**編譯過不算驗過**

改任何 JSON 讀寫，驗收都是同一套。缺哪一步就是只驗了一部分。

### 4.1 拿**真實的舊檔** round-trip，逐鍵比對

比對四件事，缺一不可：**遺失的鍵／新增的鍵／值變動／型別變動**，
再加一項跨語言檢查：**有沒有被寫成 `"True"`/`"False"` 字串的 bool**。

### 4.2 走**生產寫入路徑**，不要只呼叫 `SerializeToJson`

直接呼叫序列化只驗了 model；走真正那條寫入函式才會連 merge 語意、
`ApplyEnabled` 這類連動規則、落檔編碼一起驗到。

```bash
# 例：透過真的那條寫入路徑寫一次（值不變 ⇒ 只驗形狀）
senate ucmd run Invoke --persona <me> --arg type=<Page 型別> --arg member=SetStreamTitle \
    --arg "paramTypes=System.String;System.String;System.String" --arg "args=<同值>;null;<me>"
```

### 4.3 沒有舊檔時：依**改動前的寫入端逐鍵**重建探針

`Cmd_Invoke` 可以打私有 static（`nonPublic=true`），四步就能把結果吐成檔案來比：

```bash
senate ucmd run Invoke --persona <me> --arg type=<Cmd 型別> --arg member=LoadXxx \
    --arg nonPublic=true --arg "paramTypes=System.String" --arg "args=<key>" --arg storeAs=o
senate ucmd run Invoke --persona <me> --arg target='$o'  --arg member=SerializeToJson --arg storeAs=jd
senate ucmd run Invoke --persona <me> --arg target='$jd' --arg member=ToJsonBeautify --arg storeAs=txt
senate ucmd run Invoke --persona <me> --arg type=System.IO.File --arg member=WriteAllText \
    --arg "paramTypes=System.String;System.String" --arg "args=<輸出路徑>;\$txt"
```

⚠ `Cmd_Invoke` 的 `paramTypes` / `args` 是**分號**分隔；空字串參數要寫 `null`（`;;` 會被吃掉）。
⚠ 它的回傳值只印到 Editor console ⇒ 想看內容就用上面第四步落檔。

### 4.4 每一種**形狀**都要驗，不是每一個欄位

集合欄位至少三種形狀：**空 / 正常 / 舊格式**。只驗一種等於只驗三分之一。

> 🩸 2026-08-21：`ocr_extra_regions` 空的時候鍵會消失、舊陣列形會全部落 0 ——
> 兩個 bug 都不在「正常」那條路上。同一天另一個教訓同形：
> 修版面 bug 時樣本按「圖片比例」取極端，八件全過，而會爆的是**正文最長**的那幾件。
> ⇒ 開驗之前問：**這個東西是被哪個維度撐爆的？** 然後照那個維度取樣。

### 4.5 順序陷阱：`recompile` 回報的 `errors=0` 可能是舊快照

改完 `.cs` 送 `recompile` 之後，`errors=0` 不代表你的新 code 編過了。
判準見 skill `ucl-compile-error`：**`check_compile.py` 沒標 STALE 才算編過**。
🩸 2026-08-18 那隻 bool 就是在「recompile 回報 0 錯」之後才被 round-trip 抓到的。

---

## 5. 血證彙總（2026-08-21 那一批）

| 檔案 | 過去的形狀 | 收斂後 | 抓到什麼 |
|---|---|---|---|
| `_screenstream/_config.json` | 4 個 C# 端各自逐鍵 | 1 個 model（`UCL_ScreenStreamConfig`） | 舊檔遷移只有 Page 做 ⇒ 畫面與 worker 讀不同的辨識帶；空 List 鍵消失 |
| `sculpt.py` stdout | `ReadStr/ReadInt/ReadBool` 逐鍵 | `UCL_SculptEngineResult`＋巢狀 `exhibit` | **引擎回報就是結算依據** —— 鍵名打錯回 0 ⇒ 像「一個 voxel 都沒放下」而錢已經花了 |
| StreamWatch 五份檔 | 7 個 step 各打一次鍵名 | 5 個 model（class 放 Cmd 檔內） | `settled_at` 曾被讀成 `ended_at` ⇒ 回傳檔印「結束時刻未記」這句**假話** |

round-trip 讀數（全部 0 遺失鍵 / 0 值變動 / 0 型別變動 / 0 字串 bool）：
`_config.json` 34→40 鍵、`prepared` 14→14、`session` 25→33、`hotspots` 巢狀 2 筆 × 8 欄。

---

## 📚 延伸

| 主題 | 文件 |
|---|---|
| C# 其他共用規範（字串 key／外部 Process／letters 路徑） | [`Coding_Standards.md`](Coding_Standards.md) |
| python 端讀同一批 JSON | [`Python_Coding_Standards.md`](Python_Coding_Standards.md) |
| 註解怎麼寫（本檔每條規則都要求附理由） | [`Code_Comment_Standards.md`](Code_Comment_Standards.md) |
| 改完 .cs 怎麼確認真的編過 | skill `ucl-compile-error` |
| 持久化資料（Unity asset 那一類，不走本檔） | skill `ucl-create-asset` |
