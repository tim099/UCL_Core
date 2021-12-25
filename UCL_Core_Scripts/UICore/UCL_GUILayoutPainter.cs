using System.Collections;
using System.Collections.Generic;
using UCL.Core.UI;
using UnityEngine;


namespace UCL.Core.UI
{

    public class UCL_GUILayoutPainter
    {
        /// <summary>
        /// Current drawing texture
        /// </summary>
        public Texture2D Texture => m_Texture;


        protected UCL_ObjectDictionary m_DataDic = new UCL_ObjectDictionary();
        protected Texture2D m_Texture = null;
        protected Color m_LineColor = Color.red;
        protected bool m_IsInited = false;
        protected int m_Width = 128;
        protected int m_Height = 128;
        System.Action<Texture2D> m_OnTextureUpdateAct = null;
        public UCL_GUILayoutPainter()
        {

        }
        public UCL_GUILayoutPainter(int iWidth, int iHeight)
        {
            m_Width = iWidth;
            m_Height = iHeight;
            //Init(iWidth, iHeight);
        }
        public void SetTexture(Texture2D iTexture)
        {
            m_Texture = iTexture;
            m_Width = m_Texture.width;
            m_Height = m_Texture.height;
        }
        public void SetTexture(byte[] iDatas)
        {
            if (!m_IsInited) Init(m_Width, m_Height);
            m_Texture.LoadRawTextureData(iDatas);
            m_Texture.Apply();
        }
        public void OnTextureUpdate(System.Action<Texture2D> iOnTextureUpdateAct)
        {
            m_OnTextureUpdateAct = iOnTextureUpdateAct;
        }
        public void Init(int iWidth = 128, int iHeight = 128)
        {
            if (m_IsInited) return;
            m_IsInited = true;
            m_Width = iWidth;
            m_Height = iHeight;
            m_Texture = new Texture2D(m_Width, m_Height, TextureFormat.RGBA32, false, false);
            Clear();
        }
        virtual public void Clear()
        {
            m_Texture.SetColor(Color.clear);
        }
        virtual public void OnGUI()
        {
            if (!m_IsInited) Init(m_Width, m_Height);
            using(var aScope = new GUILayout.VerticalScope("box"))
            {
                if (GUILayout.Button("Clear"))
                {
                    Clear();
                }
                GUILayout.BeginHorizontal();
                using (var aScope2 = new GUILayout.VerticalScope("box")) {
                    bool aIsTextureUpdated = UCL_GUILayout.DrawableTexture(m_Texture, m_DataDic.GetSubDic("DrawTexture"), m_Width, m_Height, m_LineColor);
                    if (aIsTextureUpdated)
                    {
                        m_OnTextureUpdateAct?.Invoke(m_Texture);
                    }
                }
                    
                m_LineColor = UCL_GUILayout.SelectColor(m_LineColor);
                GUILayout.EndHorizontal();
            }
        }
    }
}