using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public static partial class RectExtensionMethods
{
    static public Vector2 GetPosInRect(this Rect iRect, Vector2 iPos)
    {
        return new Vector2(iPos.x - iRect.position.x, iRect.height - iPos.y);
    }
    static public Vector2Int GetPosIntInRect(this Rect iRect, Vector2 iPos)
    {
        return new Vector2Int(Mathf.RoundToInt(iPos.x - iRect.position.x), Mathf.RoundToInt(iRect.height + iRect.position.y - iPos.y));
    }
    static public Vector2Int GetPosIntOnBorder(this Rect iRect, Vector2 iPosA, Vector2 iPosB)
    {
        bool aAinBorder = iRect.Contains(iPosA);
        bool aBinBorder = iRect.Contains(iPosB);
        if (aAinBorder && aBinBorder) return default;
        if (!aAinBorder && !aBinBorder) return default;
        Vector2Int aInPos;
        Vector2Int aOutPos;
        if (aAinBorder)
        {
            aInPos = iRect.GetPosIntInRect(iPosA);
            aOutPos = iRect.GetPosIntInRect(iPosB);
        }
        else
        {
            aInPos = iRect.GetPosIntInRect(iPosB);
            aOutPos = iRect.GetPosIntInRect(iPosA);
        }
        Vector2Int aDel = aOutPos - aInPos;
        if (aDel.x == 0)
        {
            if (aDel.y > 0) return new Vector2Int(aInPos.x, Mathf.RoundToInt(iRect.height - 1));
            else return new Vector2Int(aInPos.x, 0);
        }
        if(aDel.y == 0)
        {
            if (aDel.x > 0) return new Vector2Int(Mathf.RoundToInt(iRect.width - 1), aInPos.y);
            else return new Vector2Int(0 , aInPos.y);
        }
        if (aDel.x > 0)
        {
            float aDisX = iRect.width - aInPos.x;
            float aTX = aDisX / aDel.x;
            if (aDel.y > 0)
            {
                float aDisY = iRect.height - aInPos.y;
                float aTY = aDisY / aDel.y;
                if (aTY < aTX)
                {
                    return new Vector2Int(Mathf.RoundToInt(aInPos.x + (aTY / aTX) * aDel.x), Mathf.RoundToInt(iRect.height - 1));
                }
                else
                {
                    return new Vector2Int(Mathf.RoundToInt(iRect.width - 1), Mathf.RoundToInt(aInPos.y + (aTX / aTY) * aDel.y));
                }
            }
            else//aDel.y < 0
            {
                float aDisY = aInPos.y;
                float aTY = -aDisY / aDel.y;
                if (aTY < aTX)
                {
                    return new Vector2Int(Mathf.RoundToInt(aInPos.x + (aTY / aTX) * aDel.x), 0);
                }
                else
                {
                    return new Vector2Int(Mathf.RoundToInt(iRect.width - 1), Mathf.RoundToInt(aInPos.y + (aTX / aTY) * aDel.y));
                }
            }
        }
        else //aDel.x < 0
        {
            float aDisX = aInPos.x;
            float aTX = -aDisX / aDel.x;
            if (aDel.y > 0)
            {
                float aDisY = iRect.height - aInPos.y;
                float aTY = aDisY / aDel.y;
                if (aTY < aTX)
                {
                    return new Vector2Int(Mathf.RoundToInt(aInPos.x + (aTY / aTX) * aDel.x), Mathf.RoundToInt(iRect.height - 1));
                }
                else
                {
                    return new Vector2Int(0, Mathf.RoundToInt(aInPos.y + (aTX / aTY) * aDel.y));
                }
            }
            else//aDel.y < 0
            {
                float aDisY = aInPos.y;
                float aTY = -aDisY / aDel.y;
                if (aTY < aTX)
                {
                    return new Vector2Int(Mathf.RoundToInt(aInPos.x + (aTY / aTX) * aDel.x), 0);
                }
                else
                {
                    return new Vector2Int(0, Mathf.RoundToInt(aInPos.y + (aTX / aTY) * aDel.y));
                }
            }
        }
    }
}
