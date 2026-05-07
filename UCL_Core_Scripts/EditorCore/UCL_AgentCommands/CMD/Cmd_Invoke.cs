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
    /// 範例（instance method 鏈式呼叫 — 拿 RCG_StoryData → 拿子故事）：
    /// <code>
    /// // step 1: 拿 Util（繼承自 UCL_Util&lt;T&gt; 的 static property）
    /// type=RCG.RCG_StoryData;member=Util;kind=property;storeAs=util
    /// // step 2: $util.GetData("AbandonedTemple") — instance method
    /// target=$util;member=GetData;args=AbandonedTemple;storeAs=story
    /// // step 3: $story.GetSubStory("Start") — instance method
    /// target=$story;member=GetSubStory;args=Start;storeAs=sub
    /// </code>
    /// </remarks>
    public class Cmd_Invoke : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "Invoke";

        public override string ShortDescription =>
            "Reflection-based invoker for Unity public static methods / properties / fields (e.g. CompilationPipeline.RequestScriptCompilation).";

        public override string ArgsSchema =>
            "type=Fully qualified Type.FullName, exact case (e.g. UnityEditor.Compilation.CompilationPipeline). REQUIRED unless target is set.\n" +
            "member=Method / property / field name. REQUIRED. Case-sensitive.\n" +
            "kind=method (default) / property / field\n" +
            "paramTypes=Semicolon-separated full type names for overload disambiguation (optional)\n" +
            "args=Semicolon-separated string args matching paramTypes; primitive / enum / string / 'null' supported. Use $varname to reference a value previously stored via storeAs.\n" +
            "getter=true (default) / false — for property/field, set to false then args[0] is the value to assign\n" +
            "nonPublic=true / false (default) — also search internal / private members (Unity 內建 API 大量是 internal)\n" +
            "target=$varname — make this an instance call; pulls instance from Variables[varname] (must have been storeAs'd by an earlier invoke). When set, type can be omitted (uses target.GetType()).\n" +
            "storeAs=varname — on success, store the return value into Variables[varname] for later $varname references. Cleared on Unity domain reload.";

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
