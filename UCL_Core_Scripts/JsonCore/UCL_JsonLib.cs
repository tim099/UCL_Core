using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace UCL.Core.JsonLib {
    public static class JsonConvert {
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
            //if(type.IsSubclassOf(typeof(IJsonSerializable))) {
            //    var t = new T();
            //    ((IJsonSerializable)t).DeserializeFromJson(data);
            //    return t;
            //}
            return (T)LoadDataFromJson(new T(), data);
        }
        static public object LoadDataFromJson(object obj, JsonData data) {
            if(obj == null) return null;
            Type type = obj.GetType();
            var fields = type.GetAllFieldsUntil(typeof(object), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            foreach(var field in fields) {
                if(data.Contains(field.Name)) {
                    var f_data = data[field.Name];
                    if(field.FieldType == typeof(string)) {
                        field.SetValue(obj, f_data.GetValue(field.FieldType));
                    }
                    else if(field.FieldType.IsStructOrClass()) {
                        var f_val = field.GetValue(obj);
                        var result = LoadDataFromJson(f_val, f_data);
                        //Debug.LogWarning("result:" + result.UCL_ToString());
                        field.SetValue(obj, result);
                    } else {
                        field.SetValue(obj, f_data.GetValue(field.FieldType));
                    }
                }// else {
                    //Debug.LogError("LoadDataFromJson field.Name:" + field.Name + ",Not Exist!!");
                //}
            }
            return obj;
        }
        static public void SaveDataToJson(object obj, JsonData data) {
            Type type = obj.GetType();
            var fields = type.GetAllFieldsUntil(typeof(object), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            foreach(var field in fields) {
                var value = field.GetValue(obj);
                if(value == null) {
                    data[field.Name] = "";
                } else if(value.IsNumber() || value is string) {// || value is IList || value is IDictionary
                    data[field.Name] = new JsonData(value);
                }else if(field.FieldType.IsStructOrClass()) {
                    data[field.Name] = new JsonData();
                    SaveDataToJson(value, data[field.Name]);
                }
            }
        }
    }
}