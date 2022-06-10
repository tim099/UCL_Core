using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.TextureLib {
    public class UCL_Texture2D : UCLI_Texture2D {
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
        /// <summary>
        /// Constructor without Init
        /// </summary>
        public UCL_Texture2D() {

        }
        public UCL_Texture2D(byte[] iBytes)
        {
            Init(UCL.Core.TextureLib.Lib.CreateTexture(iBytes));
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
            ClearTexture();
        }
        public void ClearTexture() {
            //Debug.LogWarning("ClearTexture()!!");
            if (m_Texture != null) {
                //Debug.LogWarning("GameObject.Destroy Not working, Memory leak!!");
                Texture2D.DestroyImmediate(m_Texture);
                m_Texture = null;
                //GameObject.Destroy();
            }
            if(m_Sprite != null)
            {
                Sprite.DestroyImmediate(m_Sprite);
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
            for(int i = 0,len = m_Col.Length; i < len ; i++) {
                m_Col[i] = iColor;
            }
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

        /// <summary>
        /// line_func take x as parameter and should return the value of y
        /// y range between 0 ~ 1
        /// </summary>
        /// <param name="iLineFunc"></param>
        /// <param name="iLineCol"></param>
        virtual public void DrawLine(System.Func<float,float> iLineFunc,Color iLineCol) {
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
        virtual public void SetPixel(Vector2Int pos, Color col) {
            m_TextureUpdated = true;
            m_SpriteUpdated = true;
            if(pos.x < 0) pos.x = 0;
            if(pos.y < 0) pos.y = 0;
            if(pos.x >= width) pos.x = width - 1;
            if(pos.y >= height) pos.y = height - 1;
            m_Col[pos.x + pos.y * width] = col;
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

