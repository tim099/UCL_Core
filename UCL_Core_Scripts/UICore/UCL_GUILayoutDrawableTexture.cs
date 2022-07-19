using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.UI
{
    static public partial class UCL_GUILayout
    {
        /// <summary>
        /// Draw on iTexture
        /// </summary>
        /// <param name="iTexture"></param>
        /// <param name="iDataDic"></param>
        /// <param name="iWidth">Canvas width</param>
        /// <param name="iHeight">Canvas height</param>
        /// <param name="iDrawCol">Draw color</param>
        /// <returns></returns>
        static public bool DrawableTexture(Texture2D iTexture, UCL_ObjectDictionary iDataDic, float iWidth, float iHeight, Color iDrawCol)
        {
            bool aIsUpdated = false;
            var aRect = DrawTexture(iTexture, iWidth, iHeight);
            var aCurrentEvent = Event.current;

            var aMousePos = aCurrentEvent.mousePosition;
            Vector2Int aPrevPos = iDataDic.GetData("PrevPos", Vector2Int.left);//Position in PrevFrame
            Vector2Int aCurPos = aRect.GetPosIntInRect(aMousePos);

            //Vector2Int aPrevDrawPos = iDataDic.GetData("PrevDrawPos", Vector2Int.left);
            Vector2Int aDrawPos = aCurPos;

            if (iTexture.width != iWidth)
            {
                aDrawPos.x = Mathf.FloorToInt((aCurPos.x * iTexture.width) / iWidth);
                if (aDrawPos.x < 0) aDrawPos.x = 0;
                if (aDrawPos.x >= iTexture.width) aDrawPos.x = iTexture.width - 1;
            }
            if (iTexture.height != iHeight)
            {
                aDrawPos.y = Mathf.FloorToInt((aCurPos.y * iTexture.height) / iHeight);
                if (aDrawPos.y < 0) aDrawPos.y = 0;
                if (aDrawPos.y >= iTexture.height) aDrawPos.x = iTexture.height - 1;
            }
            Vector2 aPrevMousePos = iDataDic.GetData("PrevMousePos", Vector2.negativeInfinity);


            int aOutTimer = iDataDic.GetData("OutTimer", 0);//the time ouside of border
            //GUILayout.Label("Timer:" + (aTimer++) + ",OutTimer:" + aOutTimer);

            if (aCurrentEvent.type == EventType.MouseUp)
            {
                aPrevMousePos = Vector2.negativeInfinity;
                aPrevPos = Vector2Int.left;
                iDataDic.SetData("PrevMousePos", Vector2.negativeInfinity);
                iDataDic.SetData("PrevPos", Vector2Int.left);
            }

            if (aRect.Contains(aMousePos))
            {
                if (aCurrentEvent.type == EventType.MouseDown)
                {
                    iTexture.SetPixel(aDrawPos.x, aDrawPos.y, iDrawCol);
                    iTexture.Apply();
                    aIsUpdated = true;
                    aPrevPos = aCurPos;
                }
                else if (aCurrentEvent.type == EventType.MouseDrag)
                {
                    if (aRect.Contains(aPrevMousePos))
                    {
                        if (aPrevPos != Vector2Int.left)
                        {
                            iTexture.DrawLine(aPrevPos, aCurPos, iDrawCol);
                            aIsUpdated = true;
                        }
                    }
                    else if (aPrevMousePos != Vector2.negativeInfinity)
                    {
                        var aPos = aRect.GetPosIntOnBorder(aMousePos, aPrevMousePos);
                        iTexture.DrawLine(aPos, aCurPos, iDrawCol);
                        aIsUpdated = true;
                    }
                    aPrevPos = aCurPos;
                }
                iDataDic.SetData("PrevPos", aPrevPos);
                iDataDic.SetData("PrevMousePos", aMousePos);
                iDataDic.SetData("OutTimer", 0);
            }
            else//out of range
            {
                iDataDic.SetData("OutTimer", aOutTimer + 1);
                if (aCurrentEvent.type == EventType.MouseDown)
                {
                    iDataDic.SetData("PrevPos", aCurPos);
                    iDataDic.SetData("PrevMousePos", aMousePos);
                }
                else if (aCurrentEvent.type == EventType.MouseDrag)
                {
                    if (aRect.Contains(aPrevMousePos) && aPrevPos != Vector2.left)//Prev pos in Rect
                    {
                        //Debug.LogError("aPrevPos:" + aPrevPos + ",aPrevMousePos:" + aPrevMousePos+ ",aOutTimer:"+ aOutTimer);
                        var aPos = aRect.GetPosIntOnBorder(aPrevMousePos, aMousePos);
                        iTexture.DrawLine(aPrevPos, aPos, iDrawCol);
                        iDataDic.SetData("PrevMousePos", aMousePos);
                        aIsUpdated = true;
                    }
                    iDataDic.SetData("PrevPos", aCurPos);
                }
                if (aOutTimer > 2)
                {
                    iDataDic.SetData("OutTimer", 0);
                    iDataDic.SetData("PrevMousePos", aMousePos);
                    iDataDic.SetData("PrevPos", Vector2.left);
                }
            }
            //if (aIsUpdated)
            {
                s_RequireRepaint = true;
            }
            return aIsUpdated;
        }
    }
}