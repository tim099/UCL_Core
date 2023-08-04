using System.Collections.Generic;
using UnityEngine;
using UnityEngine.TextCore.Text;

namespace UCL.Core.TextureLib
{
    public class UCL_Texture2D : UCLI_Texture2D, System.IDisposable {
        public enum Shape
        {
            Circle = 0,
            Square,
            Diamond,
            Star,
            Cross,
        }
        public Vector2Int size { get { return m_Size; } }
        public int width { get { return m_Size.x; } }
        public int height { get { return m_Size.y; } }
        public TextureFormat m_TextureFormat = TextureFormat.ARGB32;
        public Texture2D Texture => m_Texture;
        public Sprite sprite {
            get {
                if (m_Sprite == null) m_Sprite = Core.TextureLib.Lib.CreateSprite(m_Texture);
                return m_Sprite;
            }
        }
        protected Texture2D m_Texture;
        protected Sprite m_Sprite;
        protected Color[] m_Col;
        protected Vector2Int m_Size;
        protected bool m_TextureUpdated = false;
        protected bool m_SpriteUpdated = false;

        public static UCL_Texture2D CreateTexture(Vector2Int size, TextureFormat _TextureFormat = TextureFormat.ARGB32, System.Type type = null) {
            UCL_Texture2D tex = null;
            /*
            switch(type) {
                case "UCL_EaseTexture":tex = System.Reflection.Assembly.GetExecutingAssembly()
                .CreateInstance("UCL.Core.Tween.Ease.UCL_EaseTexture") as UCL_Texture2D; 
                        //new Core.Tween.Ease.UCL_EaseTexture(size, _TextureFormat);
                    break;
            }
            */
            if(type != null) {
                tex = System.Activator.CreateInstance(type) as UCL_Texture2D;
            }
            if(tex == null) {
                tex = new UCL_Texture2D();
            }

            tex.Init(size, _TextureFormat);
            return tex;
        }
        public void Dispose()
        {
            ClearTexture();
            m_Col = null;
        }
        /// <summary>
        /// Constructor without Init
        /// </summary>
        public UCL_Texture2D() {

        }
        public UCL_Texture2D(byte[] iBytes, bool iUpdateMipmap = false)
        {
            Init(UCL.Core.TextureLib.Lib.CreateTexture(iBytes, false, iUpdateMipmap));
        }
        public UCL_Texture2D(Texture2D iTexture)
        {
            Init(iTexture);
        }
        public UCL_Texture2D(int width, int height, TextureFormat _TextureFormat = TextureFormat.ARGB32) {
            Init(new Vector2Int(width, height), _TextureFormat);
        }
        public UCL_Texture2D(Vector2Int size, TextureFormat _TextureFormat = TextureFormat.ARGB32) {
            Init(size, _TextureFormat);
        }
        ~UCL_Texture2D() {
            Dispose();
        }
        public void ClearTexture() {
            //Debug.LogWarning("ClearTexture()!!");
            if (m_Texture != null) {
#if UNITY_EDITOR
                Object.DestroyImmediate(m_Texture);
#else
                Object.Destroy(m_Texture);
#endif
                m_Texture = null;
            }
            if(m_Sprite != null)
            {
#if UNITY_EDITOR
                Object.DestroyImmediate(m_Sprite);
#else
                Object.Destroy(m_Sprite);
#endif
                m_Sprite = null;
            }
        }
        virtual public void Init(Texture2D iTexture)
        {
            m_Texture = iTexture;
            Init(new Vector2Int(iTexture.width, iTexture.height), iTexture.format);
        }
        virtual public void Init(Vector2Int size, TextureFormat _TextureFormat = TextureFormat.ARGB32) {
            m_Size = size;
            m_TextureFormat = _TextureFormat;
            Init();
        }
        virtual public void Init() {
            m_Col = new Color[width * height];
            m_TextureUpdated = true;
            m_SpriteUpdated = true;
        }
        virtual public void ClearColor() => SetColor(Color.clear);
        virtual public void SetColor(Color iColor) {
            m_TextureUpdated = true;
            m_SpriteUpdated = true;
            System.Array.Fill(m_Col, iColor);
            //for (int i = 0,len = m_Col.Length; i < len ; i++) {
            //    m_Col[i] = iColor;
            //}
        }
        /// <summary>
        /// Draw a dot on pos
        /// </summary>
        /// <param name="iX"></param>dot position x
        /// <param name="iY"></param>dot position y
        /// <param name="iCol"></param>dot color
        /// <param name="iRadius"></param>dot radius
        virtual public void DrawDot(float iX, float iY, Color iCol, int iRadius = 0) {
            int x = Mathf.RoundToInt(iX * m_Size.x);
            int y = Mathf.RoundToInt(iY * m_Size.y);
            if(iRadius <= 0) {
                SetPixel(x, y, iCol);
            }
            int sx = x - iRadius;
            int ex = x + iRadius;
            int sy = y - iRadius;
            int ey = y + iRadius;
            if(sx < 0) sx = 0;
            if(sy < 0) sy = 0;
            if(ex >= m_Size.x) sx = m_Size.x - 1;
            if(ey >= m_Size.y) sy = m_Size.y - 1;
            for(int i = sy; i <= ey; i++) {
                for(int j = sx; j <= ex; j++) {
                    int dx = j - x;
                    int dy = i - y;
                    if(Mathf.Sqrt(dx * dx + dy * dy) <= iRadius) {
                        DrawPixel(j, i, iCol);
                    }
                }
            }
        }
        virtual public void DrawDot(Vector2 pos, Color col, int radius = 0) {
            DrawDot(pos.x, pos.y, col, radius);
        }
        virtual public void DrawHorizontalLine(float iY, Color iLineCol)
        {
            int aY = Mathf.RoundToInt(iY * m_Size.y);
            if (aY < 0) aY = 0;
            if (aY >= m_Size.y) aY = m_Size.y - 1;
            for (int aX = 0; aX < m_Size.x; aX++)
            {
                SetPixel(aX, aY, iLineCol);
            }
        }
        virtual public void DrawVerticalLine(float iX, Color iLineCol)
        {
            int aX = Mathf.RoundToInt(iX * m_Size.x);
            if (aX < 0) aX = 0;
            if (aX >= m_Size.x) aX = m_Size.x - 1;
            for (int aY = 0; aY < m_Size.y; aY++)
            {
                SetPixel(aX, aY, iLineCol);
            }
        }

