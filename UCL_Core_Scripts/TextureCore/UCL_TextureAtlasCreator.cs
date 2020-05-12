using System.Collections;
using System.Collections.Generic;
using UnityEngine;
/// <summary>
/// UCL_TextureAtlasCreator can merge multiple Textures into TextureAtlas
/// </summary>
namespace UCL.Core.TextureLib {
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
        [Core.PA.UCL_ReadOnly] public int m_AtlasSize = 0;//Realsize of Atlas
        [Core.PA.UCL_ReadOnly] public int m_Seg = 0;//m_Seg * m_Size = m_AtlasSize
        #endregion

        public Texture GetTexture() { return m_Texture; }

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
        protected string GetOutputPath() {
#if UNITY_EDITOR
            if(string.IsNullOrEmpty(m_OutputFolder)) {
                m_OutputFolder = Core.FileLib.Lib.GetFolderPath(UnityEditor.AssetDatabase.GetAssetPath(this));
            }
#endif
            return System.IO.Path.Combine(m_OutputFolder, m_SaveName);
        }
        [ATTR.UCL_FunctionButton]
        public void OpenOutputFolder() {
            m_OutputFolder = Core.FileLib.EditorLib.OpenAssetsFolderPanel(m_OutputFolder);
            /*
            string folder_path = Application.dataPath.Replace("Assets", m_OutputFolder);
            string path = UnityEditor.EditorUtility.OpenFolderPanel("Select Output Folder", folder_path, "");
            */
            Debug.LogWarning("m_OutputFolder:" + m_OutputFolder);
        }
#endif
        public void SavePNG() {
            string output_path = GetOutputPath();
            TextureLib.Lib.SavePNG(output_path, m_Texture);
#if UNITY_EDITOR
            m_Texture = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(output_path+ ".png");
#endif
        }
        public Vector2Int ConverPos(int at) {
            if(m_Seg <= 0 || at < 0) return new Vector2Int(0, 0);
            int x = at % m_Seg;
            return new Vector2Int(x, (at - x) / m_Seg);
        }
        virtual public List<Texture2D> GetTextureList() {
            return m_Texture2Ds;
        }
        public Texture2D Create() {
            var TextList = GetTextureList();
            int tex_count = TextList.Count;
            Debug.Log("Create()Texture!!");
            if(tex_count == 0) {
                Debug.LogError("m_Texture2Ds.Count==0!!");
                return null;
            }

            m_Seg = Core.MathLib.Lib.SqrtInt(tex_count);
            m_AtlasSize = m_Seg * m_Size;
            var texture = new Texture2D(m_AtlasSize, m_AtlasSize, m_TextureFormat, false);

            for(int i = 0; i < tex_count; i++) {
                var tex = TextList[i];
                var pos = ConverPos(i);
                Color[] cols = null;
                if(m_Size != tex.width || m_Size != tex.height) {
                    cols = TextureLib.Lib.GetPixels(tex, m_Size, m_Size);
                } else {
                    cols = tex.GetPixels(0);
                }
                texture.SetPixels(pos.x * m_Size, pos.y * m_Size, m_Size, m_Size, cols);
            }
            texture.Apply();

            m_Texture = texture;

            return texture;
        }
    }
}