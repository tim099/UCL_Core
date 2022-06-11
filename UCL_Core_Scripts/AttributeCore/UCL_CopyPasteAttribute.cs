using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UCL.Core.JsonLib;
using System;
namespace UCL.Core
{
    public interface UCLI_CopyPaste
    {

    }
    public static class CopyPaste
    {
        public static JsonData CopyData => s_CopyData;

        public static UCL.Core.JsonLib.JsonData s_CopyData = null;
        public static System.Type s_CopyType = null;
        public static bool HasCopyData(System.Type iType)
        {
            //if(s_CopyType != null && iType != null)
            //{
            //    Debug.LogError("s_CopyType:" + s_CopyType.Name + ",iType:" + iType.Name +
            //        ",iType.IsAssignableFrom(s_CopyType):" + iType.IsAssignableFrom(s_CopyType));
            //}
            if (s_CopyType == null || iType == null || !iType.IsAssignableFrom(s_CopyType)) return false;
            return true;
        }
        public static object GetCopyData(System.Type iType, JsonConvert.SaveMode iSaveMode = JsonConvert.SaveMode.Unity)
        {
            if(!HasCopyData(iType))
            {
                return default;
            }
            //Debug.LogError("s_CopyData:" + s_CopyData.ToJsonBeautify());
            return JsonConvert.LoadDataFromJson(s_CopyData, iType, iSaveMode);
        }
        public static object GetCopyData(JsonConvert.SaveMode iSaveMode = JsonConvert.SaveMode.Unity)
        {
            //Debug.LogError("s_CopyData:" + s_CopyData.ToJsonBeautify());
            return JsonConvert.LoadDataFromJson(s_CopyData, s_CopyType, iSaveMode);
        }
        public static void LoadCopyData(object iObj, JsonConvert.SaveMode iSaveMode = JsonConvert.SaveMode.Unity)
        {
            if (iObj == null || !HasCopyData(iObj.GetType())) return;
            JsonConvert.LoadDataFromJson(iObj, s_CopyData, iSaveMode);
        }
        public static void SetCopyData(JsonData iData, System.Type iType)
        {
            if (iData == null)
            {
                s_CopyData = null;
                s_CopyType = null;
                return;
            }

            s_CopyType = iType;
            s_CopyData = iData;

        }
        public static void SetCopyData(object iData, JsonConvert.SaveMode iSaveMode = JsonConvert.SaveMode.Unity)
        {
            if (iData == null)
            {
                s_CopyData = null;
                s_CopyType = null;
                return;
            }

            s_CopyType = iData.GetType();
            s_CopyData = JsonConvert.SaveDataToJson(iData, iSaveMode);
            //Debug.LogError("SetCopyData s_CopyType:" + s_CopyType.Name + ",s_CopyData:" + s_CopyData.ToJsonBeautify());
        }
    }
}
namespace UCL.Core.ATTR
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class UCL_CopyPasteAttribute : UCL_Attribute
    {

        public UCL_CopyPasteAttribute() { }
    }
}