        /// <summary>
        /// line_func take x as parameter and should return the value of y
        /// y range between 0 ~ 1
        /// </summary>
        /// <param name="iLineFunc"></param>
        /// <param name="iLineCol"></param>
        virtual public void DrawLine(System.Func<float,float> iLineFunc, Color iLineCol) {
            if(iLineFunc == null) return;

            int prev = 0;
            for(int i = 0; i < m_Size.x; i++) {
                float at = (i / (float)(m_Size.x - 1));
                float val = iLineFunc(at);

                int cur = Mathf.RoundToInt(val * m_Size.y);
                if(i == 0) prev = cur;
                int min = Mathf.Min(prev, cur);
                int max = Mathf.Max(prev, cur);

                for(int j = 0; j < m_Size.y; j++) {
                    if(j >= min && j <= max) {
                        SetPixel(i, j, iLineCol);
                    } else {
                        //SetPixel(i, j, background_col);
                        /*
                        if(j == m_ZeroPos) {
                            SetPixel(i, j, Color.white);
                        } else {
                            SetPixel(i, j, background_col);
                        }
                        */
                    }
                }
                prev = cur;
            }
        }
        virtual public void DrawAudioWav(System.Func<float, float> iValFunc, Color iLineCol)
        {
            if (iValFunc == null) return;
            int aMiddle = m_Size.y / 2;
            for (int i = 0; i < m_Size.x; i++)
            {
                float aVal = iValFunc(i / (float)(m_Size.x - 1));

                int aCur = Mathf.RoundToInt(aVal * m_Size.y);

                int aMin = Mathf.Min(aMiddle, aCur);
                int aMax = Mathf.Max(aMiddle, aCur);

                for (int j = 0; j < m_Size.y; j++)
                {
                    if (j >= aMin && j <= aMax)
                    {
                        SetPixel(i, j, iLineCol);
                    }
                }
            }
        }
        /// <summary>
        /// line_func take x as parameter and should return the value of y
        /// auto scale y value to fit in texture
        /// </summary>
        /// <param name="line_func"></param>
        /// <param name="line_col"></param>
        /// <param name="mark_zero"></param> true will draw a line on y = 0
        /// <param name="mark_zero"></param> true will draw a line on y = 1
        virtual public Vector2 DrawLineAutoFit(System.Func<float, float> line_func, Color line_col,bool mark_zero = false,bool mark_one = false) {
            if(line_func == null) return Vector2.zero;
            MathLib.RangeChecker<float> rangeChecker = new MathLib.RangeChecker<float>();
            rangeChecker.Init(0, 0);
            List<float> values = new List<float>();
            for(int i = 0; i < m_Size.x; i++) {
                float at = (i / (float)(m_Size.x - 1));
                float val = line_func(at);
                rangeChecker.AddValue(val);
                values.Add(val);
            }
            float Min = rangeChecker.Min;
            float Max = rangeChecker.Max;
            float Range = Max - Min;
            
            if(Min < 0 && mark_zero) {
                float z_pos = -Min / Range;
                DrawLine(delegate (float y) {
                    return z_pos;
                }, Color.white);
            }

            if(Max > 1.0f && mark_one) {
                float z_pos = (1.0f - Min) / Range;
                DrawLine(delegate (float y) {
                    return z_pos;
                }, Color.blue);
            }

            int prev = 0;
            for(int i = 0; i < m_Size.x; i++) {
                //float at = (i / (float)(m_Size.x - 1));
                float val = values[i];
                val = ((val - Min) / Range);
                int cur = Mathf.RoundToInt(val * m_Size.y);
                if(i == 0) prev = cur;
                int min = Mathf.Min(prev, cur);
                int max = Mathf.Max(prev, cur);

                for(int j = 0; j < m_Size.y; j++) {
                    if(j >= min && j <= max) {
                        SetPixel(i, j, line_col);
                    }
                }
                prev = cur;
            }
            return new Vector2(Min, Max);
        }
        virtual public void DrawLineAutoFit(System.Func<float, float> line_func, Color line_col, float Min, float Max,
            bool mark_zero = false, bool mark_one = false) {
            if(line_func == null) return;

            float Range = Max - Min;

            if(Min < 0 && mark_zero) {
                float z_pos = -Min / Range;
                DrawLine(delegate (float y) {
                    return z_pos;
                }, Color.white);
            }

            if(Max > 1.0f && mark_one) {
                float z_pos = (1.0f - Min) / Range;
                DrawLine(delegate (float y) {
                    return z_pos;
                }, Color.blue);
            }

            int prev = 0;
            for(int i = 0; i < m_Size.x; i++) {
                float at = (i / (float)(m_Size.x - 1));
                float val = line_func(at);
                val = ((val - Min) / Range);
                int cur = Mathf.RoundToInt(val * m_Size.y);
                if(i == 0) prev = cur;
                int min = Mathf.Min(prev, cur);
                int max = Mathf.Max(prev, cur);

                for(int j = 0; j < m_Size.y; j++) {
                    if(j >= min && j <= max) {
                        SetPixel(i, j, line_col);
                    }
                }
                prev = cur;
            }
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
        virtual public void SetPixel(Vector2Int iPos, Color iCol) {
            m_TextureUpdated = true;
            m_SpriteUpdated = true;
            if(iPos.x < 0) iPos.x = 0;
            if(iPos.y < 0) iPos.y = 0;
            if(iPos.x >= width) iPos.x = width - 1;
            if(iPos.y >= height) iPos.y = height - 1;
            m_Col[iPos.x + iPos.y * width] = iCol;
        }

        public class DrawFontSetting
        {
            public int m_Size = 13;
            public Texture2D m_FontTexture = null;
            public Color? m_OutlineColor = null;
            public int m_MaxWidthIndex = 16;
            public int m_MaxHeightIndex = 16;
            public Font m_Font;
        }
        //private const string FontAssetName = "Arial.ttf";//LegacyRuntime.ttf
        private static DrawFontSetting s_DefaultDrawFontSetting = null;
        public static DrawFontSetting DefaultDrawFontSetting
        {
            get
            {
                if(s_DefaultDrawFontSetting == null)
                {
                    
                    s_DefaultDrawFontSetting = new DrawFontSetting();
                    //s_DefaultDrawFontSetting.m_FontTexture = FontTexture;
                    //s_DefaultDrawFontSetting.m_Font = Resources.GetBuiltinResource<Font>(FontAssetName);
                    s_DefaultDrawFontSetting.m_Font = Resources.Load<Font>("Tripfive");
                    //s_DefaultDrawFontSetting.m_Font = Resources.Load<Font>("BLACKBOLD");
                    //Font.textureRebuilt += (iFont) =>
                    //{
                    //    if (iFont == (s_DefaultDrawFontSetting.m_Font))
                    //    {
                    //        Texture2D aTexture = s_DefaultDrawFontSetting.m_Font.material.mainTexture as Texture2D;
                    //        s_DefaultDrawFontSetting.m_FontTexture = aTexture;
                    //    }
                    //};
                    Texture2D aTexture = s_DefaultDrawFontSetting.m_Font.material.mainTexture as Texture2D;
                    s_DefaultDrawFontSetting.m_FontTexture = aTexture;
                    //if (aTexture != null)
                    //{
                    //    Texture2D texture2D = new Texture2D(aTexture.width, aTexture.height, TextureFormat.RGBA32, false);

                    //    RenderTexture currentRT = RenderTexture.active;
                    //    //RenderTexture renderTexture = RenderTexture.GetTemporary(aTexture.width, aTexture.height, 32);
                    //    RenderTexture renderTexture = new RenderTexture(aTexture.width, aTexture.height, 32);
                    //    Graphics.Blit(aTexture, renderTexture, s_DefaultDrawFontSetting.m_Font.material);

                    //    RenderTexture.active = renderTexture;
                    //    texture2D.ReadPixels(new Rect(0, 0, renderTexture.width, renderTexture.height), 0, 0);
                    //    texture2D.Apply();
                    //    //Color[] pixels = texture2D.GetPixels();
                    //    RenderTexture.active = currentRT;
                    //    //RenderTexture.ReleaseTemporary(renderTexture);
                    //    renderTexture.Release();
                    //    s_DefaultDrawFontSetting.m_FontTexture = texture2D;
                    //}
                    //else
                    //{
                    //    Debug.LogError("s_DefaultDrawFontSetting aTexture != null");
                    //}

                }
                return s_DefaultDrawFontSetting;
            }
        }
        virtual public void DrawString(string iStr, Vector2Int iPos, Color iCol, DrawFontSetting iDrawFontSetting = null)
        {
            if (string.IsNullOrEmpty(iStr))
            {
                return;
            }
            if(iDrawFontSetting == null)
            {
                iDrawFontSetting = DefaultDrawFontSetting;
            }
            
            if (iDrawFontSetting.m_Font == null)
            {
                iDrawFontSetting.m_Font = DefaultDrawFontSetting.m_Font;
            }
            iDrawFontSetting.m_Font.RequestCharactersInTexture(iStr, 0);
            if(iDrawFontSetting.m_FontTexture == null)
            {
                iDrawFontSetting.m_FontTexture = DefaultDrawFontSetting.m_FontTexture;
            }
            //int aInterVal = iDrawFontSetting.m_Size;//+ 2
            int aCurX = 0;
            for (int i = 0; i < iStr.Length; i++)
            {
                int aWidth = DrawChar(iStr[i], iPos + new Vector2Int(aCurX, 0), iCol, iDrawFontSetting);
                aCurX += aWidth;
            }
        }
        virtual public int DrawChar(char iChar, Vector2Int iPos, Color iCol, DrawFontSetting iDrawFontSetting = null)
        {
            if (iDrawFontSetting == null)
            {
                iDrawFontSetting = DefaultDrawFontSetting;
            }
            Font aFont = iDrawFontSetting.m_Font;// Asset
            if (aFont == null)
            {
                Debug.LogError("DrawChar aFont == null");
                return 0;
            }
            m_TextureUpdated = true;


            CharacterInfo aCharacterInfo;
            var aHasChar = aFont.GetCharacterInfo(iChar, out aCharacterInfo);
            if (!aHasChar)
            {
                Debug.LogError($"DrawChar ,!aHasChar iChar:{iChar}");
                return 0;
            }
            var aTexture = iDrawFontSetting.m_FontTexture;//aFont.material.GetTexture("_MainTex") as Texture2D;
            //Debug.LogWarning($"fontAsset:{aFont.name},Char:{iChar},aHasChar:{aHasChar},BottomLeft:{aCharacterInfo.uvBottomLeft}" +
            //    $",uvTopRight:{aCharacterInfo.uvTopRight},(aTexture != null):{(aTexture != null)}");
            if (aTexture == null)
            {
                Debug.LogError($"DrawChar ,aFont.material.GetTexture(\"_MainTex\") == null");
                return 0;
            }
            float aTracking = 0.1f;
            int aWidth = Mathf.RoundToInt(aTracking * iDrawFontSetting.m_Size * aCharacterInfo.glyphWidth);
            int aHeight = Mathf.RoundToInt(aTracking * iDrawFontSetting.m_Size * aCharacterInfo.glyphHeight);
            //Debug.LogError($"Char:{iChar},glyphWidth:{aCharacterInfo.glyphWidth},glyphHeight:{aCharacterInfo.glyphHeight}");
            int aAdvance = Mathf.RoundToInt(aWidth + aTracking * aCharacterInfo.advance);
            HashSet<Vector2Int> aSet = null;
            if (iDrawFontSetting.m_OutlineColor.HasValue)
            {
                aSet = new();
            }
            for (int aY = 0; aY <= aHeight; aY++)
            {
                for (int aX = 0; aX <= aWidth ; aX++)
                {
                    int aPosX = aX + iPos.x;
                    int aPosY = aY + iPos.y;
                    if (aPosX < width && aPosY < height && aPosX >= 0 && aPosY >= 0)
                    {
                        float aFX = Mathf.Lerp(aCharacterInfo.uvBottomLeft.x, aCharacterInfo.uvTopRight.x, (float)aX / aWidth);
                        float aFY = Mathf.Lerp(aCharacterInfo.uvBottomLeft.y, aCharacterInfo.uvTopRight.y, (float)aY / aHeight);
                        var aCol = aTexture.GetPixelBilinear(aFX, aFY, 0);
                        //if (aCol.a > 0.5f)
                        if (aCol.a > 0.2f)
                        {
                            m_Col[aPosX + aPosY * width] = iCol;
                            if (aCol.a > 0.8f)
                            {
                                m_Col[aPosX + aPosY * width] = aCol;
                            }
                            else
                            {
                                m_Col[aPosX + aPosY * width] = Color.Lerp(m_Col[aPosX + aPosY * width], aCol, aCol.a);
                            }
                            
                            if (aSet != null) aSet.Add(new Vector2Int(aPosX, aPosY));
                        }
                    }
                }
            }
            if (aSet != null)//Draw Outline
            {
                Color aOutlineColor = iDrawFontSetting.m_OutlineColor.Value;
                HashSet<Vector2Int> aOutlineSet = new();
                foreach (var aPos in aSet)
                {
                    for (int aX = -1; aX <= 1; aX++)
                    {
                        for (int aY = -1; aY <= 1; aY++)
                        {
                            if (aX == 0 && aY == 0) continue;
                            Vector2Int aNewPos = new Vector2Int(aX + aPos.x, aY + aPos.y);
                            if (aSet.Contains(aNewPos) || aOutlineSet.Contains(aNewPos)) continue;
                            aOutlineSet.Add(aNewPos);
                            if (aNewPos.x < width && aNewPos.y < height && aNewPos.x >= 0 && aNewPos.y >= 0)
                            {
                                m_Col[aNewPos.x + aNewPos.y * width] = aOutlineColor;
                            }
                        }
                    }
                }
            }

            return aAdvance;
        }
        virtual public void DrawShape(Shape iShape, Vector2Int iPos, Color iCol, float iRadius = 1f)
        {
            switch(iShape)
            {
                case Shape.Circle:
                    {
                        DrawCircle(iPos, iCol, iRadius);
                        break;
                    }
                case Shape.Star:
                    {
                        DrawStar(iPos, iCol, iRadius);
                        break;
                    }
                case Shape.Square:
                    {
                        DrawSquare(iPos, iCol, iRadius);
                        break;
                    }
                case Shape.Diamond:
                    {
                        DrawDiamond(iPos, iCol, iRadius);
                        break;
                    }
                case Shape.Cross:
                    {
                        DrawCross(iPos, iCol, iRadius);
                        break;
                    }
            }
        }
        virtual public void DrawCross(Vector2Int iPos, Color iCol, float iRadius = 1f)
        {
            m_TextureUpdated = true;
            m_SpriteUpdated = true;
            int aRad = Mathf.CeilToInt(iRadius);
            int aMinX = Mathf.Clamp(iPos.x - aRad, 0, width);
            int aMaxX = Mathf.Clamp(iPos.x + aRad + 1, 0, width);
            int aMinY = Mathf.Clamp(iPos.y - aRad, 0, height);
            int aMaxY = Mathf.Clamp(iPos.y + aRad + 1, 0, height);
            int aSegLen = Mathf.FloorToInt(iRadius / 1.75f);
            for (int aX = aMinX; aX < aMaxX; aX++)
            {
                for (int aY = aMinY; aY < aMaxY; aY++)
                {
                    Vector2 aDel = new Vector2(aX, aY) - iPos;
                    if (Mathf.Abs(aDel.x) < aSegLen || Mathf.Abs(aDel.y) < aSegLen)
                    {
                        m_Col[aX + aY * width] = iCol;
                    }
                }
            }
        }
        virtual public void DrawDiamond(Vector2Int iPos, Color iCol, float iRadius = 1f)
        {
            m_TextureUpdated = true;
            m_SpriteUpdated = true;
            int aRad = Mathf.CeilToInt(iRadius);
            int aMinX = Mathf.Clamp(iPos.x - aRad, 0, width);
            int aMaxX = Mathf.Clamp(iPos.x + aRad + 1, 0, width);
            int aMinY = Mathf.Clamp(iPos.y - aRad, 0, height);
            int aMaxY = Mathf.Clamp(iPos.y + aRad + 1, 0, height);
            for (int aX = aMinX; aX < aMaxX; aX++)
            {
                for (int aY = aMinY; aY < aMaxY; aY++)
                {
                    Vector2 aDel = new Vector2(aX, aY) - iPos;
                    if (Mathf.Abs(aDel.x) + Mathf.Abs(aDel.y) <= aRad)
                    {
                        m_Col[aX + aY * width] = iCol;
                    }
                }
            }
        }
        virtual public void DrawSquare(Vector2Int iPos, Color iCol, float iRadius = 1f)
        {
            m_TextureUpdated = true;
            m_SpriteUpdated = true;
            int aRad = Mathf.CeilToInt(iRadius);
            int aMinX = Mathf.Clamp(iPos.x - aRad - 1, 0, width);
            int aMaxX = Mathf.Clamp(iPos.x + aRad + 2, 0, width);
            int aMinY = Mathf.Clamp(iPos.y - aRad - 1, 0, height);
            int aMaxY = Mathf.Clamp(iPos.y + aRad + 2, 0, height);
            for (int aX = aMinX; aX < aMaxX; aX++)
            {
                for (int aY = aMinY; aY < aMaxY; aY++)
                {
                    m_Col[aX + aY * width] = iCol;
                }
            }
        }
        virtual public void DrawCircle(Vector2Int iPos, Color iCol, float iRadius = 1f)
        {
            m_TextureUpdated = true;
            m_SpriteUpdated = true;
            int aRad = Mathf.CeilToInt(iRadius);
            int aMinX = Mathf.Clamp(iPos.x - aRad - 1, 0, width);
            int aMaxX = Mathf.Clamp(iPos.x + aRad + 2, 0, width);
            int aMinY = Mathf.Clamp(iPos.y - aRad - 1, 0, height);
            int aMaxY = Mathf.Clamp(iPos.y + aRad + 2, 0, height);
            for(int aX = aMinX; aX < aMaxX; aX++)
            {
                for (int aY = aMinY; aY < aMaxY; aY++)
                {
                    float aDis = (new Vector2(aX, aY) - iPos).magnitude - 0.4f;
                    if (aDis < iRadius)
                    {
                        m_Col[aX + aY * width] = iCol;
                    }
                }
            }
        }
        virtual public void DrawStar(Vector2Int iPos, Color iCol, float iRadius = 1f)
        {
            m_TextureUpdated = true;
            m_SpriteUpdated = true;
            int aRad = Mathf.CeilToInt(iRadius);
            int aMinX = Mathf.Clamp(iPos.x - aRad - 1, 0, width);
            int aMaxX = Mathf.Clamp(iPos.x + aRad + 2, 0, width);
            int aMinY = Mathf.Clamp(iPos.y - aRad - 1, 0, height);
            int aMaxY = Mathf.Clamp(iPos.y + aRad + 2, 0, height);
            for (int aX = aMinX; aX < aMaxX; aX++)
            {
                for (int aY = aMinY; aY < aMaxY; aY++)
                {
                    Vector2 aDel = new Vector2(aX, aY) - iPos;
                    float aDis = aDel.magnitude - 0.5f;
                    float aTan = Mathf.Atan2(aDel.x, aDel.y) + Mathf.PI;
                    float aRadius = iRadius * (Mathf.Lerp(0.5f, 1, (2.5f * aTan / Mathf.PI) % 1));
                    if (aDis < aRadius)
                    {
                        m_Col[aX + aY * width] = iCol;
                    }
                }
            }
        }
        /// <summary>
        /// Draw a line start from sx,sy to ex,ey
        /// </summary>
        /// <param name="sx">start position of x</param>
        /// <param name="sy">start position of y</param>
        /// <param name="ex">end position of x</param>
        /// <param name="ey">end position of y</param>
        /// <param name="line_col">color of line</param>
        virtual public void DrawLine(int sx, int sy, int ex, int ey, Color col) {
            if(ey < 0 && sy < 0) return;
            if(ey >= height && sy >= height) return;
            if(ex < 0 && sx < 0) return;
            if(ex >= width && sx >= width) return;

            m_TextureUpdated = true;
            m_SpriteUpdated = true;

            Vector2Int os = new Vector2Int(sx, sy);
            Vector2Int oe = new Vector2Int(ex, ey);
            //Debug.LogWarning("sx:" + sx + ",sy:" + sy + ",ex:" + ex + ",ey:" + ey+ ",width:"+ width+ ",height:" + height);
            if(ex < 0) {
                if(sx < 0) return;

                int dx = ex - sx;
                int dy = ey - sy;

                if(dy == 0) {
                    ex = 0;
                } else {
                    float tx = -sx / (float)dx;
                    float ty = 0;
                    //Debug.LogWarning("sx:" + sx + ",dx:" + dx+",ex:"+ex+",sx:"+sx);
                    if(dy > 0) {//up
                        ty = (height - sy - 1) / (float)dy;
                    } else if(dy < 0) {//down
                        ty = -sy / (float)dy;
                    }

                    if(tx < ty) {//reach x border first
                        //Debug.LogWarning("tx < ty:" + tx + "<" + ty);
                        ex = 0;
                        ey = sy + Mathf.RoundToInt(dy * tx);
                    } else if(tx > ty) {//tx > ty reach y border first
                        //Debug.LogWarning("tx > ty:" + tx + ">" + ty);
                        ex = sx + Mathf.RoundToInt(dx * ty);
                        if(dy > 0) {
                            ey = height - 1;
                        } else {
                            ey = 0;
                        }
                    } else {//tx == ty reach corner
                        //Debug.LogWarning("tx == ty:" + tx + "==" + ty);
                        ex = 0;
                        if(dy > 0) {
                            ey = height - 1;
                        } else {
                            ey = 0;
                        }
                    }
                }
            } 
            else if(ex >= width) {
                if(sx >= width) {
                    return;
                }
                int dx = ex - sx;
                int dy = ey - sy;

                if(dy == 0) {
                    ex = width - 1;
                } else {
                    float tx = (width - sx - 1) / (float)dx;
                    float ty = 0;
                    if(dy > 0) {//up
                        ty = (height - sy - 1) / (float)dy;
                    } else if(dy < 0) {//down
                        ty = -sy / (float)dy;
                    }

                    if(tx < ty) {//reach x border first
                        ex = width - 1;
                        ey = sy + Mathf.RoundToInt(dy * tx);
                    } else if(tx > ty) {//tx > ty reach y border first
                        ex = sx + Mathf.RoundToInt(dx * ty);
                        if(dy > 0) {
                            ey = height - 1;
                        } else {
                            ey = 0;
                        }
                    } else {//tx == ty reach corner
                        ex = width - 1;
                        if(dy > 0) {
                            ey = height - 1;
                        } else {
                            ey = 0;
                        }
                    }
                }
            }

            if(ey < 0) {
                if(sy < 0) return;
                int dx = ex - sx;
                int dy = ey - sy;

                if(dx == 0) {
                    ey = 0;
                } else {
                    float tx = 0;
                    float ty = -sy / (float)dy;
                    //Debug.LogWarning("sx:" + sx + ",dx:" + dx+",ex:"+ex+",sx:"+sx);
                    if(dx > 0) {//right
                        tx = (width - sx - 1) / (float)dx;
                    } else if(dx < 0) {//left
                        tx = -sx / (float)dx;
                    }

                    if(ty < tx) {//reach x border first
                        //Debug.LogWarning("tx < ty:" + tx + "<" + ty);
                        ey = 0;
                        ex = sx + Mathf.RoundToInt(dx * ty);
                    } else if(ty > tx) {//tx > ty reach y border first
                        //Debug.LogWarning("tx > ty:" + tx + ">" + ty);
                        ey = sy + Mathf.RoundToInt(dy * tx);
                        if(dx > 0) {
                            ex = width - 1;
                        } else {
                            ex = 0;
                        }
                    } else {//tx == ty reach corner
                        //Debug.LogWarning("tx == ty:" + tx + "==" + ty);
                        ey = 0;
                        if(dx > 0) {
                            ex = width - 1;
                        } else {
                            ex = 0;
                        }
                    }
                }
            } 
            else if(ey >= height) {
                if(sy >= height) return;
                int dx = ex - sx;
                int dy = ey - sy;

                if(dx == 0) {
                    ey = height - 1;
                } else {
                    float ty = (height - sy - 1) / (float)dy;
                    float tx = 0;
                    //Debug.LogWarning("sx:" + sx + ",dx:" + dx + ",ex:" + ex + ",sx:" + sx);
                    if(dx > 0) {//right
                        tx = (width - sx - 1) / (float)dx;
                    } else if(dx < 0) {//left
                        tx = -sx / (float)dx;
                    }

                    if(ty < tx) {//reach y border first
                        //Debug.LogWarning("tx < ty:" + tx + "<" + ty);
                        ey = height - 1;
                        ex = sx + Mathf.RoundToInt(dx * ty);
                    } else if(ty > tx) {//reach x border first
                        //Debug.LogWarning("tx > ty:" + tx + ">" + ty);
                        ey = sy + Mathf.RoundToInt(dy * tx);
                        if(dx > 0) {
                            ex = width - 1;
                        } else {
                            ex = 0;
                        }
                    } else {//tx == ty reach corner
                        //Debug.LogWarning("tx == ty:" + tx + "==" + ty);
                        ey = height - 1;
                        if(dx > 0) {
                            ex = width - 1;
                        } else {
                            ex = 0;
                        }
                    }
                }
            }


            if(sx < 0) {
                if(ex < 0) return;

                int dx = sx - ex;
                int dy = sy - ey;

                if(dy == 0) {
                    sx = 0;
                } else {
                    float tx = -ex / (float)dx;
                    float ty = 0;
                    //Debug.LogWarning("sx:" + sx + ",dx:" + dx+",ex:"+ex+",sx:"+sx);
                    if(dy > 0) {//up
                        ty = (height - ey - 1) / (float)dy;
                    } else if(dy < 0) {//down
                        ty = -ey / (float)dy;
                    }

                    if(tx < ty) {//reach x border first
                        //Debug.LogWarning("tx < ty:" + tx + "<" + ty);
                        sx = 0;
                        sy = ey + Mathf.RoundToInt(dy * tx);
                    } else if(tx > ty) {//tx > ty reach y border first
                        //Debug.LogWarning("tx > ty:" + tx + ">" + ty);
                        sx = ex + Mathf.RoundToInt(dx * ty);
                        if(dy > 0) {
                            sy = height - 1;
                        } else {
                            sy = 0;
                        }
                    } else {//tx == ty reach corner
                        //Debug.LogWarning("tx == ty:" + tx + "==" + ty);
                        sx = 0;
                        if(dy > 0) {
                            sy = height - 1;
                        } else {
                            sy = 0;
                        }
                    }
                }
            }
            else if(sx >= width) {
                if(ex >= width) {
                    return;
                }
                int dx = sx - ex;
                int dy = sy - ey;

                if(dy == 0) {
                    sx = width - 1;
                } else {
                    float tx = (width - ex - 1) / (float)dx;
                    float ty = 0;
                    if(dy > 0) {//up
                        ty = (height - ey - 1) / (float)dy;
                    } else if(dy < 0) {//down
                        ty = -ey / (float)dy;
                    }

                    if(tx < ty) {//reach x border first
                        sx = width - 1;
                        sy = ey + Mathf.RoundToInt(dy * tx);
                    } else if(tx > ty) {//tx > ty reach y border first
                        sx = ex + Mathf.RoundToInt(dx * ty);
                        if(dy > 0) {
                            sy = height - 1;
                        } else {
                            sy = 0;
                        }
                    } else {//tx == ty reach corner
                        sx = width - 1;
                        if(dy > 0) {
                            sy = height - 1;
                        } else {
                            sy = 0;
                        }
                    }
                }
            }

            if(sy < 0) {
                if(ey < 0) return;
                int dx = sx - ex;
                int dy = sy - ey;

                if(dx == 0) {
                    sy = 0;
                } else {
                    float tx = 0;
                    float ty = -ey / (float)dy;
                    //Debug.LogWarning("sx:" + sx + ",dx:" + dx+",ex:"+ex+",sx:"+sx);
                    if(dx > 0) {//right
                        tx = (width - ex - 1) / (float)dx;
                    } else if(dx < 0) {//left
                        tx = -ex / (float)dx;
                    }

                    if(ty < tx) {//reach x border first
                        //Debug.LogWarning("tx < ty:" + tx + "<" + ty);
                        sy = 0;
                        sx = ex + Mathf.RoundToInt(dx * ty);
                    } else if(ty > tx) {//tx > ty reach y border first
                        //Debug.LogWarning("tx > ty:" + tx + ">" + ty);
                        sy = ey + Mathf.RoundToInt(dy * tx);
                        if(dx > 0) {
                            sx = width - 1;
                        } else {
                            sx = 0;
                        }
                    } else {//tx == ty reach corner
                        //Debug.LogWarning("tx == ty:" + tx + "==" + ty);
                        sy = 0;
                        if(dx > 0) {
                            sx = width - 1;
                        } else {
                            sx = 0;
                        }
                    }
                }
            } 
            else if(sy >= height) {
                if(ey >= height) return;
                int dx = sx - ex;
                int dy = sy - ey;

                if(dx == 0) {
                    sy = height - 1;
                } else {
                    float ty = (height - ey - 1) / (float)dy;
                    float tx = 0;
                    //Debug.LogWarning("sx:" + sx + ",dx:" + dx + ",ex:" + ex + ",sx:" + sx);
                    if(dx > 0) {//right
                        tx = (width - ex - 1) / (float)dx;
                    } else if(dx < 0) {//left
                        tx = -ex / (float)dx;
                    }

                    if(ty < tx) {//reach y border first
                        //Debug.LogWarning("ty < tx:" + ty + "<" + tx);
                        sy = height - 1;
                        sx = ex + Mathf.RoundToInt(dx * ty);
                    } else if(ty > tx) {//reach x border first
                        //Debug.LogWarning("ty > tx:" + ty + ">" + tx);
                        sy = ey + Mathf.RoundToInt(dy * tx);
                        if(dx > 0) {
                            sx = width - 1;
                        } else {
                            sx = 0;
                        }
                    } else {//tx == ty reach corner
                        //Debug.LogWarning("tx == ty:" + tx + "==" + ty);
                        sy = height - 1;
                        if(dx > 0) {
                            sx = width - 1;
                        } else {
                            sx = 0;
                        }
                    }
                }
            }


            try {
                int dx = ex - sx;
                int dy = ey - sy;
                if(dx == 0) {
                    if(dy > 0) {
                        for(int i = 0; i <= dy; i++) {
                            m_Col[sx + (sy + i) * width] = col;
                        }
                    } else {
                        for(int i = 0; i >= dy; i--) {
                            m_Col[sx + (sy + i) * width] = col;
                        }
                    }

                } else if(dy == 0) {
                    int sval = sx + sy * width;
                    if(dx > 0) {
                        for(int i = 0; i <= dx; i++) {
                            m_Col[sval + i] = col;
                        }
                    } else {
                        for(int i = 0; i >= dx; i--) {
                            m_Col[sval + i] = col;
                        }
                    }
                } else {
                    int lx = MathLib.Lib.Abs(dx);
                    int ly = MathLib.Lib.Abs(dy);
                    if(lx >= ly) {
                        int prev_y = sy;
                        for(int i = 0; i <= lx; i++) {
                            int x = sx + (dx > 0 ? i : -i);
                            int y = sy + Mathf.RoundToInt((i * dy) / (float)lx);
                            if(y != prev_y) {
                                m_Col[x + prev_y * width] = col;
                                prev_y = y;
                            }
                            m_Col[x + y * width] = col;
                        }
                    } else {
                        int prev_x = sx;
                        for(int i = 0; i <= ly; i++) {
                            int y = sy + (dy > 0 ? i : -i);
                            int x = sx + Mathf.RoundToInt((i * dx) / (float)ly);
                            if(x != prev_x) {
                                m_Col[prev_x + y * width] = col;
                                prev_x = x;
                            }
                            m_Col[x + y * width] = col;
                        }
                    }
                }
            } catch(System.Exception e) {
                Debug.LogError("Exception:" + e + "\n\n" +
                    ("sx:" + sx + ",sy:" + sy + ",ex:" + ex + ",ey:" + ey + "width:" + width + ",height:" + height)
                    + "\n\n" + "s:" + os.ToString() + ",e:" + oe.ToString());
            }

        }
        virtual public void DrawLine(int iStartX, int iStartY, int iEndX, int iEndY, System.Func<float, int, Color> iDrawFunc)
        {
            if (iEndY < 0 && iStartY < 0) return;
            if (iEndY >= height && iStartY >= height) return;
            if (iEndX < 0 && iStartX < 0) return;
            if (iEndX >= width && iStartX >= width) return;

            m_TextureUpdated = true;
            m_SpriteUpdated = true;

            Vector2Int os = new Vector2Int(iStartX, iStartY);
            Vector2Int oe = new Vector2Int(iEndX, iEndY);
            //Debug.LogWarning("sx:" + sx + ",sy:" + sy + ",ex:" + ex + ",ey:" + ey+ ",width:"+ width+ ",height:" + height);
            if (iEndX < 0)
            {
                if (iStartX < 0) return;

                int dx = iEndX - iStartX;
                int dy = iEndY - iStartY;

                if (dy == 0)
                {
                    iEndX = 0;
                }
                else
                {
                    float tx = -iStartX / (float)dx;
                    float ty = 0;
                    //Debug.LogWarning("sx:" + sx + ",dx:" + dx+",ex:"+ex+",sx:"+sx);
                    if (dy > 0)
                    {//up
                        ty = (height - iStartY - 1) / (float)dy;
                    }
                    else if (dy < 0)
                    {//down
                        ty = -iStartY / (float)dy;
                    }

                    if (tx < ty)
                    {//reach x border first
                        iEndX = 0;
                        iEndY = iStartY + Mathf.RoundToInt(dy * tx);
                    }
                    else if (tx > ty)
                    {//tx > ty reach y border first
                        iEndX = iStartX + Mathf.RoundToInt(dx * ty);
                        if (dy > 0)
                        {
                            iEndY = height - 1;
                        }
                        else
                        {
                            iEndY = 0;
                        }
                    }
                    else
                    {//tx == ty reach corner
                        iEndX = 0;
                        if (dy > 0)
                        {
                            iEndY = height - 1;
                        }
                        else
                        {
                            iEndY = 0;
                        }
                    }
                }
            }
            else if (iEndX >= width)
            {
                if (iStartX >= width)
                {
                    return;
                }
                int dx = iEndX - iStartX;
                int dy = iEndY - iStartY;

                if (dy == 0)
                {
                    iEndX = width - 1;
                }
                else
                {
                    float tx = (width - iStartX - 1) / (float)dx;
                    float ty = 0;
                    if (dy > 0)
                    {//up
                        ty = (height - iStartY - 1) / (float)dy;
                    }
                    else if (dy < 0)
                    {//down
                        ty = -iStartY / (float)dy;
                    }

                    if (tx < ty)
                    {//reach x border first
                        iEndX = width - 1;
                        iEndY = iStartY + Mathf.RoundToInt(dy * tx);
                    }
                    else if (tx > ty)
                    {//tx > ty reach y border first
                        iEndX = iStartX + Mathf.RoundToInt(dx * ty);
                        if (dy > 0)
                        {
                            iEndY = height - 1;
                        }
                        else
                        {
                            iEndY = 0;
                        }
                    }
                    else
                    {//tx == ty reach corner
                        iEndX = width - 1;
                        if (dy > 0)
                        {
                            iEndY = height - 1;
                        }
                        else
                        {
                            iEndY = 0;
                        }
                    }
                }
            }

            if (iEndY < 0)
            {
                if (iStartY < 0) return;
                int dx = iEndX - iStartX;
                int dy = iEndY - iStartY;

                if (dx == 0)
                {
                    iEndY = 0;
                }
                else
                {
                    float tx = 0;
                    float ty = -iStartY / (float)dy;
                    if (dx > 0)
                    {//right
                        tx = (width - iStartX - 1) / (float)dx;
                    }
                    else if (dx < 0)
                    {//left
                        tx = -iStartX / (float)dx;
                    }

                    if (ty < tx)
                    {//reach x border first
                        iEndY = 0;
                        iEndX = iStartX + Mathf.RoundToInt(dx * ty);
                    }
                    else if (ty > tx)
                    {//tx > ty reach y border first
                        iEndY = iStartY + Mathf.RoundToInt(dy * tx);
                        if (dx > 0)
                        {
                            iEndX = width - 1;
                        }
                        else
                        {
                            iEndX = 0;
                        }
                    }
                    else
                    {//tx == ty reach corner
                        iEndY = 0;
                        if (dx > 0)
                        {
                            iEndX = width - 1;
                        }
                        else
                        {
                            iEndX = 0;
                        }
                    }
                }
            }
            else if (iEndY >= height)
            {
                if (iStartY >= height) return;
                int dx = iEndX - iStartX;
                int dy = iEndY - iStartY;

                if (dx == 0)
                {
                    iEndY = height - 1;
                }
                else
                {
                    float ty = (height - iStartY - 1) / (float)dy;
                    float tx = 0;
                    if (dx > 0)
                    {//right
                        tx = (width - iStartX - 1) / (float)dx;
                    }
                    else if (dx < 0)
                    {//left
                        tx = -iStartX / (float)dx;
                    }

                    if (ty < tx)
                    {//reach y border first
                        iEndY = height - 1;
                        iEndX = iStartX + Mathf.RoundToInt(dx * ty);
                    }
                    else if (ty > tx)
                    {//reach x border first
                        iEndY = iStartY + Mathf.RoundToInt(dy * tx);
                        if (dx > 0)
                        {
                            iEndX = width - 1;
                        }
                        else
                        {
                            iEndX = 0;
                        }
                    }
                    else
                    {//tx == ty reach corner
                        iEndY = height - 1;
                        if (dx > 0)
                        {
                            iEndX = width - 1;
                        }
                        else
                        {
                            iEndX = 0;
                        }
                    }
                }
            }


            if (iStartX < 0)
            {
                if (iEndX < 0) return;

                int dx = iStartX - iEndX;
                int dy = iStartY - iEndY;

                if (dy == 0)
                {
                    iStartX = 0;
                }
                else
                {
                    float tx = -iEndX / (float)dx;
                    float ty = 0;
                    if (dy > 0)
                    {//up
                        ty = (height - iEndY - 1) / (float)dy;
                    }
                    else if (dy < 0)
                    {//down
                        ty = -iEndY / (float)dy;
                    }

                    if (tx < ty)
                    {//reach x border first
                        iStartX = 0;
                        iStartY = iEndY + Mathf.RoundToInt(dy * tx);
                    }
                    else if (tx > ty)
                    {//tx > ty reach y border first
                        iStartX = iEndX + Mathf.RoundToInt(dx * ty);
                        if (dy > 0)
                        {
                            iStartY = height - 1;
                        }
                        else
                        {
                            iStartY = 0;
                        }
                    }
                    else
                    {//tx == ty reach corner
                        iStartX = 0;
                        if (dy > 0)
                        {
                            iStartY = height - 1;
                        }
                        else
                        {
                            iStartY = 0;
                        }
                    }
                }
            }
            else if (iStartX >= width)
            {
                if (iEndX >= width)
                {
                    return;
                }
                int dx = iStartX - iEndX;
                int dy = iStartY - iEndY;

                if (dy == 0)
                {
                    iStartX = width - 1;
                }
                else
                {
                    float tx = (width - iEndX - 1) / (float)dx;
                    float ty = 0;
                    if (dy > 0)
                    {//up
                        ty = (height - iEndY - 1) / (float)dy;
                    }
                    else if (dy < 0)
                    {//down
                        ty = -iEndY / (float)dy;
                    }

                    if (tx < ty)
                    {//reach x border first
                        iStartX = width - 1;
                        iStartY = iEndY + Mathf.RoundToInt(dy * tx);
                    }
                    else if (tx > ty)
                    {//tx > ty reach y border first
                        iStartX = iEndX + Mathf.RoundToInt(dx * ty);
                        if (dy > 0)
                        {
                            iStartY = height - 1;
                        }
                        else
                        {
                            iStartY = 0;
                        }
                    }
                    else
                    {//tx == ty reach corner
                        iStartX = width - 1;
                        if (dy > 0)
                        {
                            iStartY = height - 1;
                        }
                        else
                        {
                            iStartY = 0;
                        }
                    }
                }
            }

            if (iStartY < 0)
            {
                if (iEndY < 0) return;
                int dx = iStartX - iEndX;
                int dy = iStartY - iEndY;

                if (dx == 0)
                {
                    iStartY = 0;
                }
                else
                {
                    float tx = 0;
                    float ty = -iEndY / (float)dy;
                    if (dx > 0)
                    {//right
                        tx = (width - iEndX - 1) / (float)dx;
                    }
                    else if (dx < 0)
                    {//left
                        tx = -iEndX / (float)dx;
                    }

                    if (ty < tx)
                    {//reach x border first
                        iStartY = 0;
                        iStartX = iEndX + Mathf.RoundToInt(dx * ty);
                    }
                    else if (ty > tx)
                    {//tx > ty reach y border first
                        iStartY = iEndY + Mathf.RoundToInt(dy * tx);
                        if (dx > 0)
                        {
                            iStartX = width - 1;
                        }
                        else
                        {
                            iStartX = 0;
                        }
                    }
                    else
                    {//tx == ty reach corner
                        iStartY = 0;
                        if (dx > 0)
                        {
                            iStartX = width - 1;
                        }
                        else
                        {
                            iStartX = 0;
                        }
                    }
                }
            }
            else if (iStartY >= height)
            {
                if (iEndY >= height) return;
                int dx = iStartX - iEndX;
                int dy = iStartY - iEndY;

                if (dx == 0)
                {
                    iStartY = height - 1;
                }
                else
                {
                    float ty = (height - iEndY - 1) / (float)dy;
                    float tx = 0;
                    if (dx > 0)
                    {//right
                        tx = (width - iEndX - 1) / (float)dx;
                    }
                    else if (dx < 0)
                    {//left
                        tx = -iEndX / (float)dx;
                    }

                    if (ty < tx)
                    {//reach y border first
                        iStartY = height - 1;
                        iStartX = iEndX + Mathf.RoundToInt(dx * ty);
                    }
                    else if (ty > tx)
                    {//reach x border first
                        iStartY = iEndY + Mathf.RoundToInt(dy * tx);
                        if (dx > 0)
                        {
                            iStartX = width - 1;
                        }
                        else
                        {
                            iStartX = 0;
                        }
                    }
                    else
                    {//tx == ty reach corner
                        iStartY = height - 1;
                        if (dx > 0)
                        {
                            iStartX = width - 1;
                        }
                        else
                        {
                            iStartX = 0;
                        }
                    }
                }
            }

            try
            {
                int dx = iEndX - iStartX;
                int dy = iEndY - iStartY;
                if (dx == 0)
                {
                    if (dy > 0)
                    {
                        for (int i = 0; i <= dy; i++)
                        {
                            Color aCol = iDrawFunc((float)i / dy, dy);
                            if (aCol != Color.clear)
                            {
                                m_Col[iStartX + (iStartY + i) * width] = aCol;
                            }
                            
                        }
                    }
                    else
                    {
                        for (int i = 0; i >= dy; i--)
                        {
                            Color aCol = iDrawFunc((float)i / dy, -dy);
                            if (aCol != Color.clear)
                            {
                                m_Col[iStartX + (iStartY + i) * width] = aCol;
                            }
                        }
                    }

                }
                else if (dy == 0)
                {
                    int sval = iStartX + iStartY * width;
                    if (dx > 0)
                    {
                        for (int i = 0; i <= dx; i++)
                        {
                            Color aCol = iDrawFunc((float)i / dx, dx);
                            if (aCol != Color.clear)
                            {
                                m_Col[sval + i] = aCol;
                            }
                        }
                    }
                    else
                    {
                        for (int i = 0; i >= dx; i--)
                        {
                            Color aCol = iDrawFunc((float)i / dx, -dx);
                            if (aCol != Color.clear)
                            {
                                m_Col[sval + i] = aCol;
                            }
                        }
                    }
                }
                else
                {
                    int lx = MathLib.Lib.Abs(dx);
                    int ly = MathLib.Lib.Abs(dy);
                    if (lx >= ly)
                    {
                        int prev_y = iStartY;
                        for (int i = 0; i <= lx; i++)
                        {
                            int x = iStartX + (dx > 0 ? i : -i);
                            int y = iStartY + Mathf.RoundToInt((i * dy) / (float)lx);
                            Color aCol = iDrawFunc((float)i / lx, lx);

                            if (y != prev_y)
                            {
                                if (aCol != Color.clear) m_Col[x + prev_y * width] = aCol;
                                prev_y = y;
                            }
                            if (aCol != Color.clear) m_Col[x + y * width] = aCol;
                        }
                    }
                    else
                    {
                        int prev_x = iStartX;
                        for (int i = 0; i <= ly; i++)
                        {
                            int y = iStartY + (dy > 0 ? i : -i);
                            int x = iStartX + Mathf.RoundToInt((i * dx) / (float)ly);
                            Color aCol = iDrawFunc((float)i / ly , ly);
                            if (x != prev_x)
                            {
                                if (aCol != Color.clear) m_Col[prev_x + y * width] = aCol;
                                prev_x = x;
                            }
                            if (aCol != Color.clear) m_Col[x + y * width] = aCol;
                        }
                    }
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError("Exception:" + e + "\n\n" +
                    ("sx:" + iStartX + ",sy:" + iStartY + ",ex:" + iEndX + ",ey:" + iEndY + "width:" + width + ",height:" + height)
                    + "\n\n" + "s:" + os.ToString() + ",e:" + oe.ToString());
            }

        }
        virtual public void DrawLine(int iStartX, int iStartY, int iEndX, int iEndY, Color iStartCol, Color iEndCol)
        {
            DrawLine(iStartX, iStartY, iEndX, iEndY, (iDis, iLen) => {
                return Color.Lerp(iStartCol, iEndCol, iDis);
            });
        }
        /// <summary>
        /// Draw a line start from sx,sy to ex,ey
        /// </summary>
        /// <param name="sx">start position of x</param>
        /// <param name="sy">start position of y</param>
        /// <param name="ex">end position of x</param>
        /// <param name="ey">end position of y</param>
        /// <param name="line_col">color of line</param>
        virtual public void DrawLine(float sx, float sy, float ex, float ey, Vector2 start_pos, Vector2 size, Color col) {
            DrawLine(Mathf.RoundToInt(((sx - start_pos.x) / size.x) * m_Size.x),
                Mathf.RoundToInt(((sy - start_pos.y) / size.y) * m_Size.y),
                Mathf.RoundToInt(((ex - start_pos.x) / size.x) * m_Size.x),
                Mathf.RoundToInt(((ey - start_pos.y) / size.y) * m_Size.y), col);
        }
        /// <summary>
        /// Draw a line start from sx,sy to ex,ey
        /// </summary>
        /// <param name="sx">start position of x</param>
        /// <param name="sy">start position of y</param>
        /// <param name="ex">end position of x</param>
        /// <param name="ey">end position of y</param>
        /// <param name="line_col">color of line</param>
        virtual public void DrawLine(float sx, float sy, float ex, float ey, Color col) {
            DrawLine(Mathf.RoundToInt(sx * m_Size.x), Mathf.RoundToInt(sy * m_Size.y)
                , Mathf.RoundToInt(ex * m_Size.x), Mathf.RoundToInt(ey * m_Size.y), col);
        }
        virtual public void DrawPath(UCL.Core.MathLib.UCL_Path _Path, RectTransform field, Color col,
            VectorExtensionMethods.Vec3ToVec2 dir = VectorExtensionMethods.Vec3ToVec2.xy, int seg_count = 20) {
            Vector3[] Corners = new Vector3[4];
            field.GetWorldCorners(Corners);
            Vector2 Min = Corners[0];
            Vector2 Max = Corners[2];
            for(int i = 0; i < 4; i++) {
                var point = Corners[i];
                if(point.x < Min.x) {
                    Min.x = point.x;
                }
                if(point.x > Max.x) {
                    Max.x = point.x;
                }
                if(point.y < Min.y) {
                    Min.y = point.y;
                }
                if(point.y > Max.y) {
                    Max.y = point.y;
                }
            }
            DrawPath(_Path, Min, Max - Min, col, dir, seg_count);
        }
        virtual public void DrawPath(UCL.Core.MathLib.UCL_Path _Path, Vector2 size, Color col,
            VectorExtensionMethods.Vec3ToVec2 dir = VectorExtensionMethods.Vec3ToVec2.xy, int seg_count = 20) {
            DrawPath(_Path, Vector2.zero, size, col, dir, seg_count);
        }
        virtual public void DrawPath(UCL.Core.MathLib.UCL_Path _Path, Rect rect, Color col,
            VectorExtensionMethods.Vec3ToVec2 dir = VectorExtensionMethods.Vec3ToVec2.xy, int seg_count = 20) {
            DrawPath(_Path, rect.min, rect.size, col, dir, seg_count);
        }
        virtual public void DrawPath(UCL.Core.MathLib.UCL_Path _Path, Vector2 start_pos, Vector2 size, Color col,
            VectorExtensionMethods.Vec3ToVec2 dir = VectorExtensionMethods.Vec3ToVec2.xy, int seg_count = 20) {
            if(seg_count < 1) seg_count = 1;
            Vector2 prev_pos = _Path.GetPos(0).ToVec2(dir);
            for(int i = 1; i <= seg_count; i++) {
                float at = (i / (float)seg_count);
                Vector2 pos = _Path.GetPos(at).ToVec2(dir);

                DrawLine((prev_pos.x - start_pos.x) / size.x, (prev_pos.y - start_pos.y) / size.y,
                    (pos.x - start_pos.x) / size.x, (pos.y - start_pos.y) / size.y, col);
                prev_pos = pos;
            }
        }
        virtual public void DrawPixel(int iX, int iY, Color iCol) {
            m_TextureUpdated = true;
            m_SpriteUpdated = true;
            if(iX < 0) iX = 0;
            if(iY < 0) iY = 0;
            if(iX >= width) iX = width - 1;
            if(iY >= height) iY = height - 1;
            int at = iX + iY * width;
            if(iCol.a < 1f && m_Col[at] != null) {
                Color o_col = m_Col[at];
                Color new_col = Color.Lerp(o_col, iCol, iCol.a);
                new_col.a = Mathf.Max(o_col.a, new_col.a);
                m_Col[at] = new_col;
            } else {
                m_Col[at] = iCol;
            }
            //Debug.LogWarning(("Pos:") + (x + y * Width) + "Col:" + col);
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

