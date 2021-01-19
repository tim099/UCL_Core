using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace UCL.Core.JsonLib {
    public static class JsonConvert {
        static public void LoadDataFromJson(object obj, JsonData data) {
            Type type = obj.GetType();
            var fields = type.GetAllFieldsUntil(typeof(object), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            foreach(var field in fields) {
                if(data.Contains(field.Name)) {
                    field.SetValue(obj, data[field.Name].GetObj());
                }
            }
        }
        static public void SaveDataToJson(object obj, JsonData data) {
            Type type = obj.GetType();
            var fields = type.GetAllFieldsUntil(typeof(object), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            foreach(var field in fields) {
                data[field.Name] = new JsonData(field.GetValue(obj));
            }
        }
    }
}