
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 05/05 2026
// 區塊職責：本檔提供「驗證 UCL_Asset JSON 檔格式」的 Agent Command 實作。
// 物理意義：給定 (assetType, assetId)，讀原始 JSON 檔 → 透過 UCL_Asset loader 反序列化成 C# object →
//          再 SerializeToJson() roundtrip 一次，把兩份 JSON canonical 化（排序 keys + beautify）後比對。
//          能偵測：
//            (a) 原檔有但 loader 不認識的欄位（被靜默丟棄）→ 通常是欄位名拼錯
//            (b) 原檔沒但 loader 補上預設值的欄位 → workflow 漏寫
//            (c) 同 key 但值不一致（enum 拼錯 / 型別轉換失敗 → 退回預設）
// 數值影響：純讀取，不修改原檔。執行成功會在 outputPath 寫一份 markdown report；
//          當 verdict ≠ PASS 時，額外在 fixedPath 寫 roundtrip 過後的 .fixed.json 供 agent 比對 / 採用。
#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UCL.Core.JsonLib;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>
    /// Agent Command：驗證單一 UCL_Asset JSON 檔的格式完整性。
    ///
    /// 流程：
    /// <list type="number">
    ///   <item>讀原檔 → originalRaw / originalJson</item>
    ///   <item>UCL_Asset.GetAsset(id, useCache:false) → 強制重新解析磁碟內容</item>
    ///   <item>asset.SerializeToJson() → roundtripJson</item>
    ///   <item>對兩者作 canonical（sort keys + beautify）→ 比對</item>
    /// </list>
    ///
    /// Verdict：
    /// <list type="bullet">
    ///   <item><c>PASS</c>：raw 完全相同（canonical 必然也相同）</item>
    ///   <item><c>FormattingOnly</c>：raw 不同但 canonical 相同（純格式 / 排序差異）— 安全，可採用 .fixed.json</item>
    ///   <item><c>SchemaDiff</c>：canonical 不同（欄位增 / 減 / 值改）— 真實格式問題，需要對照 diff 修原檔</item>
    /// </list>
    ///
    /// 參數：
    /// <list type="bullet">
    ///   <item><c>assetType</c>（必填）：C# Type 名稱，例 <c>RCG_ItemData</c></item>
    ///   <item><c>assetId</c>（必填）：asset ID，例 <c>SoundOfLife</c></item>
    ///   <item><c>outputPath</c>（選填）：報告檔路徑，預設 <c>AgentCommands/asset_format_check_&lt;type&gt;_&lt;id&gt;.md</c></item>
    ///   <item><c>fixedPath</c>（選填）：roundtrip JSON 路徑，預設與 outputPath 同目錄、副檔名 .fixed.json</item>
    ///   <item><c>verbose</c>（選填，預設 false）：true 時報告附完整原檔與 roundtrip 內容</item>
    /// </list>
    /// </summary>
    public class Cmd_ValidateAssetFormat : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "ValidateAssetFormat";

        public override string ShortDescription =>
            "Round-trip serialize/deserialize a UCL_Asset to detect schema or formatting issues in its JSON file.";

        public override string ArgsSchema =>
            "assetType=Asset C# type name (e.g. RCG_ItemData)\n" +
            "assetId=Asset ID, no extension (e.g. SoundOfLife)\n" +
            "outputPath=Report markdown path (default AgentCommands/asset_format_check_<type>_<id>.md)\n" +
            "fixedPath=Roundtrip JSON path written when verdict != PASS (default sibling of outputPath, .fixed.json)\n" +
            "verbose=true|false (default false) - include full original / roundtrip content in report\n" +
            "checkRefs=N (default 0) - BFS depth to validate that referenced sub-assets exist on disk; 0=off, 1=direct refs only, 2+=walk further\n" +
            "ignoreEmptyIds=true|false (default true) - empty asset entry IDs (\"\") are treated as 'intentionally unset' and skipped";

        public override string HelpURL =>
            "ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_ValidateAssetFormat.md";

        // ===== Verdicts =====

        private enum Verdict { PASS, FormattingOnly, SchemaDiff, Error }

        // 區塊職責：取得 Unity 專案根目錄（CardGame/）
        // 物理意義：Application.dataPath 為 CardGame/Assets，往上 1 層即 Unity project root。
        // 數值影響：所有 outputPath / fixedPath 相對基準都以這裡為準。
        private static string ProjectRoot
        {
            get { return Path.GetFullPath(Path.Combine(Application.dataPath, "..")).Replace('\\', '/'); }
        }

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            // 區塊職責：早期解析必要參數以便 outer catch 能寫 report
            // 物理意義：outer try/catch 需要 absReport / typeName / id 才能寫錯誤檔；
            //          若連這幾個都拿不到，至少 Debug.LogError 一下不會無聲消失
            // 數值影響：純讀 args，不修改任何狀態
            string assetTypeName = GetArg(args, "assetType", null);
            string assetId = GetArg(args, "assetId", null);
            string earlyOutputPath = GetArg(args, "outputPath", null);
            if (string.IsNullOrEmpty(earlyOutputPath))
            {
                earlyOutputPath = $"AgentCommands/asset_format_check_{assetTypeName ?? "<no-type>"}_{assetId ?? "<no-id>"}.md";
            }
            string earlyAbsReport = ToAbsolute(earlyOutputPath);

            try
            {
                await ExecuteCore(args, token, assetTypeName, assetId);
            }
            catch (Exception ex)
            {
                // 區塊職責：把任何沒被內部 catch 接住的例外寫進 report，含完整 stack trace
                // 物理意義：以前 NRE 之類會直接被 Runner 記成 Failed 並丟進 LastRunError 欄位（只有 Message），
                //          agent 看不到 stack。包了 outer try/catch 後，agent 拿到 report 就有完整定位資訊
                // 數值影響：寫 markdown 報告 + LogError；不影響其他指令
                string fullDump = $"Unhandled exception:\n{ex.GetType().FullName}: {ex.Message}\n\n{ex.StackTrace}";
                if (ex.InnerException != null)
                {
                    fullDump += $"\n\nInner: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}\n{ex.InnerException.StackTrace}";
                }
                WriteErrorReport(earlyAbsReport, assetTypeName ?? "<unknown>", assetId ?? "<unknown>", fullDump);
                throw; // 讓 Runner 也記成 Failed，queue 內保留以便重試
            }
        }

        private async UniTask ExecuteCore(Dictionary<string, string> args, CancellationToken token,
            string assetTypeName, string assetId)
        {
            // ============ 解析參數 ============
            string outputPath = GetArg(args, "outputPath", null);
            string fixedPath = GetArg(args, "fixedPath", null);
            bool verbose = string.Equals(GetArg(args, "verbose", "false"), "true", StringComparison.OrdinalIgnoreCase);
            // 區塊職責：解析「引用檢查」相關參數
            // 物理意義：checkRefs > 0 時會反射掃 asset 內所有 UCLI_AssetEntry，依 BFS 深度
            //          逐個查 File.Exists 判定有無遺失。0 = 完全跳過；1 = 只查直接引用；2+ = 跳到孫子
            // 數值影響：每深一層額外載入 N 個 sub-asset，會吃額外 IO 與時間
            int checkRefs = TryParseInt(GetArg(args, "checkRefs", "0"), 0);
            bool ignoreEmptyIds = !string.Equals(GetArg(args, "ignoreEmptyIds", "true"), "false", StringComparison.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(assetTypeName) || string.IsNullOrWhiteSpace(assetId))
            {
                Debug.LogError("[Cmd:ValidateAssetFormat] Missing required args: assetType / assetId");
                return;
            }

            // 預設輸出路徑（report + fixed json）
            if (string.IsNullOrEmpty(outputPath))
            {
                outputPath = $"AgentCommands/asset_format_check_{assetTypeName}_{assetId}.md";
            }
            if (string.IsNullOrEmpty(fixedPath))
            {
                string baseNoExt = outputPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                    ? outputPath.Substring(0, outputPath.Length - 3)
                    : outputPath;
                fixedPath = baseNoExt + ".fixed.json";
            }

            string absReport = ToAbsolute(outputPath);
            string absFixed = ToAbsolute(fixedPath);

            // ============ 解析 Type ============
            Type assetType = ResolveTypeByName(assetTypeName);
            if (assetType == null)
            {
                WriteErrorReport(absReport, assetTypeName, assetId,
                    $"Cannot resolve C# Type from name: '{assetTypeName}'. Check spelling and that the assembly is loaded.");
                return;
            }

            // ============ 取得原檔路徑 ============
            string assetAbsPath = TryGetAssetAbsPath(assetType, assetId);
            if (string.IsNullOrEmpty(assetAbsPath) || !File.Exists(assetAbsPath))
            {
                WriteErrorReport(absReport, assetTypeName, assetId,
                    $"Asset file not found. Tried: '{assetAbsPath ?? "(null)"}'. " +
                    $"Check assetId is correct and the module containing this asset is loaded.");
                return;
            }

            string assetRelPath = ToProjectRelative(assetAbsPath);

            // ============ 讀原檔 + 解析 ============
            string originalRaw;
            JsonData originalJson;
            try
            {
                originalRaw = File.ReadAllText(assetAbsPath, new UTF8Encoding(false));
                originalJson = JsonData.ParseJson(originalRaw);
            }
            catch (Exception e)
            {
                WriteErrorReport(absReport, assetTypeName, assetId,
                    $"Failed to read or parse original JSON file '{assetRelPath}': {e.Message}");
                return;
            }

            // ============ Roundtrip：強制重新讀檔 → 再 SerializeToJson ============
            // useCache=false 會走 CreateData(id) 路徑，從磁碟新讀一份；避免拿到 cache 內已修改的 in-memory state
            //
            // 區塊職責：在 GetAsset / SerializeToJson 期間掛 logMessageReceived，
            //          攔截 loader 內部丟到 Unity Console 的 Error / Exception 訊息
            // 物理意義：UCL_Asset loader 對「引用的 sub-asset 不存在」這類錯誤是吞例外 + Debug.LogException，
            //          不會讓上層方法失敗。這些錯誤對診斷「為什麼欄位掉了 / 為什麼值跑掉」極度關鍵
            //          → 必須收集到 report 裡，否則 agent 拿到報告也看不出毛病
            // 數值影響：用 lock 保護 list，過程中 Console 還是會看到原始 log（沒有壓制）
            var capturedLogs = new List<string>();
            var logLock = new object();
            Application.LogCallback logHandler = (string condition, string stackTrace, LogType type) =>
            {
                if (type != LogType.Error && type != LogType.Exception) return;
                lock (logLock)
                {
                    capturedLogs.Add($"[{type}] {condition}\n{stackTrace}");
                }
            };

            object asset = null;
            JsonData roundtripJson = null;
            string fatalError = null;

            Application.logMessageReceived += logHandler;
            try
            {
                asset = TryGetAssetFresh(assetType, assetId);
                if (asset == null)
                {
                    fatalError = $"UCL_Asset.GetAsset returned null for type='{assetTypeName}', id='{assetId}'. " +
                                 $"This likely means the loader threw an unrecoverable parse error. " +
                                 $"See Captured Errors section below for details.";
                }
                else
                {
                    var serializeMethod = asset.GetType().GetMethod("SerializeToJson", Type.EmptyTypes);
                    if (serializeMethod == null)
                    {
                        fatalError = $"Asset object of type '{assetType.Name}' does not have SerializeToJson() method.";
                    }
                    else
                    {
                        try
                        {
                            roundtripJson = serializeMethod.Invoke(asset, null) as JsonData;
                            if (roundtripJson == null) fatalError = "SerializeToJson returned null";
                        }
                        catch (Exception e)
                        {
                            fatalError = $"SerializeToJson failed: {e.Message}";
                        }
                    }
                }
            }
            finally
            {
                Application.logMessageReceived -= logHandler;
            }

            if (fatalError != null)
            {
                WriteErrorReport(absReport, assetTypeName, assetId, fatalError, capturedLogs);
                return;
            }

            // ============ Canonical 化 + 比對 ============
            string originalCanonical = Canonicalize(originalJson);
            string roundtripCanonical = Canonicalize(roundtripJson);
            string roundtripRaw = roundtripJson.ToJsonBeautify();
            // 標準化換行 + 結尾換行，避免行結尾差異被當成 diff
            originalRaw = NormalizeNewlines(originalRaw);
            roundtripRaw = NormalizeNewlines(roundtripRaw);
            if (!roundtripRaw.EndsWith("\n")) roundtripRaw += "\n";
            if (!originalRaw.EndsWith("\n")) originalRaw += "\n";

            Verdict verdict;
            if (string.Equals(originalRaw, roundtripRaw, StringComparison.Ordinal))
            {
                verdict = Verdict.PASS;
            }
            else if (string.Equals(originalCanonical, roundtripCanonical, StringComparison.Ordinal))
            {
                verdict = Verdict.FormattingOnly;
            }
            else
            {
                verdict = Verdict.SchemaDiff;
            }

            // ============ 寫 fixed.json（PASS 不寫，FormattingOnly / SchemaDiff 寫）============
            if (verdict != Verdict.PASS)
            {
                EnsureDir(absFixed);
                File.WriteAllText(absFixed, roundtripRaw, new UTF8Encoding(false));
            }

            // ============ Reference 完整性檢查（checkRefs > 0 才跑）============
            // 區塊職責：BFS 走 asset 內的 UCLI_AssetEntry 引用，檢查每個被引用的 sub-asset 是否真的存在
            // 物理意義：補足 schema check 看不到的問題 — 例如「欄位格式對但引用了不存在的 Tag」這種會在
            //          Editor 內噴 !File.Exists 例外但 schema 看不出來。讓 agent 一份報告就能拿到全貌
            // 數值影響：每深一層額外 IO；空 ID 視 ignoreEmptyIds 決定要不要報
            ReferenceCheckResult refCheck = ReferenceCheckResult.Skipped();
            if (checkRefs > 0 && asset != null)
            {
                refCheck = WalkReferences(assetType, assetId, asset, checkRefs, ignoreEmptyIds, token);
            }

            // ============ 寫 markdown report ============
            string report = RenderReport(
                assetTypeName, assetId, assetRelPath,
                verdict,
                originalCanonical, roundtripCanonical,
                originalRaw, roundtripRaw,
                outputPath, fixedPath, verbose,
                capturedLogs, refCheck);

            EnsureDir(absReport);
            File.WriteAllText(absReport, report, new UTF8Encoding(false));

            Debug.Log($"[Cmd:ValidateAssetFormat] {assetTypeName}/{assetId} → {verdict}. " +
                      $"Report: {outputPath}" + (verdict != Verdict.PASS ? $" + Fixed: {fixedPath}" : ""));

            await UniTask.CompletedTask;
        }

        // ===========================================================
        // Asset 取用 helpers
        // ===========================================================

        // 區塊職責：強制不走 cache，重新從磁碟解析 asset
        // 物理意義：cache 內可能是 Editor 內已被修改的 in-memory 版本（甚至 stale）；
        //          要驗證「磁碟上 JSON 檔的格式」必須走 useCache:false → CreateData(id) 路徑
        // 數值影響：每次呼叫此 Cmd 都會新建一個臨時 asset object（不放回 cache）
        private object TryGetAssetFresh(Type assetType, string id)
        {
            try
            {
                var utilGenericType = typeof(UCL_Util<>).MakeGenericType(assetType);
                var utilProp = utilGenericType.GetProperty("Util", BindingFlags.Public | BindingFlags.Static);
                object util = utilProp?.GetValue(null);
                if (util == null) return null;

                var getAsset = util.GetType().GetMethod("GetAsset", new Type[] { typeof(string), typeof(bool) });
                if (getAsset == null) return null;

                // useCache=false → 強制重新讀檔
                return getAsset.Invoke(util, new object[] { id, false });
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Cmd:ValidateAssetFormat] TryGetAssetFresh({assetType.Name},{id}) ex: {e.Message}");
                return null;
            }
        }

        private string TryGetAssetAbsPath(Type assetType, string id)
        {
            try
            {
                var config = UCL_ModuleService.Ins.GetAssetConfig(assetType, id);
                if (config != null && config.Exist)
                {
                    return config.AssetPath?.Replace('\\', '/');
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Cmd:ValidateAssetFormat] TryGetAssetAbsPath({assetType.Name},{id}) ex: {e.Message}");
            }
            return null;
        }

        // ===========================================================
        // Type 解析
        // ===========================================================

        // 區塊職責：依名稱在所有已載入 assembly 內找對應 Type
        // 物理意義：agent 寫的是 "RCG_ItemData" 之類短名，沒有 namespace；用 GetTypes 反向掃
        // 數值影響：找不到回 null（Cmd 會回報錯誤），找到的話交給後續 GetAsset 流程
        private Type ResolveTypeByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch { continue; }
                foreach (var t in types)
                {
                    if (t == null) continue;
                    if (string.Equals(t.Name, name, StringComparison.Ordinal)) return t;
                    if (string.Equals(t.FullName, name, StringComparison.Ordinal)) return t;
                }
            }
            return null;
        }

        // ===========================================================
        // Canonical 化（sort keys + beautify）
        // ===========================================================

        // 區塊職責：把 JsonData 轉成「結構等價即字元相同」的 canonical 字串
        // 物理意義：deep-sort 所有 dict 的 keys、用固定縮排，使「順序差異」「空白差異」都消失，
        //          只有真正的「欄位增 / 減 / 值變」才會在 canonical diff 中顯示
        // 數值影響：用於兩份 JsonData 的字串比對；canonical 相同 = schema 相同
        private static string Canonicalize(JsonData data)
        {
            JsonData sorted = DeepSortDict(data);
            return sorted.ToJsonBeautify();
        }

        // 區塊職責：遞迴排序 JsonData dictionary 的 keys
        // 物理意義：JsonData 內部對 dict 是有序集合（取決於插入順序），但語意上 JSON object 無序；
        //          做 canonical 比對前必須 deep-sort
        // 數值影響：產生一份新的 JsonData（不修改原物件）
        private static JsonData DeepSortDict(JsonData data)
        {
            if (data == null) return null;
            if (data.IsObject)
            {
                // 區塊職責：建一個空的 Dictionary 型 JsonData，並依 sorted keys 重新填入
                // 物理意義：JsonData 沒有 JsonData(JsonType) 建構子，必須用無參建構 + Init(JsonType)
                //          二步驟才能正確配置 m_Dic / m_ObjectList。否則 set_Item 會 NRE
                // 數值影響：產生新的 JsonData 物件（不修改原 data）
                var sorted = new JsonData();
                sorted.Init(JsonType.Dictionary);
                var keys = new List<string>();
                foreach (DictionaryEntry kv in (IDictionary)data) keys.Add(kv.Key.ToString());
                keys.Sort(StringComparer.Ordinal);
                foreach (var k in keys) sorted[k] = DeepSortDict(data[k]);
                return sorted;
            }
            if (data.IsArray)
            {
                var sorted = new JsonData();
                sorted.Init(JsonType.List);
                for (int i = 0; i < data.Count; i++)
                {
                    sorted.Add(DeepSortDict(data[i]));
                }
                return sorted;
            }
            // primitives: clone (JsonData is mutable but primitive value can be shared safely)
            return data;
        }

        private static string NormalizeNewlines(string s)
        {
            return s.Replace("\r\n", "\n").Replace("\r", "\n");
        }

        // ===========================================================
        // Markdown report
        // ===========================================================

        private static string RenderReport(
            string assetTypeName, string assetId, string assetRelPath,
            Verdict verdict,
            string originalCanonical, string roundtripCanonical,
            string originalRaw, string roundtripRaw,
            string reportRelPath, string fixedRelPath,
            bool verbose,
            List<string> capturedLogs,
            ReferenceCheckResult refCheck)
        {
            var sb = new StringBuilder();

            // ---- frontmatter ----
            sb.AppendLine("---");
            sb.AppendLine($"asset_type: {assetTypeName}");
            sb.AppendLine($"asset_id: {assetId}");
            sb.AppendLine($"verdict: {verdict}");
            sb.AppendLine($"generated: {DateTime.Now:yyyy-MM-ddTHH:mm:ss}");
            sb.AppendLine($"original_path: {assetRelPath}");
            if (verdict != Verdict.PASS) sb.AppendLine($"fixed_path: {fixedRelPath}");

            DiffStats stats = (verdict == Verdict.SchemaDiff)
                ? DiffStats.FromCanonicalDiff(originalCanonical, roundtripCanonical)
                : default;
            if (verdict == Verdict.SchemaDiff)
            {
                sb.AppendLine("field_diff:");
                sb.AppendLine($"  removed: {stats.Removed}  # original 有，loader 不認識（被丟棄）");
                sb.AppendLine($"  added: {stats.Added}  # loader 補了預設（原檔可能漏寫）");
            }
            int errCount = capturedLogs?.Count ?? 0;
            sb.AppendLine($"captured_error_count: {errCount}");
            // 區塊職責：把 reference 檢查結果摘要寫進 frontmatter，方便 agent 一眼判斷
            // 物理意義：reference_check 維度與主 verdict 獨立 — schema 可能 PASS 但 ref 仍有 missing
            // 數值影響：純顯示，不改其他欄位
            sb.AppendLine($"reference_check: {refCheck.Status}");
            if (refCheck.Status != "Skipped")
            {
                sb.AppendLine($"reference_depth: {refCheck.Depth}");
                sb.AppendLine($"reference_walked: {refCheck.Walked}");
                sb.AppendLine($"reference_missing: {refCheck.Missing}");
                sb.AppendLine($"reference_skipped_empty: {refCheck.SkippedEmpty}");
            }
            sb.AppendLine("---");
            sb.AppendLine();

            // ---- 摘要 ----
            sb.AppendLine($"# Asset Format Validation: `{assetTypeName}` / `{assetId}`");
            sb.AppendLine();
            sb.AppendLine($"**Verdict**: `{verdict}`");
            sb.AppendLine();
            sb.AppendLine(VerdictExplanation(verdict));
            sb.AppendLine();

            sb.AppendLine("## Files");
            sb.AppendLine();
            sb.AppendLine($"- Original (read-only): [`{assetRelPath}`](../{assetRelPath})");
            if (verdict != Verdict.PASS)
            {
                sb.AppendLine($"- Roundtrip output (loader's view): [`{fixedRelPath}`](../{fixedRelPath})");
            }
            sb.AppendLine();

            // ---- 行動建議 ----
            sb.AppendLine("## Recommended Action");
            sb.AppendLine();
            switch (verdict)
            {
                case Verdict.PASS:
                    sb.AppendLine("✅ No action needed. The JSON file roundtrips cleanly through the loader.");
                    break;
                case Verdict.FormattingOnly:
                    sb.AppendLine("ℹ️ **Pure formatting / ordering difference** — semantically identical.");
                    sb.AppendLine();
                    sb.AppendLine($"Adopt the canonicalised version by overwriting the original with `{fixedRelPath}`. ");
                    sb.AppendLine("This will normalise key order and indentation without changing any values.");
                    sb.AppendLine();
                    sb.AppendLine("```bash");
                    sb.AppendLine($"cp \"{fixedRelPath}\" \"{assetRelPath}\"");
                    sb.AppendLine("```");
                    break;
                case Verdict.SchemaDiff:
                    sb.AppendLine("⚠️ **Schema differences detected** — the loader's view differs from the source file.");
                    sb.AppendLine();
                    sb.AppendLine("Common causes:");
                    sb.AppendLine();
                    sb.AppendLine("- `removed` lines (in original, not in roundtrip) → loader did **not recognise** the field. Likely a typo or stale schema. Fix the field name in source.");
                    sb.AppendLine("- `added` lines (in roundtrip, not in original) → loader **filled in defaults**. Likely a missing required field in source. Add it explicitly to ensure intentional values.");
                    sb.AppendLine("- value changes → enum / type conversion failed and fell back to default. Check enum spelling, numeric type, null vs empty string.");
                    sb.AppendLine();
                    sb.AppendLine($"Inspect the diff below and `{fixedRelPath}` to decide which values are correct, then patch the original. " +
                                 $"If you trust the loader fully, you may overwrite original with `{fixedRelPath}` — but be aware that " +
                                 $"unrecognised fields will be permanently lost.");
                    break;
                case Verdict.Error:
                    sb.AppendLine("❌ Could not run the check. See error block above.");
                    break;
            }
            sb.AppendLine();

            // ---- Reference Integrity（checkRefs > 0 才有）----
            // 區塊職責：列出走訪到的所有引用，標示存在 / 遺失 / 略過
            // 物理意義：給 agent 看哪些 sub-asset 不存在，配合 captured errors 對照可快速定位 broken ref
            // 數值影響：純報告，不修改任何資料
            if (refCheck.Status != "Skipped")
            {
                sb.AppendLine($"## Reference Integrity (depth={refCheck.Depth})");
                sb.AppendLine();
                sb.AppendLine($"Walked **{refCheck.Walked}** outgoing references; " +
                              $"**{refCheck.Missing}** missing, **{refCheck.SkippedEmpty}** empty IDs skipped.");
                sb.AppendLine();
                if (refCheck.Entries.Count == 0)
                {
                    sb.AppendLine("(no references found within depth limit)");
                }
                else
                {
                    sb.AppendLine("| Status | Type | ID | Depth | Path / Note |");
                    sb.AppendLine("|---|---|---|:-:|---|");
                    foreach (var e in refCheck.Entries)
                    {
                        string icon = e.Status switch
                        {
                            "Exists" => "✓ exists",
                            "Missing" => "✗ **MISSING**",
                            "Empty" => "⚠ empty",
                            "Unknown" => "? unknown",
                            _ => e.Status,
                        };
                        string pathOrNote = string.IsNullOrEmpty(e.Path) ? (e.Note ?? "") : e.Path;
                        sb.AppendLine($"| {icon} | `{e.AssetType}` | `{e.Id}` | {e.Depth} | {pathOrNote} |");
                    }
                }
                sb.AppendLine();
                if (refCheck.Missing > 0)
                {
                    sb.AppendLine($"> ⚠️ **{refCheck.Missing} missing references** — these are likely the cause of any " +
                                  "`AssetConfig.GetJsonData ... !File.Exists` exceptions in the Captured Errors section. " +
                                  "Common fixes: create the missing asset, or correct the referenced ID in source.");
                    sb.AppendLine();
                }
            }

            // ---- 攔截到的 Console errors（loader 內部丟出的，最重要的診斷線索）----
            if (capturedLogs != null && capturedLogs.Count > 0)
            {
                sb.AppendLine("## Captured Errors During Parse / Serialize");
                sb.AppendLine();
                sb.AppendLine($"Unity Console emitted **{capturedLogs.Count}** Error / Exception entries while loading and " +
                              "re-serializing this asset. These are usually the **root cause** of any field changes you see in the diff above " +
                              "(e.g. a missing referenced sub-asset → the field falls back to default).");
                sb.AppendLine();
                for (int i = 0; i < capturedLogs.Count; i++)
                {
                    sb.AppendLine($"### Error #{i + 1}");
                    sb.AppendLine();
                    sb.AppendLine("```");
                    sb.AppendLine(capturedLogs[i].TrimEnd());
                    sb.AppendLine("```");
                    sb.AppendLine();
                }
            }

            // ---- diff（只 SchemaDiff 才有意義）----
            if (verdict == Verdict.SchemaDiff)
            {
                sb.AppendLine("## Canonical Diff");
                sb.AppendLine();
                sb.AppendLine("Unified diff between **canonicalized original** (left, `-`) and **canonicalized roundtrip** (right, `+`). " +
                              "Both forms have keys deep-sorted and 4-space indent so only schema differences remain.");
                sb.AppendLine();
                sb.AppendLine("```diff");
                sb.AppendLine(UnifiedDiff.Build(originalCanonical, roundtripCanonical, contextLines: 3));
                sb.AppendLine("```");
                sb.AppendLine();
            }

            // ---- verbose 才附完整內容 ----
            if (verbose)
            {
                sb.AppendLine("## Original (raw)");
                sb.AppendLine();
                sb.AppendLine("```json");
                sb.AppendLine(originalRaw.TrimEnd());
                sb.AppendLine("```");
                sb.AppendLine();

                sb.AppendLine("## Roundtrip (raw)");
                sb.AppendLine();
                sb.AppendLine("```json");
                sb.AppendLine(roundtripRaw.TrimEnd());
                sb.AppendLine("```");
                sb.AppendLine();
            }

            return sb.ToString();
        }

        private static string VerdictExplanation(Verdict v)
        {
            switch (v)
            {
                case Verdict.PASS:
                    return "Original raw text matches the roundtrip output exactly. Format is canonical and complete.";
                case Verdict.FormattingOnly:
                    return "Canonical form (sorted keys + beautified) is identical, but raw text differs. " +
                           "This usually means the source file has a different key order, indentation, or whitespace. " +
                           "No semantic difference — safe to adopt the roundtrip output.";
                case Verdict.SchemaDiff:
                    return "Canonical forms differ — there is at least one schema-level difference (added / removed field, " +
                           "or changed value). See diff below.";
                case Verdict.Error:
                    return "An error prevented the check from completing.";
            }
            return "";
        }

        // ===========================================================
        // Error report (early exit path)
        // ===========================================================

        private static void WriteErrorReport(string absReport, string assetTypeName, string assetId,
            string message, List<string> capturedLogs = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("---");
            sb.AppendLine($"asset_type: {assetTypeName}");
            sb.AppendLine($"asset_id: {assetId}");
            sb.AppendLine("verdict: Error");
            sb.AppendLine($"generated: {DateTime.Now:yyyy-MM-ddTHH:mm:ss}");
            sb.AppendLine($"captured_error_count: {capturedLogs?.Count ?? 0}");
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine($"# Asset Format Validation: `{assetTypeName}` / `{assetId}`");
            sb.AppendLine();
            sb.AppendLine("**Verdict**: `Error`");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine(message);
            sb.AppendLine("```");
            sb.AppendLine();

            if (capturedLogs != null && capturedLogs.Count > 0)
            {
                sb.AppendLine("## Captured Errors During Parse / Serialize");
                sb.AppendLine();
                sb.AppendLine($"Unity Console emitted **{capturedLogs.Count}** Error / Exception entries — these are " +
                              "almost certainly the underlying cause of the failure above.");
                sb.AppendLine();
                for (int i = 0; i < capturedLogs.Count; i++)
                {
                    sb.AppendLine($"### Error #{i + 1}");
                    sb.AppendLine();
                    sb.AppendLine("```");
                    sb.AppendLine(capturedLogs[i].TrimEnd());
                    sb.AppendLine("```");
                    sb.AppendLine();
                }
            }

            EnsureDir(absReport);
            File.WriteAllText(absReport, sb.ToString(), new UTF8Encoding(false));
            Debug.LogError($"[Cmd:ValidateAssetFormat] {assetTypeName}/{assetId} → Error: {message}");
        }

        // ===========================================================
        // 路徑 helpers
        // ===========================================================

        private static string ToAbsolute(string relOrAbs)
        {
            return Path.IsPathRooted(relOrAbs) ? relOrAbs : Path.Combine(ProjectRoot, relOrAbs);
        }

        private static string ToProjectRelative(string absPath)
        {
            if (string.IsNullOrEmpty(absPath)) return string.Empty;
            absPath = absPath.Replace('\\', '/');
            string root = ProjectRoot.Replace('\\', '/').TrimEnd('/') + "/";
            if (absPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return absPath.Substring(root.Length);
            }
            return absPath;
        }

        private static void EnsureDir(string absPath)
        {
            string dir = Path.GetDirectoryName(absPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        }

        private static string GetArg(Dictionary<string, string> args, string key, string defaultVal)
        {
            if (args == null) return defaultVal;
            return args.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : defaultVal;
        }

        private static int TryParseInt(string s, int defaultVal)
        {
            if (string.IsNullOrWhiteSpace(s)) return defaultVal;
            return int.TryParse(s, out var v) ? v : defaultVal;
        }

        // ===========================================================
        // Reference walker（BFS）
        // 區塊職責：從 root asset 出發，依 BFS 深度限制走訪所有 UCLI_AssetEntry，
        //          檢查每個被引用的 sub-asset 是否真的存在於某個 module 內
        // 物理意義：這是 schema 檢查看不到的層級 — 「欄位格式對」+「引用的 ID 字串對」+
        //          「但目標檔不存在」是常見的 broken state（例如 Tag 與 Item 同名 collision）
        // 數值影響：純讀取；每深一層需載入 sub-asset object（會觸發 loader 例外，被外層 logHandler 捕捉）
        // ===========================================================

        private struct ReferenceCheckResult
        {
            public string Status;          // "OK" | "Missing" | "Skipped"
            public int Depth;              // 設定的最大深度
            public int Walked;             // 實際走過的引用數（含 root 子層）
            public int Missing;            // 找不到的引用數
            public int SkippedEmpty;       // 因為 ID 為空被略過的引用數
            public List<RefEntry> Entries;

            public static ReferenceCheckResult Skipped()
                => new ReferenceCheckResult { Status = "Skipped", Entries = new List<RefEntry>() };
        }

        private class RefEntry
        {
            public string Status;     // "Exists" | "Missing" | "Empty" | "Unknown"
            public string AssetType;
            public string Id;
            public int Depth;
            public string Path;       // project-root-relative，僅 Exists 時非空
            public string Note;       // 補充說明（例如 cycle skipped）
        }

        private ReferenceCheckResult WalkReferences(Type rootType, string rootId, object rootAsset,
            int maxDepth, bool ignoreEmptyIds, CancellationToken token)
        {
            var result = new ReferenceCheckResult
            {
                Status = "OK",
                Depth = maxDepth,
                Entries = new List<RefEntry>(),
            };

            // BFS queue: (Type, ID, Depth, ParentObject)
            // visited 用 "Type:ID" key 防重複
            var queue = new Queue<(Type RefType, string Id, int Depth, object Holder)>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 第一步：把 rootAsset 內的引用收齊，丟進 queue（深度 1）
            EnqueueOutgoing(rootAsset, depth: 1, queue, ignoreEmptyIds, result);

            while (queue.Count > 0)
            {
                if (token.IsCancellationRequested) break;
                var (refType, refId, depth, _) = queue.Dequeue();
                if (depth > maxDepth) continue;

                string key = $"{refType.FullName}:{refId}";
                if (!visited.Add(key)) continue;  // already checked

                result.Walked++;

                var entry = new RefEntry
                {
                    AssetType = refType.Name,
                    Id = refId,
                    Depth = depth,
                };

                // 嘗試查 path
                string absPath = TryGetAssetAbsPath(refType, refId);
                if (!string.IsNullOrEmpty(absPath) && File.Exists(absPath))
                {
                    entry.Status = "Exists";
                    entry.Path = ToProjectRelative(absPath);
                }
                else
                {
                    entry.Status = "Missing";
                    entry.Note = absPath == null
                        ? "AssetConfig not found in any loaded module"
                        : $"File missing: {ToProjectRelative(absPath)}";
                    result.Missing++;
                }
                result.Entries.Add(entry);

                // 若還沒到 maxDepth，載入此 sub-asset 並收集它的引用
                if (depth < maxDepth && entry.Status == "Exists")
                {
                    object subAsset = TryGetAssetFresh(refType, refId);
                    if (subAsset != null)
                    {
                        EnqueueOutgoing(subAsset, depth: depth + 1, queue, ignoreEmptyIds, result);
                    }
                }
            }

            if (result.Missing > 0) result.Status = "Missing";
            return result;
        }

        // 區塊職責：用反射收集 obj 內所有 UCLI_AssetEntry，把 (Type, ID) 入 BFS queue
        // 物理意義：與 Cmd_ResolveAssetReferences 的 CollectAssetEntries 同一邏輯（複製）；
        //          這裡刻意不 refactor 共用，避免動到既存 Cmd 風險，後續可整合
        // 數值影響：僅 enqueue；空 ID 依 ignoreEmptyIds 決定要不要記為 SkippedEmpty
        private void EnqueueOutgoing(object obj, int depth,
            Queue<(Type RefType, string Id, int Depth, object Holder)> queue,
            bool ignoreEmptyIds, ReferenceCheckResult result)
        {
            var collected = new List<(Type, string)>();
            var fieldVisited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            CollectAssetEntries(obj, collected, fieldVisited);

            foreach (var (refType, refId) in collected)
            {
                if (string.IsNullOrEmpty(refId))
                {
                    if (ignoreEmptyIds)
                    {
                        result.SkippedEmpty++;
                        continue;
                    }
                    // 不忽略 → 記成 Empty entry（但不入 queue，因為沒 ID 可查）
                    result.Entries.Add(new RefEntry
                    {
                        Status = "Empty",
                        AssetType = refType?.Name ?? "<unknown>",
                        Id = "",
                        Depth = depth,
                        Note = "Empty ID (set ignoreEmptyIds=false to surface these)",
                    });
                    result.SkippedEmpty++;
                    continue;
                }
                if (refType == null) continue;
                queue.Enqueue((refType, refId, depth, obj));
            }
        }

        // 區塊職責：反射遞迴收集 UCLI_AssetEntry（複製自 Cmd_ResolveAssetReferences）
        // 物理意義：保持兩支 Cmd 獨立，避免相互依賴；後續若要抽共用 helper 再做
        // 數值影響：純讀取
        private void CollectAssetEntries(object obj, List<(Type, string)> outList, HashSet<object> fieldVisited)
        {
            if (obj == null) return;
            if (obj is string) return;
            Type t = obj.GetType();
            if (t.IsPrimitive || t.IsEnum) return;
            if (!t.IsClass && !t.IsValueType) return;
            if (obj is UnityEngine.Object) return;

            if (t.IsClass)
            {
                if (fieldVisited.Contains(obj)) return;
                fieldVisited.Add(obj);
            }

            if (obj is UCLI_AssetEntry entry)
            {
                if (entry.AssetType != null)
                {
                    outList.Add((entry.AssetType, entry.IsEmpty ? "" : entry.ID));
                }
                return;
            }

            if (obj is IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    CollectAssetEntries(item, outList, fieldVisited);
                }
                return;
            }

            var fields = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var f in fields)
            {
                if (f.IsNotSerialized) continue;
                if (f.IsDefined(typeof(NonSerializedAttribute), false)) continue;
                Type ft = f.FieldType;
                if (ft.IsPrimitive || ft.IsEnum || ft == typeof(string)) continue;

                // 區塊職責：尊重 UCL_ConditionalAttribute — 與 JsonConvert.SaveFieldsToJson 同一規則
                // 物理意義：欄位若被 [Conditional(其他欄位, ...)] 修飾且當前條件不成立，
                //          serializer 不會寫入 JSON；deserializer 也不會讀，但反序列化時建構子
                //          會給該欄位預設值（例如 RCG_StatusDropPoolGenData m_ID="Default"）→
                //          若 walker 不跳過會誤把這個未啟用的預設值報成 missing reference
                // 數值影響：跳過未啟用的條件欄位，與 loader 行為一致
                var aConditional = f.GetCustomAttribute<UCL.Core.PA.ConditionalAttribute>();
                if (aConditional != null && !aConditional.IsShow(obj)) continue;

                object val;
                try { val = f.GetValue(obj); }
                catch { continue; }
                if (val == null) continue;
                CollectAssetEntries(val, outList, fieldVisited);
            }
        }

        // ===========================================================
        // Diff stats（讀 unified diff 結果 → 統計 +/- 行數）
        // ===========================================================

        private struct DiffStats
        {
            public int Added;
            public int Removed;

            public static DiffStats FromCanonicalDiff(string a, string b)
            {
                string diff = UnifiedDiff.Build(a, b, contextLines: 0);
                int added = 0, removed = 0;
                foreach (var line in diff.Split('\n'))
                {
                    if (line.StartsWith("+") && !line.StartsWith("+++")) added++;
                    else if (line.StartsWith("-") && !line.StartsWith("---")) removed++;
                }
                return new DiffStats { Added = added, Removed = removed };
            }
        }

        // 區塊職責：在 Unity .NET Standard 環境補一個 ReferenceEqualityComparer
        // 物理意義：.NET 5+ 的 System.Collections.Generic.ReferenceEqualityComparer 在 Unity 沒有，
        //          但走訪物件圖時必須用 reference equality（避免「兩個內容相同的子物件」被當成同一個）
        // 數值影響：純 helper，不影響邏輯；僅給 fieldVisited HashSet 當比較器用
        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }

    // ===========================================================
    // 簡易 unified diff（LCS 為基底）
    // 區塊職責：產生 git-style unified diff 字串
    // 物理意義：兩段文字依行切分後，用 LCS 找出 longest common subsequence，
    //          其餘行標記為 `-`（刪除）/ `+`（新增），含 contextLines 行上下文
    // 數值影響：純字串輸出，不修改任何資料
    // ===========================================================
    internal static class UnifiedDiff
    {
        public static string Build(string a, string b, int contextLines = 3)
        {
            var aLines = (a ?? string.Empty).Replace("\r\n", "\n").Split('\n');
            var bLines = (b ?? string.Empty).Replace("\r\n", "\n").Split('\n');

            // 計算 LCS 表
            int n = aLines.Length, m = bLines.Length;
            var dp = new int[n + 1, m + 1];
            for (int i = n - 1; i >= 0; i--)
            {
                for (int j = m - 1; j >= 0; j--)
                {
                    if (aLines[i] == bLines[j]) dp[i, j] = dp[i + 1, j + 1] + 1;
                    else dp[i, j] = Math.Max(dp[i + 1, j], dp[i, j + 1]);
                }
            }

            // backtrack 產生 (op, line) 序列：op = ' ' (same) | '-' (only in a) | '+' (only in b)
            var ops = new List<(char Op, string Line)>();
            int x = 0, y = 0;
            while (x < n && y < m)
            {
                if (aLines[x] == bLines[y]) { ops.Add((' ', aLines[x])); x++; y++; }
                else if (dp[x + 1, y] >= dp[x, y + 1]) { ops.Add(('-', aLines[x])); x++; }
                else { ops.Add(('+', bLines[y])); y++; }
            }
            while (x < n) { ops.Add(('-', aLines[x])); x++; }
            while (y < m) { ops.Add(('+', bLines[y])); y++; }

            // 把 ops 收成 hunks（連續的非 ' ' 行 + 周邊 contextLines 行）
            var sb = new StringBuilder();
            sb.AppendLine("--- original (canonical)");
            sb.AppendLine("+++ roundtrip (canonical)");

            int i2 = 0;
            while (i2 < ops.Count)
            {
                if (ops[i2].Op == ' ') { i2++; continue; }
                int hunkStart = Math.Max(0, i2 - contextLines);
                int hunkEnd = i2;
                // 擴張 hunkEnd 直到連續 contextLines 個 ' ' 行（或結尾）
                int sameRun = 0;
                while (hunkEnd < ops.Count)
                {
                    if (ops[hunkEnd].Op == ' ')
                    {
                        sameRun++;
                        if (sameRun > contextLines * 2) break;
                    }
                    else { sameRun = 0; }
                    hunkEnd++;
                }
                hunkEnd = Math.Min(ops.Count, hunkEnd);

                // 計算 hunk 行號（粗略，給人類看用）
                int aLineNo = 1, bLineNo = 1;
                for (int k = 0; k < hunkStart; k++)
                {
                    if (ops[k].Op == ' ' || ops[k].Op == '-') aLineNo++;
                    if (ops[k].Op == ' ' || ops[k].Op == '+') bLineNo++;
                }
                int aCount = 0, bCount = 0;
                for (int k = hunkStart; k < hunkEnd; k++)
                {
                    if (ops[k].Op == ' ' || ops[k].Op == '-') aCount++;
                    if (ops[k].Op == ' ' || ops[k].Op == '+') bCount++;
                }
                sb.AppendLine($"@@ -{aLineNo},{aCount} +{bLineNo},{bCount} @@");
                for (int k = hunkStart; k < hunkEnd; k++)
                {
                    sb.Append(ops[k].Op).AppendLine(ops[k].Line);
                }
                i2 = hunkEnd;
            }

            return sb.ToString();
        }
    }
}
#endif
