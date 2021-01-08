using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.TextureLib {
    [ATTR.EnableUCLEditor]
    public class UCL_TE_ColorReplace : UCL_TextureEditor {
        [Header("Min value of ReplaceColor")]
        public Color m_ReplaceColorMin;
        [Header("Max value of ReplaceColor")]
        public Color m_ReplaceColorMax;

        public Color m_Color;

        public bool m_GreyScaleAlpha = false;
        [ATTR.UCL_FunctionButton]
        public void ReplaceColor() {
            int width = m_Texture.width;
            int height = m_Texture.height;
            var texture = new Texture2D(width, height, m_TextureFormat, false);
            for(int y = 0; y < height; y++) {
                for(int x = 0; x < width; x++) {
                    var col = m_Texture.GetPixel(x,y);
                    if(m_GreyScaleAlpha) {
                        col.a = (col.r + col.g + col.b) / 3f;
                    }
                    if(col.IsBetween(m_ReplaceColorMin, m_ReplaceColorMax)) {
                        col = m_Color;
                    }

                    texture.SetPixel(x, y, col);

                }
            }
            SaveTexture(texture);
        }
    }
}