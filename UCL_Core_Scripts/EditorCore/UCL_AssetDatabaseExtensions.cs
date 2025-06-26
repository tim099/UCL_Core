
// ATS_AutoHeader
// to change the auto header please go to ATS_AutoHeader.cs
using UnityEngine;

namespace UCL.Core
{
    public class UCL_AssetDatabaseExtensions
    {
        public static void CreateOrUpdateScriptableObject<T>(string assetPath, System.Action<T> setAsset) where T : ScriptableObject
        {
#if UNITY_EDITOR
            var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<T>(assetPath);
            if (asset != null)
            {
                setAsset(asset);
                UnityEditor.EditorUtility.SetDirty(asset);
                UnityEditor.AssetDatabase.SaveAssetIfDirty(asset);
            }
            else
            {
                asset = ScriptableObject.CreateInstance<T>();
                setAsset(asset);
                UnityEditor.AssetDatabase.CreateAsset(asset, assetPath);
            }
#endif
        }
    }
}
