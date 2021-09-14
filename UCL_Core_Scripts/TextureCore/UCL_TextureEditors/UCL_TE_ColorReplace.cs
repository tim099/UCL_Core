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
        [Header("Replace color between Max and Min to m_Color")]
        public Color m_Color;

        public bool m_GreyScaleAlpha = false;


        [ATTR.UCL_FunctionButton]
        public void ReplaceColor() {
            int aWidth = m_Texture.width;
            int aHeight = m_Texture.height;
            var aTexture = new Texture2D(aWidth, aHeight, m_TextureFormat, false);
            for(int y = 0; y < aHeight; y++) {
                for(int x = 0; x < aWidth; x++) {
                    var aCol = m_Texture.GetPixel(x, y);
                    if(m_GreyScaleAlpha) {
                        aCol.a = (aCol.r + aCol.g + aCol.b) / 3f;
                    }
                    if(aCol.IsBetween(m_ReplaceColorMin, m_ReplaceColorMax)) {
                        aCol = m_Color;
                    }

                    aTexture.SetPixel(x, y, aCol);

                }
            }
            SaveTexture(aTexture);
        }
    }
}