using System;
using System.Collections;
using System.Collections.Generic;
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
        static public object LoadDataFromJson(object obj, JsonData data) {
            if(obj == null) return null;
            Type type = obj.GetType();
            var fields = type.GetAllFieldsUntil(typeof(object), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            foreach(var field in fields) {
                if(data.Contains(field.Name)) {
                    var f_data = data[field.Name];
                    if (f_data == null) continue;
                    var aFieldData = f_data.GetValue(field.FieldType);
                    if(aFieldData == null) {
                        aFieldData = Activator.CreateInstance(field.FieldType);
                    }
                    if(field.FieldType == typeof(string)) {
                        field.SetValue(obj, aFieldData);
                    }
                    else if(field.FieldType.IsEnum) {
                        Enum aEnum = Enum.Parse(field.FieldType, f_data, true) as Enum;
                        if (aEnum != null)
                        {
                            field.SetValue(obj, aEnum);
                        }
                    }
                    else if(field.FieldType.IsStructOrClass()) {
                        var result = LoadDataFromJson(aFieldData, f_data);
                        //Debug.LogWarning("result:" + result.UCL_ToString());
                        field.SetValue(obj, result);
                    } else {
                        field.SetValue(obj, aFieldData);
                    }
                }// else {
                    //Debug.LogError("LoadDataFromJson field.Name:" + field.Name + ",Not Exist!!");
                //}
            }
            return obj;
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
        /// <summary>
        /// Save iObj into iData
        /// </summary>
        /// <param name="iObj"></param>
        /// <param name="iData"></param>
        static public void SaveDataToJson(object iObj, JsonData iData) {
            Type type = iObj.GetType();
            //if (type != null)
            //{
            //    Debug.LogWarning("type:" + type.Name);
            //}
            var fields = type.GetAllFieldsUntil(typeof(object), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            foreach(var field in fields) {
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
                else if (value is IEnumerable)
                {
                    var aGenericData = new JsonData();
                    iData[field.Name] = aGenericData;
                    //var GenericType = type.GetGenericTypeDefinition();
                    //var TypeInfo = type.GetTypeInfo();
                    //var GenericTypeArguments = TypeInfo.GenericTypeArguments;
                    //var ContentType = GenericTypeArguments[0];

                    var aEnumerable = value as IEnumerable;
                    foreach (var aItem in aEnumerable)
                    {
                        //Debug.LogWarning("aItem.GetType().Name:" + aItem.GetType().Name);
                        var aData = new JsonData();
                        SaveDataToJson(aItem, aData);
                        aGenericData.Add(aData);
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