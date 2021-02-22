using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace UCL.Core.JsonLib {
    public static class JsonConvert {
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
        /// Save object into JsonData
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
        static public T LoadDataFromJson<T>(JsonData data) where T : new() {
            Type type = typeof(T);
            var value = data.GetValue(type);
            if(value != null) return (T)value;
            return (T)LoadDataFromJson(new T(), data);
        }
        static public object LoadDataFromJson(object iObj, JsonData data) {
            if(iObj == null) return null;
            Type type = iObj.GetType();
            if (iObj is IList && type.IsGenericType)
            {
                IList aList = iObj as IList;
                Type aElementType = aList.GetType().GetGenericArguments().Single();
                for (int i = 0; i < data.Count; i++)
                {
                    var aObj = DataToObject(data[i], aElementType);
                    if (aObj != null) aList.Add(aObj);
                }
                return iObj;
            }

            var fields = type.GetAllFieldsUntil(typeof(object), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            foreach(var field in fields) {
                if(data.Contains(field.Name)) {
                    var f_data = data[field.Name];
                    if (f_data == null) continue;
                    var aFieldData = f_data.GetValue(field.FieldType);
                    if(aFieldData == null) {
                        try
                        {
                            aFieldData = Activator.CreateInstance(field.FieldType);
                        }
                        catch(System.Exception e)
                        {
                            Debug.LogError("field.Name:" + field.Name + "System.Exception:" + e);
                            continue;
                        }
                    }
                    if(field.FieldType == typeof(string)) {
                        field.SetValue(iObj, aFieldData);
                    }
                    else if (field.FieldType == typeof(bool))
                    {
                        field.SetValue(iObj, f_data.GetString() == "True");
                    }
                    else if(field.FieldType.IsEnum) {
                        try
                        {
                            Enum aEnum = Enum.Parse(field.FieldType, f_data, true) as Enum;
                            if (aEnum != null)
                            {
                                field.SetValue(iObj, aEnum);
                            }
                        }
                        catch(System.Exception e)
                        {
                            Debug.LogError("System.Exception:" + e);
                        }
                    }
                    else if (aFieldData is IList && field.FieldType.IsGenericType)
                    {
                        IList aList = aFieldData as IList;
                        Type aElementType = aList.GetType().GetGenericArguments().Single();
                        for (int i = 0; i < f_data.Count; i++)
                        {
                            var aObj = DataToObject(f_data[i], aElementType);
                            if(aObj != null) aList.Add(aObj);
                        }
                        field.SetValue(iObj, aList);
                    }
                    else if(field.FieldType.IsStructOrClass()) {
                        var result = LoadDataFromJson(aFieldData, f_data);
                        //Debug.LogWarning("result:" + result.UCL_ToString());
                        field.SetValue(iObj, result);
                    } else {
                        field.SetValue(iObj, aFieldData);
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
            Type type = iObj.GetType();
            var fields = type.GetAllFieldsUntil(typeof(object), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (iObj is IList)
            {
                IList aList = iObj as IList;
                foreach (var aItem in aList)
                {
                    iData.Add(ObjectToData(aItem));
                }
                return;
            }
            foreach (var field in fields) {
                var value = field.GetValue(iObj);
                if (value == null)
                {
                    iData[field.Name] = "";
                }
                else if (value.IsNumber() || value is string)
                {// || value is IList || value is IDictionary
                    iData[field.Name] = new JsonData(value);
                }
                else if (field.FieldType.IsEnum)
                {
                    iData[field.Name] = value.ToString();
                }
                else if (field.FieldType == typeof(bool))
                {
                    iData[field.Name] = value.ToString();
                }
                else if (value is IEnumerable)
                {
                    var aGenericData = new JsonData();
                    iData[field.Name] = aGenericData;

                    var aEnumerable = value as IEnumerable;
                    foreach (var aItem in aEnumerable)
                    {
                        aGenericData.Add(ObjectToData(aItem));
                    }
                }
                else if (field.FieldType.IsStructOrClass())
                {
                    iData[field.Name] = new JsonData();
                    SaveDataToJson(value, iData[field.Name]);
                }
            }
        }
    }
}