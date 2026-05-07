// 區塊職責：通用反射調用 Cmd — 把字串描述（type / member / args）餵給 UCL_ReflectionInvoker，
//          動態觸發 Unity 內建 API（如 CompilationPipeline.RequestScriptCompilation /
//          AssetDatabase.ImportAsset / EditorPrefs.SetString…），不必為每個 API 都寫專用 Cmd。
// 物理意義：解析 + 反射 Invoke 抽到 UCL_ReflectionInvoker（位於 UtilCore，runtime-available，
//          觸發來源不限 Cmd）；本檔只是 args dispatch 的薄層。
// 數值影響：副作用視被呼叫的 API 而定（呼叫 RequestScriptCompilation 會觸發 domain reload 等）；
//          解析失敗 / 找不到 type / 參數型別不符 → 印 LogError + throw（讓 Cmd queue 標 Failed）。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UCL.Core;        // UCL_ReflectionInvoker / UCL_ReflectionInvokeRequest / UCL_ReflectionInvokeResult
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>
    /// 透過反射觸發任意 public static method / property / field。
    /// 解析與執行都在 <see cref="UCL_ReflectionInvoker"/>；本 Cmd 只負責 args 轉接 + 結果輸出。
    /// </summary>
    /// <remarks>
    /// 範例（觸發 Unity 重編，等同 Cmd_Recompile 的核心）：
    /// <code>
    /// type=UnityEditor.Compilation.CompilationPipeline
    /// member=RequestScriptCompilation
    /// </code>
    /// 範例（讀屬性 — 是否正在編譯）：
    /// <code>
    /// type=UnityEditor.EditorApplication
    /// member=isCompiling
    /// kind=property
    /// </code>
    /// 範例（呼帶參 method — refresh asset database with options）：
    /// <code>
    /// type=UnityEditor.AssetDatabase
    /// member=Refresh
    /// paramTypes=UnityEditor.ImportAssetOptions
    /// args=Default
    /// </code>
    /// </remarks>
    public class Cmd_Invoke : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "Invoke";

        public override string ShortDescription =>
            "Reflection-based invoker for Unity public static methods / properties / fields (e.g. CompilationPipeline.RequestScriptCompilation).";

        public override string ArgsSchema =>
            "type=Fully qualified type name (e.g. UnityEditor.Compilation.CompilationPipeline). REQUIRED.\n" +
            "member=Method / property / field name. REQUIRED.\n" +
            "kind=method (default) / property / field\n" +
            "paramTypes=Semicolon-separated full type names for overload disambiguation (optional)\n" +
            "args=Semicolon-separated string args matching paramTypes; primitive / enum / string / 'null' supported (optional)\n" +
            "getter=true (default) / false — for property/field, set to false then args[0] is the value to assign\n" +
            "nonPublic=true / false (default) — also search internal / private static members (Unity 內建 API 大量是 internal)";

        /// <summary>Page「Fill Example」按鈕一鍵填入用 — 等價 Cmd_Recompile 的核心呼叫，最小可驗證範例。</summary>
        public override string ExampleArgs =>
            "type=UnityEditor.Compilation.CompilationPipeline;member=RequestScriptCompilation";

        public override string HelpURL =>
            "ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_Invoke.md";

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            // 1) 解析字串描述 → request
            UCL_ReflectionInvokeRequest req;
            try
            {
                req = UCL_ReflectionInvoker.ParseRequest(args ?? new Dictionary<string, string>());
            }
            catch (Exception e)
            {
                Debug.LogError($"[AgentCmd:Invoke] parse failed: {e.Message}");
                throw;
            }

            // 2) 執行
            Debug.Log($"[AgentCmd:Invoke] {req.Kind} {req.TypeName}.{req.MemberName}" +
                      (req.ParamTypes.Count > 0 ? $"({string.Join(",", req.ParamTypes)})" : "") +
                      (req.Args.Count > 0 ? $" args=[{string.Join(",", req.Args)}]" : "") +
                      (req.Kind != "method" ? $" getter={req.IsGetter}" : ""));

            var result = UCL_ReflectionInvoker.Invoke(req);

            // 3) 結果輸出
            if (!result.Success)
            {
                Debug.LogError($"[AgentCmd:Invoke] FAILED: {result.Error}");
                throw new Exception(result.Error); // 標記 Cmd 為 Failed，外部 Python wrapper 才知道
            }

            if (result.ValueType == typeof(void) || result.Value == null)
            {
                Debug.Log("[AgentCmd:Invoke] OK (void / null)");
            }
            else
            {
                Debug.Log($"[AgentCmd:Invoke] OK ({result.ValueType?.FullName}) = {result.ValueAsString}");
            }

            await UniTask.CompletedTask;
        }
    }
}
#endif
