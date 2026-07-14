using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UCL.Core;

namespace UCL.Core
{
    /// <summary>
    /// Scaffolding base for an AssetEntry whose selectable ID list is "scoped" to the
    /// asset currently being edited on GUI (<see cref="UCLI_Asset.s_CurOnGUIAsset"/>).
    ///
    /// 編輯某個「容器 Asset」時, 下拉選單只顯示該容器已登錄的 ID; 容器沒開 / 清單為空時
    /// fallback 回全體 ID (保留原 InteractionHSceneEntry 的語意).
    ///
    /// 只負責 s_CurOnGUIAsset 接線 + 空清單 fallback; "如何從當前 asset 取出 scoped IDs"
    /// 交給子類的 <see cref="GetScopedIDs"/>. 有共用 interface 時可直接繼承本類 override
    /// (type-safe fast path); 沒有 interface 時走 <see cref="UCL_AssetEntryScopedReflect{T}"/>.
    /// </summary>
    [System.Serializable]
    public abstract class UCL_AssetEntryScoped<T> : UCL_AssetEntryDefault<T>
        where T : class, UCLI_Asset, UCLI_Preview, new()
    {
        /// <summary>
        /// 從當前正在編輯的 asset 取出被 scope 的 ID 清單.
        /// 回傳 null / 空清單 → 表示不 scope (走 base.GetAllIDs 全體 fallback).
        /// </summary>
        /// <param name="iCurAsset">UCLI_Asset.s_CurOnGUIAsset, 可能為 null</param>
        protected abstract List<string> GetScopedIDs(UCLI_CommonEditable iCurAsset);

        override public List<string> GetAllIDs(bool iUseCache = false)
        {
            List<string> result = GetScopedIDs(UCLI_Asset.s_CurOnGUIAsset);
            if (result.IsNullOrEmpty())
            {
                result = base.GetAllIDs(iUseCache);
            }
            return result;
        }
    }

    /// <summary>
    /// Reflection strategy for <see cref="UCL_AssetEntryScoped{T}"/> — 用於「沒有共用 interface」
    /// 的情況: 子類只需指定 scope 型別 (<see cref="ScopeType"/>) 與清單成員名 (<see cref="ScopeMemberName"/>),
    /// 由 reflection 抓出成員值 (欄位或屬性皆可), 逐元素取 ID.
    ///
    /// ScopeType 可傳具體型別 (typeof(HSceneAsset)) 或 interface (typeof(IInteractions)) — 兩者
    /// IsInstanceOfType 都成立, 後者可一次 cover 同介面的一整族 asset.
    ///
    /// 成本: MemberInfo 靜態快取, 每個 (type, name) 只 reflect 一次; 且上游 SelectIDOnGUI 已對
    /// GetAllIDs 做 1s 快取 → Editor-only, 每秒最多反射取值一次, 可忽略.
    /// </summary>
    [System.Serializable]
    public abstract class UCL_AssetEntryScopedReflect<T> : UCL_AssetEntryScoped<T>
        where T : class, UCLI_Asset, UCLI_Preview, new()
    {
        /// <summary>scope 判定型別, 具體型別或 interface 皆可 (e.g. typeof(HSceneAsset) / typeof(IInteractions))</summary>
        protected abstract System.Type ScopeType { get; }

        /// <summary>scope 清單所在的成員名 (欄位或屬性), e.g. "Interactions"</summary>
        protected abstract string ScopeMemberName { get; }

        /// <summary>
        /// 清單元素身上「ID 所在」的成員名. 預設 "ID" (元素自帶 ID / 本身是 UCLI_ID);
        /// 元素的 ID 藏在巢狀成員時覆寫, e.g. SkeletonGraphicSetting → "skeleton" (值為 UCLI_ID, 取其 ID).
        /// 成員值支援 string 或 UCLI_ID.
        /// </summary>
        protected virtual string ElementIDMemberName => "ID";

        // (ownerType, memberName) → MemberInfo(欄位或屬性); null 表示找不到 (也快取避免重複找)
        static readonly Dictionary<(System.Type, string), MemberInfo> s_MemberCache = new();

        protected override List<string> GetScopedIDs(UCLI_CommonEditable iCurAsset)
        {
            if (iCurAsset == null) return null;
            System.Type scopeType = ScopeType;
            if (scopeType == null || !scopeType.IsInstanceOfType(iCurAsset)) return null;

            MemberInfo member = ResolveMember(iCurAsset.GetType(), ScopeMemberName);
            if (member == null) return null;

            object value = GetMemberValue(member, iCurAsset);
            if (value is not IEnumerable seq) return null;

            string idMemberName = ElementIDMemberName;
            List<string> ids = new List<string>();
            foreach (object element in seq)
            {
                if (element == null) continue;
                string elementID = ExtractElementID(element, idMemberName);
                if (elementID != null) ids.Add(elementID);
            }
            return ids;
        }

        static MemberInfo ResolveMember(System.Type iOwnerType, string iMemberName)
        {
            var key = (iOwnerType, iMemberName);
            if (s_MemberCache.TryGetValue(key, out MemberInfo cached)) return cached;

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            MemberInfo member = (MemberInfo)iOwnerType.GetProperty(iMemberName, flags)
                                ?? iOwnerType.GetField(iMemberName, flags);
            s_MemberCache[key] = member;
            return member;
        }

        static object GetMemberValue(MemberInfo iMember, object iTarget)
        {
            return iMember switch
            {
                PropertyInfo p => p.GetValue(iTarget),
                FieldInfo f => f.GetValue(iTarget),
                _ => null,
            };
        }

        static string ExtractElementID(object iElement, string iIDMemberName)
        {
            if (iIDMemberName == "ID" && iElement is UCLI_ID id) return id.ID; // 預設情境 fast path

            MemberInfo idMember = ResolveMember(iElement.GetType(), iIDMemberName);
            if (idMember == null) return null;
            return GetMemberValue(idMember, iElement) switch
            {
                string str => str,
                UCLI_ID nested => nested.ID,
                _ => null,
            };
        }
    }
}
