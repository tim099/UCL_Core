using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;



namespace UCL.Core.EditorLib {
    [Core.ATTR.EnableUCLEditor]
    [CreateAssetMenu(fileName = "New ScenesInBuild", menuName = "UCL/ScenesInBuild")]
    public class UCL_ScenesInBuild : ScriptableObject
    {

        [Header("Use Editor Setting if m_ScenesInBuild is Empty.")]
        public SceneAsset[] m_ScenesInBuild;

        /// <summary>
        /// Apply scenes in BuildSetting to Editor
        /// 把BuildSetting中的建置場景套用到Editor
        /// </summary>
        public void ApplyScenesInBuildSetting()
        {
            if (m_ScenesInBuild != null && m_ScenesInBuild.Length > 0)
            {
                EditorBuildSettingsScene[] aScenes = new EditorBuildSettingsScene[m_ScenesInBuild.Length];
                for (int i = 0; i < aScenes.Length; i++)
                {
                    var aScene = new EditorBuildSettingsScene(AssetDatabase.GetAssetPath(m_ScenesInBuild[i].GetInstanceID()), true);
                    aScenes[i] = aScene;
                }
                EditorBuildSettings.scenes = aScenes;
            }
        }
        /// <summary>
        /// Get all scenes name
        /// </summary>
        /// <returns></returns>
        public string[] GetScenesName()
        {
            string[] ScenesName = new string[m_ScenesInBuild.Length];
            List<string> ScenesNameList = new List<string>();
            for (int i = 0; i < m_ScenesInBuild.Length; i++)
            {
                var scene = m_ScenesInBuild[i];
                ScenesName[i] = scene.name;
            }

            return ScenesName;
        }
        public string GetScenePath(string iSceneName)
        {
            string aScenePath = "";
#if UNITY_EDITOR
            for (int i = 0; i < m_ScenesInBuild.Length; i++)
            {
                var scene = m_ScenesInBuild[i];
                if (scene.name == iSceneName)
                {
                    aScenePath = UCL.Core.EditorLib.AssetDatabaseMapper.GetAssetPath(scene.GetInstanceID());
                }
            }
#endif
            return aScenePath;
        }
    }
}


