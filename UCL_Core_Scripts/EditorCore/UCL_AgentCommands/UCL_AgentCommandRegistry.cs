
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 05/04 2026
// Registry of agent command handlers — auto-discovers all UCL_AgentCommandHandlerBase subclasses.
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>
    /// 註冊所有可被 agent command 觸發的 handler。
    ///
    /// 自動發現：static ctor 內透過 <see cref="AssemblyExtensions.GetAllSubclass(System.Type)"/>
    /// 掃描所有 <see cref="UCL_AgentCommandHandlerBase"/> 子類，反射建立後註冊。
    /// 專案新增指令只要寫一個 class 繼承基底即可，無需手動 Register。
    /// </summary>
    public static class UCL_AgentCommandRegistry
    {
        // CommandType (case-insensitive) → handler 實例
        static readonly Dictionary<string, UCL_AgentCommandHandlerBase> s_Handlers
            = new(StringComparer.OrdinalIgnoreCase);

        static UCL_AgentCommandRegistry()
        {
            // 區塊職責：自動發現並註冊所有 Handler 子類
            // 物理意義：以反射列出全部 assembly 內 UCL_AgentCommandHandlerBase 的非抽象子類
            // 數值影響：Editor-only，啟動 domain reload 時跑一次，後續為 O(1) 查表
            var baseType = typeof(UCL_AgentCommandHandlerBase);
            foreach (var t in baseType.GetAllSubclass())
            {
                if (t.IsAbstract) continue;
                try
                {
                    var inst = (UCL_AgentCommandHandlerBase)Activator.CreateInstance(t);
                    if (string.IsNullOrEmpty(inst.CommandType))
                    {
                        Debug.LogError($"[UCL_AgentCmd] Handler '{t.FullName}' returned empty CommandType — ignored.");
                        continue;
                    }
                    if (s_Handlers.TryGetValue(inst.CommandType, out var existing))
                    {
                        Debug.LogError($"[UCL_AgentCmd] Duplicate CommandType '{inst.CommandType}' — '{t.FullName}' overrides '{existing.GetType().FullName}'.");
                    }
                    s_Handlers[inst.CommandType] = inst;
                }
                catch (Exception e)
                {
                    Debug.LogError($"[UCL_AgentCmd] Failed to instantiate handler '{t.FullName}': {e}");
                }
            }
        }

        /// <summary>取得 handler 實例（找不到回 null）。</summary>
        public static UCL_AgentCommandHandlerBase Get(string type)
        {
            if (string.IsNullOrEmpty(type)) return null;
            return s_Handlers.TryGetValue(type, out var h) ? h : null;
        }

        /// <summary>列出所有已註冊的指令類型名稱（按字母排序）。</summary>
        public static IReadOnlyList<string> ListTypes()
        {
            return s_Handlers.Keys.OrderBy(s => s).ToList();
        }

        /// <summary>列出所有 handler 實例（按 CommandType 排序）。</summary>
        public static IReadOnlyList<UCL_AgentCommandHandlerBase> ListHandlers()
        {
            return s_Handlers.Values.OrderBy(h => h.CommandType).ToList();
        }
    }
}
#endif
