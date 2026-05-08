// 區塊職責：把指定 UCL_Asset 子類的 .json 從專案 .BuiltinModules 遷移到 Templates~（成為跨專案範本）
// 物理意義：
//   - source 端：當前專案 `<projectRoot>/Assets/.BuiltinModules/ModulesRoot/Modules/<module>/UCL_Assets/<TypeName>/<ID>.json`
//   - target 端：UCL_Core 倉庫 `<UCL_Core>/Templates~/Assets/.BuiltinModules/ModulesRoot/Modules/<module>/UCL_Assets/<TypeName>/<ID>.json`
//   - 使用情境：開發者在某專案內編輯了 Asset（例：UCL_ChatTavernIdentityAsset 的 claude-da-xiaojie），
//             想把這份內容當預設範本回流到 UCL_Core 倉庫，後續其他專案 pull UCL_Core 後的 AutoTemplatePush
//             機制就會把它推送過去。
// 數值影響：File.Copy 寫 Templates~ 內 .json；不刪除任何檔；不自動 commit（依 ucl-commit skill 三層 bump 流程）
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>
    /// Agent Command：把指定 UCL_Asset 子類的 .json 實例從當前專案 .BuiltinModules 遷移到 UCL_Core 的 Templates~。
    ///
    /// 參數：
    /// - <c>assetType</c>（必填）：目標 UCL_Asset 子類短名（例 <c>UCL_ChatTavernIdentityAsset</c>）；大小寫敏感
    /// - <c>id</c>（必填）：要遷移的 Asset ID（例 <c>claude-da-xiaojie</c>）；填 <c>*</c> 表示遷移該類型全部
    /// - <c>module</c>（選填，預設 <c>Core</c>）：來源 module id；多 module 專案才需指定
    /// - <c>force</c>（選填，預設 <c>false</c>）：true = 直接覆寫已存在的 Template；false = 已存在則 skip
    ///
    /// 行為：
    /// - 解析 assetType 是否真的繼承 UCL_Asset&lt;T&gt;（不是的話直接 fail，避免亂遷檔）
    /// - 計算 source / target 路徑後 File.Copy
    /// - id=<c>*</c> 時 enumerate source dir 內所有 .json 並逐筆遷移
    /// </summary>
    public class Cmd_MigrateAssetToTemplate : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "MigrateAssetToTemplate";

        public override string ShortDescription =>
            "Migrate a UCL_Asset .json (or all of one type) from project .BuiltinModules to Templates~ (becomes default template).";

        public override string ArgsSchema =>
            "assetType=UCL_Asset 子類短名 (e.g., UCL_ChatTavernIdentityAsset, required) | " +
            "id=Asset ID (e.g., claude-da-xiaojie; '*' = all of this type, required) | " +
            "module=Source module id (default: Core) | " +
            "force=true|false 已存在 Template 時是否覆寫 (default: false)";

        public override string ExampleArgs =>
            "assetType=UCL_ChatTavernIdentityAsset;id=claude-da-xiaojie;module=Core";

        public override string HelpURL =>
            "ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_MigrateAssetToTemplate.md";

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            await UniTask.Yield();   // 不必真 async，但保持 signature

            string assetType = GetArg(args, "assetType", "");
            string id = GetArg(args, "id", "");
            string module = GetArg(args, "module", "Core");
            bool force = string.Equals(GetArg(args, "force", "false"), "true", StringComparison.OrdinalIgnoreCase);

            if (string.IsNullOrEmpty(assetType))
                throw new ArgumentException("[Cmd:MigrateAssetToTemplate] 缺少 assetType");
            if (string.IsNullOrEmpty(id))
                throw new ArgumentException("[Cmd:MigrateAssetToTemplate] 缺少 id（單筆 ID 或 '*' 代表全部）");

            // 區塊職責：反射驗證 assetType 真的繼承 UCL_Asset<T>
            // 物理意義：避免 agent 亂填 type 名 → 把不該動的檔案搬到 Templates~
            // 數值影響：找不到 / 非 UCL_Asset 子類 → throw → Cmd 回 Failed
            Type type = ResolveUCLAssetType(assetType);
            if (type == null)
                throw new ArgumentException($"[Cmd:MigrateAssetToTemplate] 找不到 UCL_Asset 子類 '{assetType}' 或它沒繼承 UCL_Asset<T>");

            string relAssetFolder = $"UCL_Assets/{type.Name}";   // 對齊 UCL_ModulePath.ModuleRelativePath.GetAssetRelativePath
            string moduleRel = $"ModulesRoot/Modules/{module}/{relAssetFolder}";

            string srcRoot = UCL_AssetPath.GetPath(UCL_AssetType.BuiltinModules);   // <projectRoot>/Assets/.BuiltinModules
            string dstRoot = UCL_AssetPath.GetPath(UCL_AssetType.TemplateModules);  // <UCL_Core>/Templates~/Assets/.BuiltinModules
            if (string.IsNullOrEmpty(srcRoot))
                throw new InvalidOperationException("[Cmd:MigrateAssetToTemplate] 找不到 BuiltinModules 路徑");
            if (string.IsNullOrEmpty(dstRoot))
                throw new InvalidOperationException("[Cmd:MigrateAssetToTemplate] 找不到 TemplateModules 路徑（UCL_CoreEditor.asmdef 不存在？）");

            string srcDir = Path.Combine(srcRoot, moduleRel);
            string dstDir = Path.Combine(dstRoot, moduleRel);

            if (!Directory.Exists(srcDir))
                throw new DirectoryNotFoundException($"[Cmd:MigrateAssetToTemplate] 來源目錄不存在：{srcDir}（該專案還沒建過任何 {type.Name} 實例？）");

            // 收集要搬的檔
            var fileIds = new List<string>();
            if (id == "*")
            {
                foreach (var f in Directory.EnumerateFiles(srcDir, "*.json", SearchOption.TopDirectoryOnly))
                {
                    fileIds.Add(Path.GetFileNameWithoutExtension(f));
                }
                if (fileIds.Count == 0)
                {
                    Debug.Log($"[Cmd:MigrateAssetToTemplate] {srcDir} 內無 .json 可遷移。");
                    return;
                }
            }
            else
            {
                fileIds.Add(id);
            }

            int copied = 0, skipped = 0, missing = 0;
            foreach (var fid in fileIds)
            {
                token.ThrowIfCancellationRequested();
                string srcPath = Path.Combine(srcDir, fid + ".json");
                string dstPath = Path.Combine(dstDir, fid + ".json");
                if (!File.Exists(srcPath))
                {
                    Debug.LogWarning($"[Cmd:MigrateAssetToTemplate] 來源檔不存在 — skip: {srcPath}");
                    missing++;
                    continue;
                }
                if (File.Exists(dstPath) && !force)
                {
                    Debug.Log($"[Cmd:MigrateAssetToTemplate] target 已存在 (force=false) — skip: {dstPath}");
                    skipped++;
                    continue;
                }
                try
                {
                    Directory.CreateDirectory(dstDir);
                    File.Copy(srcPath, dstPath, overwrite: true);
                    copied++;
                    Debug.Log($"[Cmd:MigrateAssetToTemplate] {(File.Exists(dstPath) && !force ? "OVERWRITE" : "WRITE")} {dstPath}");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[Cmd:MigrateAssetToTemplate] copy 失敗 {fid}: {ex.Message}");
                }
            }

            AssetDatabase.Refresh();
            Debug.Log(
                $"[Cmd:MigrateAssetToTemplate] 完成：type={type.Name} module={module}\n" +
                $"  copied={copied} / skipped={skipped} / missing={missing}\n" +
                $"  src={srcDir}\n" +
                $"  dst={dstDir}\n" +
                $"  ⚠ 未自動 commit — 請依 ucl-commit skill 三層 bump 流程處理"
            );
        }

        // 區塊職責：依短型別名找 UCL_Asset<T> 子類
        // 物理意義：跨 assembly 反射，找名字符合 + 繼承 UCL_Asset<T> 的具體 class（非泛型本身）
        // 數值影響：純讀；找不到回 null
        static Type ResolveUCLAssetType(string shortName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
                catch { continue; }

                foreach (var t in types)
                {
                    if (t == null || t.IsAbstract || t.IsGenericTypeDefinition) continue;
                    if (t.Name != shortName) continue;
                    if (!IsUCLAssetSubclass(t)) continue;
                    return t;
                }
            }
            return null;
        }

        static bool IsUCLAssetSubclass(Type t)
        {
            // 走 inheritance 找 UCL_Asset<>（generic open type）
            for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
            {
                if (cur.IsGenericType && cur.GetGenericTypeDefinition() == typeof(UCL_Asset<>))
                    return true;
            }
            return false;
        }
    }
}
#endif
