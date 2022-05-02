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
        /// Convert Json into object
        /// 將Json轉換成物件
        /// </summary>
        /// <param name="iJson"></param>
        /// <returns></returns>
        static public object JsonToObject(string iJson, SaveMode iSaveMode = SaveMode.Normal) {
            return JsonToObject(JsonData.ParseJson(iJson), iSaveMode);
        }
        /// <summary>
        /// Convert JsonData into object
        /// 將JsonData轉換成物件
        /// </summary>
        /// <param name="iData"></param>
        /// <returns></returns>
        static public object JsonToObject(JsonData iData, SaveMode iSaveMode = SaveMode.Normal) {
            if(!iData.Contains("ClassName") || !iData.Contains("ClassData")) {
                Debug.LogError("JsonToObject !iData.Contains(ClassName) || !iData.Contains(ClassData)");
                return null;
            }
            string aClassName = iData.GetString("ClassName");
            Type aClassType = Type.GetType(aClassName);
            JsonData aClassData = iData.GetString("ClassData");
            object aObj = aClassType.CreateInstance();//Activator.CreateInstance();
            LoadDataFromJson(aObj, aClassData, iSaveMode);
            return aObj;
            //return JsonUtility.FromJson(aClassData.ToJson(), aClassType);
        }
        /// <summary>
        /// Save object into JsonData(also save AssemblyQualifiedName so can convert back to object)
        /// </summary>
        /// <param name="iObj"></param>
        /// <returns></returns>
        static public JsonData ObjectToJson(object iObj, SaveMode iSaveMode = SaveMode.Normal) {
            JsonData aData = new JsonData();
            aData["ClassName"] = iObj.GetType().AssemblyQualifiedName;
            aData["ClassData"] = SaveDataToJson(iObj, iSaveMode);
            return aData;
        }
        /// <summary>
        /// Load List of <T> from Json
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="iData"></param>
        /// <returns></returns>
        static public List<T> LoadListFromJson<T>(JsonData iData, SaveMode iSaveMode = SaveMode.Normal) where T : new() {
            List<T> aList = new List<T>();
            for(int i = 0; i < iData.Count; i++) {
                aList.Add(LoadDataFromJson<T>(iData[i], iSaveMode));
            }
            return aList;
        }
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
        /// Convert data in iObj into JsonData
        /// </summary>
        /// <param name="iObj"></param>
        /// <param name="iFieldNameAlterFunc">Input is the original fieldname, and return the name you want to save as json key</param>
        /// <returns></returns>
        static public JsonData SaveDataToJson(object iObj, SaveMode iSaveMode = SaveMode.Normal, System.Func<string, string> iFieldNameAlterFunc = null)
        {
            JsonData aData = new JsonData();
            SaveDataToJson(iObj, aData, iSaveMode, iFieldNameAlterFunc);
            return aData;
        }
        /// <summary>
        /// Create Object using JsonData
        /// </summary>
        /// <param name="iData"></param>
        /// <param name="iType"></param>
        /// <returns></returns>
        static object DataToObject(JsonData iData, Type iType, SaveMode iSaveMode = SaveMode.Normal, System.Func<string, string> iFieldNameAlterFunc = null)
        {
            if(iType == typeof(JsonData))
            {
                return iData;
            }
            else if (typeof(UnityJsonSerializableObject).IsAssignableFrom(iType))
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
                JsonData aData = new JsonData();
                SaveDataToJson(iObj, aData, iSaveMode, iFieldNameAlterFunc);
                return aData;
            }

            return new JsonData(iObj);
        }
        /// <summary>
        /// Save iObj into iData
        /// </summary>
        /// <param name="iObj"></param>
        /// <param name="iData"></param>
        /// <param name="iFieldNameAlterFunc">Input is the original fieldname, and return the name you want to save as json key</param>
        static public void SaveDataToJson(object iObj, JsonData iData, SaveMode iSaveMode = SaveMode.Normal, System.Func<string, string> iFieldNameAlterFunc = null)
        {
            if (iObj is IList)
            {
                IList aList = iObj as IList;
                foreach (var aItem in aList)
                {
                    iData.Add(ObjectToData(aItem, iSaveMode, iFieldNameAlterFunc));
                }
                return;
            } else if(iObj is IDictionary)
            {
                IDictionary aDic = iObj as IDictionary;
                foreach (var aKey in aDic.Keys)
                {
                    iData[aKey.ConvertToJsonSafeString()] = ObjectToData(aDic[aKey], iSaveMode, iFieldNameAlterFunc);
                }

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
                var aValue = aField.GetValue(iObj);
                if (aValue == null)
                {
                    iData[aFieldName] = "";
                }
                else if(aValue is IJsonSerializable)
                {
                    iData[aFieldName] = ((IJsonSerializable)aValue).SerializeToJson();
                }
                else if (aValue.IsNumber() || aValue is string)
                {// || value is IList || value is IDictionary
                    iData[aFieldName] = new JsonData(aValue);
                }
                else if (aField.FieldType.IsEnum)
                {
                    iData[aFieldName] = aValue.ToString();
                }
                else if (aField.FieldType == typeof(bool))
                {
                    iData[aFieldName] = aValue.ToString();
                }
                else if(aValue is IDictionary)
                {
                    IDictionary aDic = aValue as IDictionary;
                    if (aDic.Count > 0)
                    {
                        var aGenericData = new JsonData();
                        iData[aFieldName] = aGenericData;

                        foreach (var aKey in aDic.Keys)
                        {
                            aGenericData[aKey.ConvertToJsonSafeString()] = ObjectToData(aDic[aKey], iSaveMode, iFieldNameAlterFunc);
                        }
                    }
                    else
                    {
                        iData[aFieldName] = null;
                    }
                }
                else if (aValue is IEnumerable)
                {
                    var aGenericData = new JsonData();
                    iData[aFieldName] = aGenericData;

                    var aEnumerable = aValue as IEnumerable;
                    foreach (var aItem in aEnumerable)
                    {
                        aGenericData.Add(ObjectToData(aItem, iSaveMode, iFieldNameAlterFunc));
                    }
                }
                else if (aField.FieldType.IsStructOrClass())
                {
                    iData[aFieldName] = new JsonData();
                    SaveDataToJson(aValue, iData[aFieldName], iSaveMode, iFieldNameAlterFunc);
                }
            }
        }
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
            Type iType = iObj.GetType();

            if (iObj is IList && iType.IsGenericType)
            {
                IList aList = iObj as IList;
                Type aElementType = aList.GetType().GetGenericArguments().Single();
                for (int i = 0; i < iData.Count; i++)
                {
                    var aObj = DataToObject(iData[i], aElementType, iSaveMode, iFieldNameAlterFunc);
                    if (aObj != null) aList.Add(aObj);
                }
                return iObj;
            }
            else if (iObj is IDictionary && iType.IsGenericType)
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
            List<FieldInfo> aFields = null;
            switch (iSaveMode)
            {
                case SaveMode.Unity:
                    {
                        aFields = iType.GetAllFieldsUnityVer(typeof(object));
                        break;
                    }
                case SaveMode.Normal:
                default:
                    {
                        aFields = iType.GetAllFieldsUntil(typeof(object), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
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
                    else if (aField.FieldType.IsAssignableFrom(typeof(UnityJsonSerializableObject)))
                    {
                        string aClassName = iData.GetString("ClassName");
                        Type aClassType = Type.GetType(aClassName);
                        JsonData aClassData = iData.GetString("ClassData");
                        UnityJsonSerializableObject aObj = aClassType.CreateInstance() as UnityJsonSerializableObject;//Activator.CreateInstance();
                        aObj.DeserializeFromJson(aClassData);
                        aField.SetValue(iObj, aObj);
                    }
                    else if (typeof(UnityJsonSerializable).IsAssignableFrom(aField.FieldType))
                    {
                        (aFieldData as UnityJsonSerializable).DeserializeFromJson(aJsonData);
                        aField.SetValue(iObj, aFieldData);
                    }
                    else if(aField.FieldType == typeof(JsonData))
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
                            Debug.LogError("aField.FieldType:"+ aField.FieldType.Name+ ",aField.Name:" + aField.Name + ",aStr:" + aStr + "\nSystem.Exception:" + iE);
                            Debug.LogException(iE);
                        }
                    }
                    else if (aFieldData is IList && aField.FieldType.IsGenericType)
                    {
                        IList aList = aFieldData as IList;
                        Type aElementType = aList.GetType().GetGenericArguments().Single();
                        for (int i = 0; i < aJsonData.Count; i++)
                        {
                            var aObj = DataToObject(aJsonData[i], aElementType, iSaveMode, iFieldNameAlterFunc);
                            if (aObj != null)
                            {
                                aList.Add(aObj);
                            }
                        }
                        aField.SetValue(iObj, aList);
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