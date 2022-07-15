using System.Collections;
using System.Collections.Generic;
using UnityEngine;


namespace UCL.Core.TextureLib
{
    [System.Serializable]
    public class UCL_TileMap : System.IDisposable
    {
        public TextureFormat m_TextureFormat = TextureFormat.ARGB32;


        public int TextureSize { get; protected set; }
        public int Width { get; protected set; }
        public int Height { get; protected set; }
        public List<Texture2D> AtlasTextures { get; protected set; }


        int[,] m_TileMap = null;
        TextureAtlasData m_Atlas = null;
        UCL_Texture2D m_TileMapTexture = null;
        Material m_Mat = null;


        public Texture2D TileMapTexture => m_TileMapTexture.GetTexture();
        bool m_Inited = false;

        public void Dispose()
        {
            if (!m_Inited) return;
            m_Inited = false;
            m_Atlas.Dispose();
            m_Atlas = null;

            m_TileMapTexture.Dispose();
            m_TileMapTexture = null;

            m_TileMap = null;
            ClearMat();
        }
        public UCL_TileMap() { }


        ~UCL_TileMap()
        {
            Dispose();
        }
        public void ClearMat()
        {
            if (m_Mat != null) GameObject.Destroy(m_Mat);
            m_Mat = null;
        }
        public Material CreateMaterial(Material iMat)
        {
            ClearMat();
            m_Mat = GameObject.Instantiate(iMat);

            m_Mat.SetInt("Width", Width);
            m_Mat.SetInt("Height", Height);

            m_Mat.SetInt("Seg", m_Atlas.m_Seg);
            m_Mat.SetFloat("SegSize", m_Atlas.SegSize);
            m_Mat.SetFloat("AtlasMult", m_Atlas.AtlasMult);
            m_Mat.SetTexture("_TextureAtlas", m_Atlas.m_Texture);
            return m_Mat;
        }
        public void SetTile(int iX, int iY, int iID)
        {
            m_TileMap[iX, iY] = iID;
            Color aCol = m_Atlas.GetAtlasColor(iID);
            m_TileMapTexture.SetPixel(iX, iY, aCol);
            m_TileMapTexture.GetTexture();
        }
        public void Init(int[,] iTileMap, List<Texture2D> iAtlasTextures, int iTextureSize)
        {
            TextureSize = iTextureSize;
            AtlasTextures = iAtlasTextures;
            m_TileMap = iTileMap;
            Width = iTileMap.GetLength(0);
            Height = iTileMap.GetLength(1);
            m_TileMapTexture = new UCL_Texture2D(Width, Height);
            m_Atlas = UCL.Core.TextureLib.Lib.CreateTextureAtlas(AtlasTextures, TextureSize, m_TextureFormat, 0);
            var aTextureAtlas = m_Atlas.m_Texture;
            aTextureAtlas.filterMode = FilterMode.Point;
            var aTileTexture = m_TileMapTexture.GetTexture();
            aTileTexture.filterMode = FilterMode.Point;
            for (int aY = 0; aY < Height; aY++)
            {
                for (int aX = 0; aX < Width; aX++)
                {
                    int aID = m_TileMap[aX, aY];
                    Color aCol = m_Atlas.GetAtlasColor(aID);
                    m_TileMapTexture.SetPixel(aX, aY, aCol);
                }
            }
        }
    }
}