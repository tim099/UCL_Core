using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.TextureLib {
    [ATTR.EnableUCLEditor]
    public class UCL_TextureAtlas : MonoBehaviour {
        public List<Texture2D> m_Texture2Ds;
        [Header("Size per texture in atlas")]public int m_Size = 64;
        public ImageFormat m_SaveFormat = ImageFormat.png;
        public TextureFormat m_TextureFormat = TextureFormat.ARGB32;
        /// <summary>
        /// Interval of texture (Pixels)
        /// </summary>
        public int m_Interval = 0;
        public string m_OutputFolder = "Assets/Textures";
        public string m_SaveName = "TextureAtlas";
        

        #region AutoGen
        [Header("Auto Generate Part")]
        [Core.PA.UCL_ReadOnly] public Texture2D m_Texture;
        [Core.PA.UCL_ReadOnly] public int m_AtlasSize = 0;//Realsize of Atlas
        [Core.PA.UCL_ReadOnly] public int m_Seg = 0;//m_Seg * m_Size = m_AtlasSize
        #endregion

        public Texture GetTexture() { return m_Texture; }

        [ATTR.UCL_FunctionButton]
        public void CreateImage() {
            Debug.LogWarning("CreateAndSave()");
            if(Create()) {
                SaveImage();
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
            UCL.Core.EditorLib.SelectionMapper.activeObject = UCL.Core.EditorLib.AssetDatabaseMapper.LoadMainAssetAtPath(path);
            UCL.Core.ServiceLib.UCL_UpdateService.AddDelayAction(delegate () {
                UCL.Core.EditorLib.SelectionMapper.activeGameObject = gameObject;
            },1);
            
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
            return System.IO.Path.Combine(m_OutputFolder, m_SaveName);
        }
        public void SaveImage() {
#if UNITY_EDITOR_WIN
            Core.FileLib.WindowsLib.OpenAssetExplorer(m_OutputFolder);
#endif
            TextureLib.Lib.SaveTexture(GetOutputPath(), m_Texture, m_SaveFormat);
        }
        virtual public List<Texture2D> GetTextureList() {
            return m_Texture2Ds;
        }
        public Texture2D Create() {
            var TextList = GetTextureList();
            var aData = TextureLib.Lib.CreateTextureAtlas(GetTextureList(), m_Size, m_TextureFormat, m_Interval);
            m_Texture = aData.m_Texture;
            m_AtlasSize = m_Texture.width;
            m_Seg = aData.m_Seg;
            return m_Texture;
        }
    }
}

