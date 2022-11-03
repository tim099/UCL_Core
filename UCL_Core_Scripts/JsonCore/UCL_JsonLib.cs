using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;

namespace UCL.Core.JsonLib {
    public static class JsonConvert {
        public enum SaveMode
        {
            /// <summary>
            /// Both Public and NonPublic field will be save to Json
            /// </summary>
            /// <language>en-US</language>

            /// <summary>
            /// Public與NonPublic都會被存到Json
            /// </summary>
            /// <language>zh-TW</language>
            Normal,

            /// <summary>
            /// Ignore Fields with [HideInInspector]
            /// Ignore NonPublic Fields whithout[SerializeField]
            /// </summary>
            /// <language>en-US</language>

            /// <summary>
            /// [HideInInspector]的部分不會被保存到Json
            /// NonPublic部分如果有[SerializeField]則會被保存到Json
            /// </summary>
            /// <language>zh-TW</language>
            Unity,
        }

        const int MaxParsingLayer = 100;

        /// <summary>
        /// Convert JsonData into object
        /// 將JsonData轉換成物件
        /// </summary>
        /// <param name="iData"></param>
        /// <returns></returns>
        static public object JsonToObject(JsonData iData, SaveMode iSaveMode = SaveMode.Normal, System.Func<string, string> iFieldNameAlterFunc = null) {
            if(!iData.Contains("ClassName") || !iData.Contains("ClassData")) {
                Debug.LogError("JsonToObject !iData.Contains(ClassName) || !iData.Contains(ClassData) iData:"+ iData.ToJsonBeautify());
                return null;
            }
            string aClassName = iData.GetString("ClassName");
            Type aClassType = Type.GetType(aClassName);
            JsonData aClassData = iData["ClassData"];
            object aObj = aClassType.CreateInstance();//Activator.CreateInstance();
            if (aObj is IJsonSerializable)
            {
                ((IJsonSerializable)aObj).DeserializeFromJson(aClassData);
            }
            else
            {
                LoadDataFromJson(aObj, aClassData, iSaveMode, iFieldNameAlterFunc);
            }
            return aObj;
            //return JsonUtility.FromJson(aClassData.ToJson(), aClassType);
        }
        /// <summary>
        /// Save object into JsonData(also save AssemblyQualifiedName so can convert back to object)
        /// </summary>
        /// <param name="iObj"></param>
        /// <returns></returns>
        static public JsonData ObjectToJson(object iObj, SaveMode iSaveMode = SaveMode.Normal, System.Func<string, string> iFieldNameAlterFunc = null) {
            JsonData aData = new JsonData();
            aData["ClassName"] = iObj.GetType().AssemblyQualifiedName;
            JsonData aClassData = null;
            if(iObj is IJsonSerializable)
            {
                aClassData = ((IJsonSerializable)iObj).SerializeToJson();
            }
            else
            {
                aClassData = SaveDataToJson(iObj, iSaveMode, iFieldNameAlterFunc);
            }
            aData["ClassData"] = aClassData;
            return aData;
        }
        /// <summary>
        /// Load data from Json
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="iData"></param>
        /// <returns></returns>
        static public T LoadDataFromJsonUnityVer<T>(JsonData iData) where T : new()
            => LoadDataFromJson<T>(iData, SaveMode.Unity, UCL.Core.UCL_StaticFunctions.FieldNameUnityVer);
        /// <summary>
        /// Load data from Json
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="iData"></param>
        /// <param name="iSaveMode"></param>
        /// <returns></returns>
        static public T LoadDataFromJson<T>(JsonData iData, SaveMode iSaveMode = SaveMode.Normal, System.Func<string, string> iFieldNameAlterFunc = null) where T : new() {
            Type aType = typeof(T);
            var aValue = iData.GetValue(aType);
            if(aValue != null) return (T)aValue;
            return (T)LoadDataFromJson(new T(), iData, iSaveMode, iFieldNameAlterFunc);
        }
        /// <summary>
        /// Load data from Json(UnityVer
        /// </summary>
        /// <returns></returns>
        static public object LoadDataFromJsonUnityVer(JsonData iData, Type iType) => LoadDataFromJson(iData, iType, JsonConvert.SaveMode.Unity, UCL.Core.UCL_StaticFunctions.FieldNameUnityVer);
        /// <summary>
        /// Load data from Json
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="iData"></param>
        /// <param name="iType"></param>
        /// <param name="iSaveMode"></param>
        /// <param name="iFieldNameAlterFunc"></param>
        /// <returns></returns>
        static public object LoadDataFromJson(JsonData iData, Type iType, SaveMode iSaveMode = SaveMode.Normal, System.Func<string, string> iFieldNameAlterFunc = null) {
            var aValue = iData.GetValue(iType);
            if (aValue != null)
            {
                return aValue;
            }
            
