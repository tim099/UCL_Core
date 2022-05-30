using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UCL.Core
{
    /// <summary>
    /// Replace the name field in UCL_GUILayout.DrawObjectData
    /// UCL_GUILayout.DrawObjectData繪製時 會呼叫NameOnGUI()取代預設的FieldName顯示
    /// </summary>
    public interface UCLI_NameOnGUI
    {
        void NameOnGUI(UCL_ObjectDictionary iDic, string iDisplayName);
    }
}