using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core {
    public static class UCL_RectTransformLib{
        public static void SetFullScreen(RectTransform rect) {
            rect.anchorMin = new Vector2(0, 0);
            rect.anchorMax = new Vector2(1, 1);
            rect.sizeDelta = new Vector2(0, 0);
        }
    }
}