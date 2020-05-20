using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.TextureLib {
    public class UCL_Texture2D : UCLI_Texture2D {
        public Vector2Int size { get { return m_Size; } }
        public int width { get { return m_Size.x; } }
        public int height { get { return m_Size.y; } }
        public TextureFormat m_TextureFormat = TextureFormat.ARGB32;
        public Texture2D texture{ get {
                return GetTexture();
            }
        }
        public Sprite sprite {
            get {
                return GetSprite();
            }
        }
        protected Texture2D m_Texture;
        protected Sprite m_Sprite;
        protected Color[] m_Col;
        protected Vector2Int m_Size;
        protected bool m_TextureUpdated = false;
        protected bool m_SpriteUpdated = false;
        public UCL_Texture2D(Vector2Int size, TextureFormat _TextureFormat = TextureFormat.ARGB32) {
            m_Size = size;
            m_TextureFormat = _TextureFormat;
            Init();
        }
        virtual public void Init() {
            m_Col = new Color[width * height];
            m_TextureUpdated = true;
            m_SpriteUpdated = true;
        }
        virtual public Texture2D GetTexture() {
            if(m_Texture == null) {
                m_Texture = Object.Instantiate(new Texture2D(width, height, m_TextureFormat, false));
                //Debug.LogWarning("Create Texture:" + width + "," + height);
            }
            if(m_TextureUpdated) {
                m_Texture.SetPixels(m_Col);
                m_Texture.Apply();
                m_TextureUpdated = false;
            }
            return m_Texture;
        }
        virtual public Sprite GetSprite() {
            if(m_SpriteUpdated) {
                m_Sprite = Core.TextureLib.Lib.CreateSprite(GetTexture());
                //Sprite.Create(GetTexture(), new Rect(0,0,m_Texture.width, m_Texture.height), Vector2.zero);
                m_SpriteUpdated = false;
            }
            return m_Sprite;

        }
        virtual public void SetPixel(Vector2Int pos, Color col) {
            m_TextureUpdated = true;
            m_SpriteUpdated = true;
            if(pos.x < 0) pos.x = 0;
            if(pos.y < 0) pos.y = 0;
            if(pos.x >= width) pos.x = width - 1;
            if(pos.y >= height) pos.y = height - 1;
            m_Col[pos.x + pos.y * width] = col;
        }
        virtual public void SetPixel(int x,int y, Color col) {
            m_TextureUpdated = true;
            m_SpriteUpdated = true;
            if(x < 0) x = 0;
            if(y < 0) y = 0;
            if(x >= width) x = width - 1;
            if(y >= height) y = height - 1;
            m_Col[x + y * width] = col;
            //Debug.LogWarning(("Pos:") + (x + y * Width) + "Col:" + col);
        }
        virtual public Color[] GetPixels() { return m_Col; }
        virtual public Color GetPixelBilinear(float x, float y) {
            float xx = x * width;
            float yy = y * height;

            int sx = Mathf.FloorToInt(xx);
            int sy = Mathf.FloorToInt(yy);
            if(sx < 0) sx = 0;
            if(sy < 0) sy = 0;

            float ox = xx - sx;
            float oy = yy - sy;

            return Color.Lerp(Color.Lerp(GetPixel(sx, sy), GetPixel(sx + 1, sy), ox),
                                Color.Lerp(GetPixel(sx, sy + 1), GetPixel(sx + 1, sy + 1), ox), oy);
        }
        virtual public Color GetPixel(int x, int y) {
            if(x < 0) x = 0;
            if(x >= width) x = width - 1;
            if(y < 0) y = 0;
            if(y >= height) y = height - 1;

            return m_Col[x + y * width];
        }
        virtual public Color GetPixel(Vector2Int pos) {
            if(pos.x < 0) pos.x = 0;
            if(pos.y < 0) pos.y = 0;
            if(pos.x >= width) pos.x = width - 1;
            if(pos.y >= height) pos.y = height - 1;
            return m_Col[pos.x + pos.y * width];
        }
    }
}