            return LoadDataFromJson(iType.CreateInstance(), iData, iSaveMode, iFieldNameAlterFunc);
        }
        /// <summary>
        /// Convert data in iObj into JsonData(UnityVer)
        /// Be aware don't call SaveDataToJson(this) in IJsonSerializable.SerializeToJson() to prevent infinite recursion(Call SaveFieldsToJson instead!!)
        /// 請勿在IJsonSerializable.SerializeToJson()內呼叫(避免infinite recursion
        /// </summary>
        /// <param name="iObj"></param>
        /// <returns></returns>
        static public JsonData SaveDataToJsonUnityVer(object iObj) => SaveDataToJson(iObj, JsonConvert.SaveMode.Unity, UCL.Core.UCL_StaticFunctions.FieldNameUnityVer);

        /// <summary>
        /// Create Object using JsonData
        /// 根據傳入的Type與JsonData生成物件
        /// </summary>
        /// <param name="iData"></param>
        /// <param name="iType">type of objet</param>
        /// <returns></returns>
        static object DataToObject(JsonData iData, Type iType, SaveMode iSaveMode = SaveMode.Normal, System.Func<string, string> iFieldNameAlterFunc = null)
        {
            if(iType == typeof(JsonData))
            {
                return iData;
            }
            else if (typeof(UnityJsonSerializableObject).IsAssignableFrom(iType))
            {
                try
                {
                    string aClassName = iData.GetString("ClassName");
                    Type aClassType = Type.GetType(aClassName);
                    if (aClassType != null)
                    {
                        JsonData aClassData = iData.GetString("ClassData");
                        UnityJsonSerializableObject aObj = aClassType.CreateInstance() as UnityJsonSerializableObject;
                        aObj.DeserializeFromJson(iData);
                        return aObj;
                    }
                    else
                    {
                        Debug.LogError("DataToObject aClassName:" + aClassName + ", Type not found!");
                        return null;
                    }
                }
                catch(System.Exception iE)
                {
                    Debug.LogException(iE);
                    return null;
                }
            }
            else if (typeof(IJsonSerializable).IsAssignableFrom(iType))
            {
                IJsonSerializable aObj = null;
                try
                {
                    aObj = iType.CreateInstance() as IJsonSerializable;
                }
                catch (System.Exception iE)
                {
                    Debug.LogException(iE);
                }
                if (aObj != null) aObj.DeserializeFromJson(iData);
                return aObj;
            }
            else if (iType.IsEnum)
            {
                string aStr = iData.GetString();
                try
                {
                    if (!string.IsNullOrEmpty(aStr))
                    {
                        return Enum.Parse(iType, aStr, true) as Enum;
                    }
                    else
                    {
                        return null;
                    }
                }
                catch(System.Exception iE)
                {
                    Debug.LogException(iE);
                    return null;
                }
            }
            else if(iType == typeof(string))
            {
                return iData.GetString();
            }
            else if (iType == typeof(int))
            {
                return iData.GetInt();
            }
            else if (iType == typeof(float))
            {
                return iData.GetFloat();
            }
            else if (iType == typeof(double))
            {
                return iData.GetDouble();
            }
            else if (iType.IsTuple())
            {
                Type[] aTypeArray = iType.GetGenericArguments();

                var aConstructer = iType.GetConstructor(aTypeArray);
                if (aConstructer != null && aTypeArray.Length == iData.Count)
                {
                    object[] aValues = new object[aTypeArray.Length];
                    for (int i = 0; i < aTypeArray.Length; i++)
                    {
                        aValues[i] = DataToObject(iData[i], aTypeArray[i], iSaveMode, iFieldNameAlterFunc);
                    }
                    return aConstructer.Invoke(aValues);
                }
            }
            else if (iType.IsStructOrClass())
            {
                object aObj = null;
                try
                {
                    aObj = iType.CreateInstance();
                }
                catch(System.Exception iE)
                {
                    Debug.LogException(iE);
                }
                if (aObj != null)
                {
                    return LoadDataFromJson(aObj, iData, iSaveMode, iFieldNameAlterFunc);
                }
                return null;
            }

