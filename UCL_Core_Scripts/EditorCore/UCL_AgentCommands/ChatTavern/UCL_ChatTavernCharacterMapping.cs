// 區塊職責：sender_id → 顯示用 identity_id 的 mapping 表
// 物理意義：訊息進來時 sender_id 可能對不上任何 UCL_ChatTavernIdentityAsset（Asset 還沒建 / 用了別名 / 異 agent 自報名 id 不在我們命名規則內）。
//          原本直接 fallback 灰框佔位，UI 看起來很掉漆。本表提供三段查找：
//            (1) sender_id 直接 match Asset
//            (2) sender_id 在 alias 表裡 → 對應到一個已存在的 Asset id
//            (3) 都沒中 → 用 default_identity_id（預設 "gemini-da-xiaojie"）
// 設計取捨：用 List<Pair> 而不是 Dictionary 是為了能用 Unity JsonUtility 序列化（不支援 Dict）；
//          內部仍可以 lazy 建 Dictionary 加速 lookup（小規模沒必要，現在純線性掃）。
//          檔案放 <TavernDir>/character_mapping.json，跟 rooms.json / identities.json 同層。
//          mtime cache：避免每次 lookup 都 IO；外部編輯 JSON 後 ≤ 1 次 lookup 內生效。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.ChatTavern
{
    /// <summary>
    /// sender_id → identity_id 別名表 + 預設 fallback。
    /// 使用 List 而非 Dict 是為了 JsonUtility 相容（Unity 內建不支援 Dict 序列化）。
    /// </summary>
    [Serializable]
    public class UCL_ChatTavernCharacterMappingData
    {
        public string default_identity_id = "gemini-da-xiaojie";
        public List<UCL_ChatTavernCharacterAlias> aliases = new List<UCL_ChatTavernCharacterAlias>();
    }

    /// <summary>單筆別名：sender_id → identity_id。</summary>
    [Serializable]
    public class UCL_ChatTavernCharacterAlias
    {
        public string from;
        public string to;
    }

    /// <summary>
    /// 角色 mapping 持久化 + 查找。所有需要把 sender_id 解析成 identity asset id 的地方
    /// 都應該走 <see cref="ResolveIdentityId"/>，避免散落各自實作 fallback 鏈。
    /// </summary>
    public static class UCL_ChatTavernCharacterMapping
    {
        public const string MappingFile = "character_mapping.json";

        static UCL_ChatTavernCharacterMappingData s_Cache;
        static long s_LastMtimeTicks;

        // ===========================================================
        // 路徑
        // ===========================================================

        public static string GetMappingPath()
            => Path.Combine(UCL_ChatTavernIO.GetTavernDir(), MappingFile);

        // ===========================================================
        // Load / Save
        // ===========================================================

        // 區塊職責：load mapping，含 mtime cache + 不存在時 auto-create 預設
        // 物理意義：單一 editor session 內多次 lookup 共用 cache；外部編輯 JSON 後下一次 lookup 偵測 mtime 變動自動 reload
        // 數值影響：cache miss → 一次 File.GetLastWriteTimeUtc + 可能一次 File.ReadAllText
        public static UCL_ChatTavernCharacterMappingData Load()
        {
            string path = GetMappingPath();
            if (!File.Exists(path))
            {
                // auto-create with sensible defaults
                var fresh = new UCL_ChatTavernCharacterMappingData();
                Save(fresh);
                s_Cache = fresh;
                s_LastMtimeTicks = File.GetLastWriteTimeUtc(path).Ticks;
                return fresh;
            }

            long mtime = File.GetLastWriteTimeUtc(path).Ticks;
            if (s_Cache != null && mtime == s_LastMtimeTicks) return s_Cache;

            try
            {
                string json = File.ReadAllText(path);
                var data = JsonUtility.FromJson<UCL_ChatTavernCharacterMappingData>(json)
                           ?? new UCL_ChatTavernCharacterMappingData();
                if (data.aliases == null) data.aliases = new List<UCL_ChatTavernCharacterAlias>();
                s_Cache = data;
                s_LastMtimeTicks = mtime;
                return data;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[UCL_ChatTavernCharacterMapping] load failed, using fresh default: {e.Message}");
                var fresh = new UCL_ChatTavernCharacterMappingData();
                s_Cache = fresh;
                s_LastMtimeTicks = mtime;
                return fresh;
            }
        }

        public static void Save(UCL_ChatTavernCharacterMappingData data)
        {
            UCL_ChatTavernIO.EnsureTavernDir();
            string path = GetMappingPath();
            string json = JsonUtility.ToJson(data, prettyPrint: true);
            File.WriteAllText(path, json);
            s_Cache = data;
            s_LastMtimeTicks = File.GetLastWriteTimeUtc(path).Ticks;
        }

        public static void InvalidateCache()
        {
            s_Cache = null;
            s_LastMtimeTicks = 0;
        }

        // ===========================================================
        // 查找
        // ===========================================================

        // 區塊職責：sender_id → identity_id 解析（三段查找鏈）
        // 物理意義：訊息渲染端拿著 sender_id 想查 avatar / color，本函式負責把它對應到「真的有 Asset」的 identity_id
        // 數值影響：純讀取 + 線性掃 aliases；alias 規模小（< 20 條）OK，大規模再 dict 化
        public static string ResolveIdentityId(string senderId)
        {
            if (string.IsNullOrEmpty(senderId)) return null;

            var asset = new UCL_ChatTavernIdentityAsset();

            // (1) 直接 match
            if (asset.ContainsAsset(senderId)) return senderId;

            var data = Load();

            // (2) alias 表
            if (data.aliases != null)
            {
                foreach (var entry in data.aliases)
                {
                    if (entry == null) continue;
                    if (string.Equals(entry.from, senderId, StringComparison.Ordinal))
                    {
                        if (!string.IsNullOrEmpty(entry.to) && asset.ContainsAsset(entry.to))
                            return entry.to;
                        break;  // alias 命中但 target 不存在 → 不再續找，走 default
                    }
                }
            }

            // (3) default
            if (!string.IsNullOrEmpty(data.default_identity_id)
                && asset.ContainsAsset(data.default_identity_id))
            {
                return data.default_identity_id;
            }

            return null;
        }

        // ===========================================================
        // 編輯 helper（給 Page UI 用）
        // ===========================================================

        public static void AddOrUpdateAlias(string from, string to)
        {
            var data = Load();
            if (data.aliases == null) data.aliases = new List<UCL_ChatTavernCharacterAlias>();
            for (int i = 0; i < data.aliases.Count; i++)
            {
                if (string.Equals(data.aliases[i].from, from, StringComparison.Ordinal))
                {
                    data.aliases[i].to = to;
                    Save(data);
                    return;
                }
            }
            data.aliases.Add(new UCL_ChatTavernCharacterAlias { from = from, to = to });
            Save(data);
        }

        public static void RemoveAlias(string from)
        {
            var data = Load();
            if (data.aliases == null) return;
            for (int i = data.aliases.Count - 1; i >= 0; i--)
            {
                if (string.Equals(data.aliases[i].from, from, StringComparison.Ordinal))
                {
                    data.aliases.RemoveAt(i);
                }
            }
            Save(data);
        }

        public static void SetDefault(string identityId)
        {
            var data = Load();
            data.default_identity_id = identityId ?? "";
            Save(data);
        }
    }
}
#endif
