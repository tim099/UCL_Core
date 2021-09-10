using System;
namespace UCL.Core.JsonLib
{
    public static partial class UCL_JsonExtension
    {
        public static string ToHexString(this object iObj)
        {
            if (iObj == null) return string.Empty;
            return iObj.ToByteArray().ToHexString();
        }
        /// <summary>
        /// Convert object into a json safe format string
        /// </summary>
        /// <param name="iObj"></param>
        /// <returns></returns>
        public static string ConvertToJsonSafeString(this object iObj)
        {
            if (iObj == null) return string.Empty;
            if (iObj is string)
            {
                return ((string)iObj).Replace("\"", "\\\"");
            }
            Type aType = iObj.GetType();
            if (aType.IsNumber())
            {
                return iObj.ToString();
            }
            
            return JsonConvert.SaveDataToJson(iObj).ToJson().Replace("\"", "\\\"");
        }

        /// <summary>
        /// Convert JsonSafeString back to object
        /// </summary>
        /// <param name="iJsonSafeString"></param>
        /// <param name="iType">Type of object</param>
        /// <returns></returns>
        public static object JsonSafeStringToObject(this string iJsonSafeString, Type iType)
        {
            if (iType.IsString())
            {
                return iJsonSafeString.Replace("\\\"", "\"");
            }
            if (iType.IsNumber())
            {
                return iType.TryParseToNumber(iJsonSafeString);
            }
            string aJson = iJsonSafeString.Replace("\\\"", "\"");
            object aObj = JsonConvert.LoadDataFromJson(aJson, iType);
            //UnityEngine.Debug.LogError("aJson:" + aJson + ",iType:" + iType.ToString()+ ",aObj:"+ aObj.UCL_ToString());
            return aObj;
            //return iJsonSafeString.HexStringToByteArray().ToStructure(iType);
        }
    }
}