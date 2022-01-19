using System.Collections.Generic;
using UnityEngine;
/// <summary>
/// UCL_TextureAtlasCreator can merge multiple Textures into TextureAtlas
/// </summary>
namespace UCL.Core.TextureLib
{
#if UNITY_EDITOR
    [ATTR.EnableUCLEditor]
#endif
    [CreateAssetMenu(fileName = "New TextureAtlasCreator", menuName = "UCL/TextureAtlasCreator")]
    public class UCL_TextureAtlasCreator : ScriptableObject {
        public List<Texture2D> m_Texture2Ds;
        /// <summary>
        /// Texture that size not fit m_Size will be scale to fit m_Size
        /// </summary>
        [Header("Size per texture in atlas")] public int m_Size = 64;

        public TextureFormat m_TextureFormat = TextureFormat.ARGB32;

        /// <summary>
        /// If m_OutputFolder == "" then Output to the folder this UCL_TextureAtlasCreator located in
        /// </summary>
        public string m_OutputFolder = "";
        public string m_SaveName = "TextureAtlas";

        #region AutoGen
        [Header("Auto Generate Part")]
        [Core.PA.UCL_ReadOnly] public Texture2D m_Texture;
        /// <summary>
        /// Realsize of Atlas
        /// </summary>
        [Core.PA.UCL_ReadOnly] public int m_AtlasSize = 0;
        /// <summary>
        /// m_Seg * m_Size = m_AtlasSize
        /// </summary>
        [Core.PA.UCL_ReadOnly] public int m_Seg = 0;
        /// <summary>
        /// Interval of texture (Pixels)
        /// </summary>
        public int m_Interval = 0;
        #endregion

        public Texture TextureAtlas => m_Texture;

#if UNITY_EDITOR
        [ATTR.UCL_FunctionButton]
#endif
        public void CreatePNG() {
            Debug.LogWarning("CreateAndSave()");
            if(Create()) {
                SavePNG();
            }
        }
        
#if UNITY_EDITOR
        [ATTR.UCL_FunctionButton]
        public void CreateAsset() {
            Debug.LogWarning("CreateAndSave()");
            if(Create()) {
                SaveAsset();
            }
        }
        public void SaveAsset() {
            var path = GetOutputPath() + ".asset";
            Debug.LogWarning("SaveAsset():" + path);
            TextureLib.EditorLib.SaveTextureAsset(path, m_Texture);
            //UnityEditor.Selection.activeObject = gameObject;
        }
        [ATTR.UCL_FunctionButton]
        public void OpenOutputFolder() {
            m_OutputFolder = Core.FileLib.EditorLib.OpenAssetsFolderExplorer(m_OutputFolder);
            /*
            string folder_path = Application.dataPath.Replace("Assets", m_OutputFolder);
            string path = UnityEditor.EditorUtility.OpenFolderPanel("Select Output Folder", folder_path, "");
            */
            Debug.LogWarning("m_OutputFolder:" + m_OutputFolder);
        }
#endif
        protected string GetOutputPath() {
#if UNITY_EDITOR
            if(string.IsNullOrEmpty(m_OutputFolder)) {
                m_OutputFolder = Core.FileLib.Lib.GetFolderPath(UCL.Core.EditorLib.AssetDatabaseMapper.GetAssetPath(this));
            }
#endif
            return System.IO.Path.Combine(m_OutputFolder, m_SaveName);
        }
        public void SavePNG() {
            string output_path = GetOutputPath();
            TextureLib.Lib.SavePNG(output_path, m_Texture);
#if UNITY_EDITOR
            m_Texture = UCL.Core.EditorLib.AssetDatabaseMapper.LoadAssetAtPath<Texture2D>(output_path + ".png");
#endif
        }
        //[UCL.Core.PA.UCL_ReadOnly]
        //[SerializeField]
        //protected List<Vector2Int> m_ConverPosVec = new List<Vector2Int>();
        public Vector2Int ConverPos(int iAt) {
            if(m_Seg <= 0 || iAt < 0) return Vector2Int.zero;//|| at>= m_ConverPosVec.Count
            int aX = iAt % m_Seg;
            return new Vector2Int(aX, (iAt - aX) / m_Seg);
        }
        virtual public List<Texture2D> GetTextureList() {
            return m_Texture2Ds;
        }

        public Texture2D Create() {
            var aData = TextureLib.Lib.CreateTextureAtlas(GetTextureList(), m_Size, m_TextureFormat, m_Interval);
            m_Texture = aData.m_Texture;
            m_AtlasSize = m_Texture.width;
            m_Seg = aData.m_Seg;
            return m_Texture;
        }

    }
}