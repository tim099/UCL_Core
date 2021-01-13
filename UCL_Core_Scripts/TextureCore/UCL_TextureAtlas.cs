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
            UnityEditor.Selection.activeObject = UnityEditor.AssetDatabase.LoadMainAssetAtPath(path);
            Core.EditorLib.UCL_EditorUpdateManager.AddDelayAction(delegate () {
                UnityEditor.Selection.activeGameObject = gameObject;
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

