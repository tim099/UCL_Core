using System;
namespace UCL.Core.JsonLib
{
    public static partial class UCL_JsonExtension
    {
        /// <summary>
        /// Copy data from iSrc to iDst
        /// </summary>
        /// <param name="iDst"></param>
        /// <param name="iSrc"></param>
        public static void Copy(this IJsonSerializable iDst, IJsonSerializable iSrc)
        {
            iDst.DeserializeFromJson(iSrc.SerializeToJson());
        }
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
            if (iObj is Enum)
            {
                return iObj.ToString();
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
            if (iType.IsEnum)
            {
                return Enum.Parse(iType, iJsonSafeString);
            }
            if (iType.IsNumber())
            {
                return iType.TryParseToNumber(iJsonSafeString);
            }
            string aJson = iJsonSafeString.Replace("\\\"", "\"");
            object aObj = JsonConvert.LoadDataFromJson(JsonData.ParseJson(aJson), iType);
            //UnityEngine.Debug.LogError("aJson:" + aJson + ",iType:" + iType.ToString()+ ",aObj:"+ aObj.UCL_ToString());
            return aObj;
            //return iJsonSafeString.HexStringToByteArray().ToStructure(iType);
        }
    }
}