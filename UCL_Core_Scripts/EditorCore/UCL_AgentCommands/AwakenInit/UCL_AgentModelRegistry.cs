// 區塊職責：trailer 型號欄的 **Editor 端 facade** —— 轉呼叫 SCP_Core 的共用實作，本檔不做任何解析。
// 物理意義：Unity 與 senate.exe 各有一個宿主，而 trailer 只有一種正確答案 ⇒ 實作必須只有一份。
//          本檔存在的唯一理由是**補上 Editor 才知道的兩個參數**（letters 根、央行區域），
//          跟 `UCL_PersonaProfile` 那個 facade 是同一個樣板、同一個理由。
// 數值影響：純唯讀。⛔ 不讀也不寫任何設定檔。
//
// 🩸 2026-09-10（TASK-0187，Tim 拍板）本檔退掉三樣東西：
//   ① `RegistryPath` / `SaveAll` / `SaveModels` —— `AwakenInit/agent_models.json` **不再是輸入**。
//      兩張表（vendors／models）已寫死在 `SCP_AgentModelRegistry`。
//      理由：`UCL_Core` 是掛在多棵樹底下的 submodule，而那個檔是**專案級**的 ⇒ 同一位同事
//      從不同的樹提交會得到不同的 trailer，而那寫進 git history 之後改不掉。
//      實測（同一個 submodule、同一天）：`Zeta@summit(Claude / claude-opus-5)` 與
//      `zeta@summit(claude-opus-5)` 並存 —— 差別只在「提交的人站在哪棵樹」。
//   ② 自己那份 `s_Aliases` / `Normalize` / `Resolve` / `FormatTrailerModel` 實作 —— 全部轉呼叫。
//   ③ `UCL_ActualAgent` 列舉當表 key 的種子 —— 正規清單改由共用層提供（key 與 value 同一處）。
// ⛔ 這裡不准長出任何邏輯：facade 一旦「順手多做一點」就是第二份實作，
//   而兩份實作對同一個 persona 給出不同答案時，**不會有任何一層報錯**。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands
{
    public class UCL_AgentModelResolution
    {
        public string Model = "";
        public string Raw = "";
        public string Source = "";      // as-written / agent-translated / agent-unmapped / empty
        public string AgentKey = "";
        public bool WasTranslated => Source == "agent-translated";
    }

    public static class UCL_AgentModelRegistry
    {
        /// <summary>正規 actual_agent → 廠牌名。**唯讀** —— 值寫死在共用層，不是設定。</summary>
        public static Dictionary<string, string> LoadVendors()
            => new Dictionary<string, string>(
                (IDictionary<string, string>)SCP.Core.Letters.SCP_AgentModelRegistry.Vendors,
                StringComparer.OrdinalIgnoreCase);

        /// <summary>正規 actual_agent → 預設型號。**唯讀** —— 值寫死在共用層，不是設定。</summary>
        public static Dictionary<string, string> LoadModels()
            => new Dictionary<string, string>(
                (IDictionary<string, string>)SCP.Core.Letters.SCP_AgentModelRegistry.Models,
                StringComparer.OrdinalIgnoreCase);

        /// <summary>辨識用正規化 —— 無視大小寫、空白、連字號、底線。</summary>
        public static string Normalize(string value)
            => SCP.Core.Letters.SCP_AgentModelRegistry.Normalize(value);

        /// <summary>這個字串是不是 agent 名？是的話回正規 actual_agent，不是（或有歧義）回空字串。</summary>
        public static string IdentifyAgent(string value)
            => SCP.Core.Letters.SCP_AgentModelRegistry.IdentifyAgent(value);

        /// <summary>persona.model → 是 agent 名就翻成該 agent 預設型號；翻不出來保留原值。</summary>
        public static UCL_AgentModelResolution Resolve(string persona)
        {
            var aScp = SCP.Core.Letters.SCP_AgentModelRegistry.Resolve(
                UCL_LettersPath.Root, persona,
                Treasury.UCL_CentralBankSettings.CurrencyId,
                w => Debug.LogWarning(w));
            return new UCL_AgentModelResolution
            {
                Model = aScp.Model,
                Raw = aScp.Raw,
                Source = aScp.Source,
                AgentKey = aScp.AgentKey,
            };
        }

        /// <summary>trailer 的型號欄字串（`vendor / version`）。規則與射程見共用層。</summary>
        public static string FormatTrailerModel(string persona)
            => SCP.Core.Letters.SCP_AgentModelRegistry.FormatTrailerModel(
                UCL_LettersPath.Root, persona,
                Treasury.UCL_CentralBankSettings.CurrencyId,
                w => Debug.LogWarning(w));
    }
}
#endif
