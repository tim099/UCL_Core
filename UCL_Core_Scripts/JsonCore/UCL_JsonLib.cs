using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace UCL.Core.JsonLib {
    public static class JsonConvert {
        const int MaxParsingLayer = 10;

        /// <summary>
        /// Convert Json string into object
        /// </summary>
        /// <param name="iJson"></param>
        /// <returns></returns>
        static public object JsonToObject(string iJson) {
            return JsonToObject(JsonData.ParseJson(iJson));
        }
        /// <summary>
        /// Convert JsonData into object
        /// </summary>
        /// <param name="iData"></param>
        /// <returns></returns>
        static public object JsonToObject(JsonData iData) {
            if(!iData.Contains("ClassName") || !iData.Contains("ClassData")) {
                Debug.LogError("!iData.Contains(ClassName) || !iData.Contains(ClassData)");
                return null;
            }
            string aClassName = iData["ClassName"];
            Type aClassType = Type.GetType(aClassName);
            JsonData aClassData = iData["ClassData"];
            object aObj = Activator.CreateInstance(aClassType);
            LoadDataFromJson(aObj, aClassData);
            return aObj;
            //return JsonUtility.FromJson(aClassData.ToJson(), aClassType);
        }
        /// <summary>
        /// Save object into JsonData(also save AssemblyQualifiedName so can convert back to object)
        /// </summary>
        /// <param name="iObj"></param>
        /// <returns></returns>
        static public JsonData ObjectToJson(object iObj) {
            JsonData aData = new JsonData();
            aData["ClassName"] = iObj.GetType().AssemblyQualifiedName;
            aData["ClassData"] = SaveDataToJson(iObj);
            return aData;
        }
        static public List<T> LoadListFromJson<T>(JsonData data) where T : new() {
            List<T> list = new List<T>();
            for(int i = 0; i < data.Count; i++) {
                list.Add(LoadDataFromJson<T>(data[i]));
            }
            return list;
        }
        static public T LoadDataFromJson<T>(JsonData iData) where T : new() {
            Type aType = typeof(T);
            var aValue = iData.GetValue(aType);
            if(aValue != null) return (T)aValue;
            return (T)LoadDataFromJson(new T(), iData);
        }
        static public object LoadDataFromJson(object iObj, JsonData iData, int iLayer = 0) {
            if(iObj == null || MaxParsingLayer > 10) return null;
            Type iType = iObj.GetType();
            if (iObj is IList && iType.IsGenericType)
            {
                IList aList = iObj as IList;
                Type aElementType = aList.GetType().GetGenericArguments().Single();
                for (int i = 0; i < iData.Count; i++)
                {
                    var aObj = DataToObject(iData[i], aElementType);
                    if (aObj != null) aList.Add(aObj);
                }
                return iObj;
            }

            var aFields = iType.GetAllFieldsUntil(typeof(object), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            foreach(var aField in aFields) {
                if(iData.Contains(aField.Name)) {
                    var aJsonData = iData[aField.Name];
                    if (aJsonData == null) continue;
                    var aFieldData = aJsonData.GetValue(aField.FieldType);
                    if(aFieldData == null) {
                        try
                        {
                            aFieldData = Activator.CreateInstance(aField.FieldType);
                        }
                        catch(System.Exception e)
                        {
                            Debug.LogException(e);
                            continue;
                        }
                    }
                    if(aField.FieldType == typeof(string)) {
                        aField.SetValue(iObj, aFieldData);
                    }
                    else if (aField.FieldType == typeof(bool))
                    {
                        aField.SetValue(iObj, aJsonData.GetString() == "True");
                    }
                    else if(aField.FieldType.IsEnum) {
                        try
                        {
                            Enum aEnum = Enum.Parse(aField.FieldType, aJsonData, true) as Enum;
                            if (aEnum != null)
                            {
                                aField.SetValue(iObj, aEnum);
                            }
                        }
                        catch(System.Exception e)
                        {
                            Debug.LogError("System.Exception:" + e);
                        }
                    }
                    else if (aFieldData is IList && aField.FieldType.IsGenericType)
                    {
                        IList aList = aFieldData as IList;
                        Type aElementType = aList.GetType().GetGenericArguments().Single();
                        for (int i = 0; i < aJsonData.Count; i++)
                        {
                            var aObj = DataToObject(aJsonData[i], aElementType);
                            if(aObj != null) aList.Add(aObj);
                        }
                        aField.SetValue(iObj, aList);
                    }
                    else if(aField.FieldType.IsStructOrClass()) {
                        var result = LoadDataFromJson(aFieldData, aJsonData, iLayer + 1);
                        //Debug.LogWarning("result:" + result.UCL_ToString());
                        aField.SetValue(iObj, result);
                    } 
                    else {
                        aField.SetValue(iObj, aFieldData);
                    }
                }// else {
                    //Debug.LogError("LoadDataFromJson field.Name:" + field.Name + ",Not Exist!!");
                //}
            }
            return iObj;
        }
        /// <summary>
        /// Convert data in iObj into JsonData
        /// </summary>
        /// <param name="iObj"></param>
        /// <returns></returns>
        static public JsonData SaveDataToJson(object iObj) {
            JsonData aData = new JsonData();
            SaveDataToJson(iObj, aData);
            return aData;
        }
        static object DataToObject(JsonData iData, Type iType)
        {
            if (iType.IsEnum)
            {
                string aStr = iData.GetString();
                if (!string.IsNullOrEmpty(aStr))
                {
                    return Enum.Parse(iType, aStr, true) as Enum;
                }
                else
                {
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
            else if (iType.IsStructOrClass())
            {
                object aObj = Activator.CreateInstance(iType);
                return LoadDataFromJson(aObj, iData);
            }


            return iData.GetObj();
        }
        static JsonData ObjectToData(object iObj)
        {
            if (iObj == null) return null;
            Type aType = iObj.GetType();
            if (aType.IsEnum)
            {
                return new JsonData(iObj.ToString());
            }
            else if (iObj.IsNumber() || iObj is string)
            {
                return new JsonData(iObj);
            }
            else if (aType.IsStructOrClass())
            {
                JsonData aData = new JsonData();
                SaveDataToJson(iObj, aData);
                return aData;
            }
            
            return new JsonData(iObj);
        }
        /// <summary>
        /// Save iObj into iData
        /// </summary>
        /// <param name="iObj"></param>
        /// <param name="iData"></param>
        static public void SaveDataToJson(object iObj, JsonData iData) {
            if (iObj is IList)
            {
                IList aList = iObj as IList;
                foreach (var aItem in aList)
                {
                    iData.Add(ObjectToData(aItem));
                }
                return;
            }
            Type aType = iObj.GetType();
            var aFields = aType.GetAllFieldsUntil(typeof(object), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            foreach (var aField in aFields) {
                if (aField.GetCustomAttribute<HideInInspector>() != null)
                {
                    continue;
                }
                var aValue = aField.GetValue(iObj);
                if (aValue == null)
                {
                    iData[aField.Name] = "";
                }
                else if (aValue.IsNumber() || aValue is string)
                {// || value is IList || value is IDictionary
                    iData[aField.Name] = new JsonData(aValue);
                }
                else if (aField.FieldType.IsEnum)
                {
                    iData[aField.Name] = aValue.ToString();
                }
                else if (aField.FieldType == typeof(bool))
                {
                    iData[aField.Name] = aValue.ToString();
                }
                else if (aValue is IEnumerable)
                {
                    var aGenericData = new JsonData();
                    iData[aField.Name] = aGenericData;

                    var aEnumerable = aValue as IEnumerable;
                    foreach (var aItem in aEnumerable)
                    {
                        aGenericData.Add(ObjectToData(aItem));
                    }
                }
                else if (aField.FieldType.IsStructOrClass())
                {
                    iData[aField.Name] = new JsonData();
                    SaveDataToJson(aValue, iData[aField.Name]);
                }
            }
        }
    }
}