            return iData.GetObj();
        }
        /// <summary>
        /// Convert Object into JsonData
        /// </summary>
        /// <param name="iObj"></param>
        /// <param name="iSaveMode"></param>
        /// <param name="iFieldNameAlterFunc"></param>
        /// <returns></returns>
        static JsonData ObjectToData(object iObj, SaveMode iSaveMode, System.Func<string, string> iFieldNameAlterFunc = null)
        {
            if (iObj == null) return null;
            if (iObj is JsonData) return iObj as JsonData;

            Type aType = iObj.GetType();
            if (aType.IsEnum)
            {
                return new JsonData(iObj.ToString());
            }
            else if (iObj is IJsonSerializable)
            {
                return ((IJsonSerializable)iObj).SerializeToJson();
            }
            else if (iObj.IsNumber() || iObj is string)
            {
                return new JsonData(iObj);
            }
            else if (aType.IsTuple())
            {
                var aResult = iObj.GetTupleElements();
                JsonData aData = new JsonData();
                aData.ToArray();
                for (int i = 0; i < aResult.Count; i++)
                {
                    aData.Add(ObjectToData(aResult[i], iSaveMode, iFieldNameAlterFunc));
                }
                return aData;
            }
            else if (aType.IsStructOrClass())
            {
                return SaveDataToJson(iObj, iSaveMode, iFieldNameAlterFunc);
            }

            return new JsonData(iObj);
        }
        /// <summary>
        /// Save iObj into iData
        /// Be aware don't call SaveDataToJson(this) in IJsonSerializable.SerializeToJson() to prevent infinite recursion(Call SaveFieldsToJson instead!!)
        /// </summary>
        /// <param name="iObj"></param>
        /// <param name="iData"></param>
        /// <param name="iFieldNameAlterFunc">Input is the original fieldname, and return the name you want to save as json key</param>
        static public JsonData SaveDataToJson(object iObj, SaveMode iSaveMode = SaveMode.Normal, System.Func<string, string> iFieldNameAlterFunc = null)
        {
            Type aType = iObj.GetType();
            //Debug.LogError("SaveDataToJson:" +aType.Name);
            if(iObj is IJsonSerializable)
            {
                return ((IJsonSerializable)iObj).SerializeToJson();
            } else if (iObj is IList)
            {
                JsonData iData = new JsonData();
                IList aList = iObj as IList;
                var aItemType = aType.GetGenericValueType();
                //Debug.LogError("aItemType:" + aItemType.Name);
                if (typeof(UCLI_TypeList).IsAssignableFrom(aItemType) && !typeof(UnityJsonSerializableObject).IsAssignableFrom(aItemType))
                {
                    //Debug.LogError("typeof(UCLI_TypeList).IsAssignableFrom(aItemType) aItemType:" + aItemType.Name);
                    foreach (var aItem in aList)
                    {
                        iData.Add(ObjectToJson(aItem, iSaveMode, iFieldNameAlterFunc));
                    }
                }
                else
                {
                    foreach (var aItem in aList)
                    {
                        iData.Add(ObjectToData(aItem, iSaveMode, iFieldNameAlterFunc));
                    }
                }

                return iData;
            } else if(iObj is IDictionary)
            {
                JsonData aData = new JsonData();
                IDictionary aDic = iObj as IDictionary;
                foreach (var aKey in aDic.Keys)
                {
                    aData[aKey.ConvertToJsonSafeString()] = ObjectToData(aDic[aKey], iSaveMode, iFieldNameAlterFunc);
                }
                return aData;
            }
            return SaveFieldsToJson(iObj, iSaveMode, iFieldNameAlterFunc);
        }
        static public JsonData SaveFieldsToJsonUnityVer(object iObj)
            => SaveFieldsToJson(iObj, JsonConvert.SaveMode.Unity, UCL.Core.UCL_StaticFunctions.FieldNameUnityVer);
        static public JsonData SaveFieldsToJson(object iObj, SaveMode iSaveMode = SaveMode.Normal, System.Func<string, string> iFieldNameAlterFunc = null)
        {
            JsonData aData = new JsonData();
            Type aType = iObj.GetType();

            List<FieldInfo> aFields = null;
            switch (iSaveMode)
            {

                case SaveMode.Unity:
                    {
                        aFields = aType.GetAllFieldsUnityVer(typeof(object));
                        break;
                    }
                case SaveMode.Normal:
                default:
                    {
                        aFields = aType.GetAllFieldsUntil(typeof(object), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        break;
                    }
            }

            foreach (var aField in aFields)
            {
                string aFieldName = aField.Name;
                if (iFieldNameAlterFunc != null)
                {
                    aFieldName = iFieldNameAlterFunc(aFieldName);
                }
                var aValue = aField.GetValue(iObj);
                if (aValue == null)
                {
                    //iData[aFieldName] = "";
                }
                else if (aValue is IJsonSerializable)
                {
                    aData[aFieldName] = ((IJsonSerializable)aValue).SerializeToJson();
                }
                else if (aValue.IsNumber() || aValue is string)
                {// || value is IList || value is IDictionary
                    aData[aFieldName] = new JsonData(aValue);
                }
                else if (aField.FieldType.IsEnum)
                {
                    aData[aFieldName] = aValue.ToString();
                }
                else if (aField.FieldType == typeof(bool))
                {
                    aData[aFieldName] = aValue.ToString();
                }
                else if (aValue is JsonData)
                {
                    aData[aFieldName] = (JsonData)aValue;
                }
                else if (aValue is IDictionary)
                {
                    IDictionary aDic = aValue as IDictionary;
                    if (aDic.Count > 0)
                    {
                        var aGenericData = new JsonData();

                        foreach (var aKey in aDic.Keys)
                        {
                            aGenericData[aKey.ConvertToJsonSafeString()] = ObjectToData(aDic[aKey], iSaveMode, iFieldNameAlterFunc);
                        }
                        aData[aFieldName] = aGenericData;
                    }
                    //else
                    //{
                    //    iData[aFieldName] = null;
                    //}
                }
                else if (aValue is IList)
                {
                    var aList = aValue as IList;
                    if (aList.Count > 0)
                    {
                        aData[aFieldName] = SaveDataToJson(aValue, iSaveMode, iFieldNameAlterFunc);
                    }
                }
                else if (aValue is IEnumerable)
                {
                    var aGenericData = new JsonData();
                    aData[aFieldName] = aGenericData;

                    var aEnumerable = aValue as IEnumerable;
                    foreach (var aItem in aEnumerable)
                    {
                        aGenericData.Add(ObjectToData(aItem, iSaveMode, iFieldNameAlterFunc));
                    }
                }
                else if (aField.FieldType.IsStructOrClass())
                {
                    aData[aFieldName] = SaveDataToJson(aValue, iSaveMode, iFieldNameAlterFunc);
                }
            }
            return aData;
        }
        /// <summary>
        /// Load data from Json
        /// </summary>
        /// <param name="iObj"></param>
        /// <param name="iData"></param>
        /// <returns></returns>
        static public object LoadDataFromJsonUnityVer(object iObj, JsonData iData)
            => LoadDataFromJson(iObj, iData, UCL.Core.JsonLib.JsonConvert.SaveMode.Unity, UCL.Core.UCL_StaticFunctions.FieldNameUnityVer);
        /// <summary>
        /// Load data from Json
        /// </summary>
        /// <param name="iObj"></param>
        /// <param name="iData"></param>
        /// <param name="iLayer"></param>
        /// <returns></returns>
        static public object LoadDataFromJson(object iObj, JsonData iData, SaveMode iSaveMode = SaveMode.Normal, System.Func<string, string> iFieldNameAlterFunc = null, int iLayer = 0)
        {
            if (iObj == null || iData == null || iLayer > MaxParsingLayer)
            {
                return null;
            }
            Type aType = iObj.GetType();
            if(iObj is IJsonSerializable) 
            {
                ((IJsonSerializable)iObj).DeserializeFromJson(iData);
                return iObj;
            }
            else if (iObj is IList && aType.IsGenericType)
            {
                IList aList = iObj as IList;
                Type aElementType = aType.GetGenericValueType();
                //Debug.LogError("IList aElementType:" + aElementType.Name);
                if (typeof(UCLI_TypeList).IsAssignableFrom(aElementType) && !typeof(UnityJsonSerializableObject).IsAssignableFrom(aElementType))
                {
                    //Debug.LogError("1 IList aElementType:" + aElementType.Name);
                    for (int i = 0; i < iData.Count; i++)
                    {
                        var aObj = JsonToObject(iData[i], iSaveMode, iFieldNameAlterFunc);
                        if (aObj != null) aList.Add(aObj);
                    }
                }
                else
                {
                    //Debug.LogError("2 IList aElementType:" + aElementType.Name);
                    for (int i = 0; i < iData.Count; i++)
                    {
                        var aObj = DataToObject(iData[i], aElementType, iSaveMode, iFieldNameAlterFunc);
                        if (aObj != null) aList.Add(aObj);
                    }
                }

                return iObj;
            }
            else if (iObj is IDictionary && aType.IsGenericType)
            {
                IDictionary aDic = iObj as IDictionary;
                Type aKeyType = aDic.GetType().GetGenericKeyType();
                Type aElementType = aDic.GetType().GetGenericValueType();
                IDictionary aJsonDic = iData.GetJsonDic() as IDictionary;
                if (aJsonDic != null)
                {
                    foreach (string aKey in aJsonDic.Keys)
                    {
                        var aObj = DataToObject(iData[aKey], aElementType, iSaveMode, iFieldNameAlterFunc);
                        if (aObj != null)
                        {
                            aDic[aKey.JsonSafeStringToObject(aKeyType)] = aObj;
                        }
                    }
                }
            }

            LoadFieldFromJson(iObj, iData, iSaveMode, iFieldNameAlterFunc, iLayer);
            return iObj;
        }

        static public object LoadFieldFromJsonUnityVer(object iObj, JsonData iData)
            => LoadFieldFromJson(iObj, iData, UCL.Core.JsonLib.JsonConvert.SaveMode.Unity, UCL.Core.UCL_StaticFunctions.FieldNameUnityVer);
        static public object LoadFieldFromJson(object iObj, JsonData iData, SaveMode iSaveMode = SaveMode.Normal, System.Func<string, string> iFieldNameAlterFunc = null, int iLayer = 0)
        {
            if (iObj == null || iData == null || iLayer > MaxParsingLayer)
            {
                return null;
            }
            Type aType = iObj.GetType();
            List<FieldInfo> aFields = null;
            switch (iSaveMode)
            {
                case SaveMode.Unity:
                    {
                        aFields = aType.GetAllFieldsUnityVer(typeof(object));
                        break;
                    }
                case SaveMode.Normal:
                default:
                    {
                        aFields = aType.GetAllFieldsUntil(typeof(object), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        break;
                    }
            }
            foreach (var aField in aFields)
            {
                string aFieldName = aField.Name;
                if (iFieldNameAlterFunc != null)
                {
                    aFieldName = iFieldNameAlterFunc(aFieldName);
                }
                if (iData.Contains(aFieldName))
                {
                    var aJsonData = iData[aFieldName];
                    if (aJsonData == null) continue;
                    var aFieldData = aJsonData.GetValue(aField.FieldType);
                    if (aFieldData == null)
                    {
                        try
                        {
                            aFieldData = aField.FieldType.CreateInstance();
                        }
                        catch (System.Exception e)
                        {
                            Debug.LogException(e);
                            continue;
                        }
                    }
                    if (aField.FieldType == typeof(string))
                    {
                        aField.SetValue(iObj, aFieldData);
                    }
                    else if (aField.FieldType == typeof(bool))
                    {
                        aField.SetValue(iObj, aJsonData.GetString() == "True");
                    }
                    else if (aField.FieldType.IsAssignableFrom(typeof(JsonData)))
                    {
                        aField.SetValue(iObj, aJsonData);
                    }
                    else if (aField.FieldType.IsAssignableFrom(typeof(UnityJsonSerializableObject)))
                    {
                        string aClassName = iData.GetString("ClassName");
                        Type aClassType = Type.GetType(aClassName);
                        JsonData aClassData = iData.GetString("ClassData");
                        UnityJsonSerializableObject aObj = aClassType.CreateInstance() as UnityJsonSerializableObject;//Activator.CreateInstance();
                        aObj.DeserializeFromJson(aClassData);
                        aField.SetValue(iObj, aObj);
                    }
                    //else if (typeof(UnityJsonSerializable).IsAssignableFrom(aField.FieldType))
                    //{
                    //    (aFieldData as UnityJsonSerializable).DeserializeFromJson(aJsonData);
                    //    aField.SetValue(iObj, aFieldData);
                    //}
                    else if (typeof(IJsonSerializable).IsAssignableFrom(aField.FieldType))
                    {
                        (aFieldData as IJsonSerializable).DeserializeFromJson(aJsonData);
                        aField.SetValue(iObj, aFieldData);
                    }
                    else if (aField.FieldType == typeof(JsonData))
                    {
                        aField.SetValue(iObj, aJsonData);
                    }
                    else if (aField.FieldType.IsEnum)
                    {
                        string aStr = aJsonData.GetString();
                        try
                        {
                            if (aJsonData.IsString && !string.IsNullOrEmpty(aStr))
                            {
                                Enum aEnum = Enum.Parse(aField.FieldType, aStr, true) as Enum;
                                if (aEnum != null)
                                {
                                    aField.SetValue(iObj, aEnum);
                                }
                            }
                        }
                        catch (System.Exception iE)
                        {
                            Debug.LogError("aField.FieldType:" + aField.FieldType.Name + ",aField.Name:" + aField.Name + ",aStr:" + aStr
                                + ",JsonData:" + iData.ToJsonBeautify()
                                + "\nSystem.Exception:" + iE);
                            Debug.LogException(iE);
                        }
                    }
                    else if (aFieldData is IList && aField.FieldType.IsGenericType)
                    {
                        //Debug.LogError("IList FieldName:" + aField.Name);
                        aField.SetValue(iObj, LoadDataFromJson(aFieldData, aJsonData, iSaveMode, iFieldNameAlterFunc, iLayer + 1));
                    }
                    else if (aFieldData is IDictionary && aField.FieldType.IsGenericType)
                    {
                        IDictionary aDic = aFieldData as IDictionary;
                        Type aKeyType = aDic.GetType().GetGenericKeyType();
                        Type aElementType = aDic.GetType().GetGenericValueType();
                        IDictionary aJsonDic = aJsonData.GetJsonDic() as IDictionary;
                        if (aJsonDic != null)
                        {
                            foreach (string aKey in aJsonDic.Keys)
                            {
                                var aObj = DataToObject(aJsonData[aKey], aElementType, iSaveMode, iFieldNameAlterFunc);
                                if (aObj != null)
                                {
                                    aDic[aKey.JsonSafeStringToObject(aKeyType)] = aObj;
                                }
                            }
                        }
                        aField.SetValue(iObj, aDic);
                    }
                    else if (aField.FieldType.IsStructOrClass())
                    {
                        var aResult = LoadDataFromJson(aFieldData, aJsonData, SaveMode.Unity, iFieldNameAlterFunc, iLayer + 1);
                        aField.SetValue(iObj, aResult);
                    }
                    else
                    {
                        aField.SetValue(iObj, aFieldData);
                    }
                }
            }
            return iObj;
        }
    }
}