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
            Vector2Int aPrevDrawPos = iDataDic.GetData("PrevPos", Vector2Int.left);//Position in PrevFrame
            Vector2Int aCurPos = aRect.GetPosIntInRect(aMousePos);

            //Vector2Int aPrevDrawPos = iDataDic.GetData("PrevDrawPos", Vector2Int.left);
            
            System.Func<Vector2Int, Vector2Int> GetDrawPos = (iPos) =>
            {
                Vector2Int aPos = iPos;
                if (iTexture.width != iWidth)
                {
                    aPos.x = Mathf.FloorToInt((aCurPos.x * iTexture.width) / iWidth);
                    if (aPos.x < 0) aPos.x = 0;
                    if (aPos.x >= iTexture.width) aPos.x = iTexture.width - 1;
                }
                if (iTexture.height != iHeight)
                {
                    aPos.y = Mathf.FloorToInt((aCurPos.y * iTexture.height) / iHeight);
                    if (aPos.y < 0) aPos.y = 0;
                    if (aPos.y >= iTexture.height) aPos.y = iTexture.height - 1;
                }
                return aPos;
            };
            Vector2Int aDrawPos = GetDrawPos(aCurPos);
            
            Vector2 aPrevMousePos = iDataDic.GetData("PrevMousePos", Vector2.negativeInfinity);


            int aOutTimer = iDataDic.GetData("OutTimer", 0);//the time ouside of border
            //GUILayout.Label("Timer:" + (aTimer++) + ",OutTimer:" + aOutTimer);

            if (aCurrentEvent.type == EventType.MouseUp)
            {
                aPrevMousePos = Vector2.negativeInfinity;
                aPrevDrawPos = Vector2Int.left;
                iDataDic.SetData("PrevMousePos", Vector2.negativeInfinity);
                iDataDic.SetData("PrevPos", Vector2Int.left);
            }

            if (aRect.Contains(aMousePos))//In paint range
            {
                if (aCurrentEvent.type == EventType.MouseDown)//Draw a Dot
                {
                    //Debug.LogError("aDrawPos:" + aDrawPos);
                    iTexture.SetPixel(aDrawPos.x, aDrawPos.y, iDrawCol);
                    iTexture.Apply();
                    aIsUpdated = true;
                    aPrevDrawPos = aDrawPos;
                }
                else if (aCurrentEvent.type == EventType.MouseDrag)
                {
                    if (aRect.Contains(aPrevMousePos))
                    {
                        if (aPrevDrawPos != Vector2Int.left)
                        {
                            iTexture.SetPixel(aDrawPos.x, aDrawPos.y, iDrawCol);
                            iTexture.DrawLine(aPrevDrawPos, aDrawPos, iDrawCol);
                            aIsUpdated = true;
                        }
                    }
                    else if (aPrevMousePos != Vector2.negativeInfinity)//out of range
                    {
                        var aPos = GetDrawPos(aRect.GetPosIntOnBorder(aMousePos, aPrevMousePos));
                        //Debug.LogError("aPrevDrawPos:" + aPrevDrawPos + ",aPos:" + aPos+ ",aPrevMousePos:"+ aPrevMousePos+ ",aMousePos:"+ aMousePos);
                        iTexture.SetPixel(aPos.x, aPos.y, iDrawCol);
                        iTexture.DrawLine(aPos, aDrawPos, iDrawCol);
                        aIsUpdated = true;
                    }
                    aPrevDrawPos = aDrawPos;
                }
                iDataDic.SetData("PrevPos", aPrevDrawPos);
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
                    if (aRect.Contains(aPrevMousePos) && aPrevDrawPos != Vector2.left)//Prev pos in Rect
                    {
                        
                        var aPos = GetDrawPos(aRect.GetPosIntOnBorder(aPrevMousePos, aMousePos));
                        //Debug.LogError("aPrevDrawPos:" + aPrevDrawPos + ",aPos:" + aPos);
                        if (aPrevDrawPos != aPos)
                        {
                            iTexture.DrawLine(aPrevDrawPos, aPos, iDrawCol);
                        }
                        
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