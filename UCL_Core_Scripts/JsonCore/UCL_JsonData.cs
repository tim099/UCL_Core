using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UCL.Core.UI;
using UnityEngine;

namespace UCL.Core.JsonLib {
    public enum JsonType {
        None,
        /// <summary>
        /// Dic of JsonData
        /// </summary>
        Dictionary,
        List,
        String,
        Int,
        UInt,
        Long,
        ULong,
        Double,
        Boolean
    }
    public class JsonData : IList, IDictionary, UI.UCLI_FieldOnGUI//, UCLI_ShortName
    {
        #region Properties
        object m_Obj;
        JsonType m_Type;

        IList<JsonData> m_List;
        IDictionary<string, JsonData> m_Dic;
        IList<KeyValuePair<string, JsonData>> m_ObjectList;
        public JsonType JsonType => m_Type;
        public object Obj => m_Obj;
        public int Count { get {
                var aCollection = GetCollection();
                if (aCollection == null) return 0;
                return aCollection.Count;
            } }
        public bool IsArray => m_Type == JsonType.List;
        public bool IsBoolean => m_Type == JsonType.Boolean;
        public bool IsDouble => m_Type == JsonType.Double;
        public bool IsInt => m_Type == JsonType.Int;
        public bool IsLong => m_Type == JsonType.Long;
        public bool IsObject => m_Type == JsonType.Dictionary;
        public bool IsString => m_Type == JsonType.String;
        public IDictionary<string, JsonData> Dic => m_Dic;
        #endregion

        #region Interface
        //virtual public string GetShortName()
        //{
        //    switch (m_Type)
        //    {
        //        case JsonType.List:
        //            {
        //                return $"JsonData[{m_Type.ToString()}]";
        //            }
        //        case JsonType.Dictionary:
        //            {
        //                return $"JsonData[{m_Type.ToString()}]";
        //            }
        //        case JsonType.None:
        //            {
        //                return "JsonData(null)";
        //            }
        //        default:
        //            {
        //                if(m_Obj == null) return "JsonData(null)";
        //                return $"JsonData[{m_Type.ToString()}]({m_Obj.ToString()})";
        //            }
        //    }
        //    return "JsonData";
        //}
        virtual public object OnGUI(string iFieldName, UCL_ObjectDictionary iDataDic, UCL_GUILayout.DrawObjectParams iParams)
        {
            UCL_ObjectDictionary aSubDic = iDataDic.GetSubDic("JsonData");

            GUILayout.BeginHorizontal();
            bool aShow = true;
            var aType = m_Type;
            if (m_Type == JsonType.Dictionary || m_Type == JsonType.List)
            {
                aShow = UCL_GUILayout.Toggle(iDataDic.GetData(UCL_GUILayout.IsShowFieldKey, false));
                iDataDic.SetData(UCL_GUILayout.IsShowFieldKey, aShow);
            }
            System.Action aDrawFieldName = () => {
                //GUILayout.BeginHorizontal(GUILayout.ExpandWidth(false));
                //using(var aScope = new GUILayout.HorizontalScope(GUILayout.ExpandWidth(false)))
                {
                    var aNewType = UCL_GUILayout.PopupAuto(m_Type, iDataDic.GetSubDic(nameof(m_Type)), 10, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80)));
                    if (aNewType != m_Type)
                    {
                        //m_Type = aNewType;
                        iDataDic.Clear();
                        Init(aNewType);
                    }
                    GUILayout.Label(iFieldName, UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                }
                //GUILayout.EndHorizontal();
            };
            switch (aType)
            {
                case JsonType.List:
                    {
                        GUILayout.BeginVertical();
                        GUILayout.BeginHorizontal();
                        aDrawFieldName();
                        GUILayout.EndHorizontal();
                        if (aShow) UCL_GUILayout.DrawObjectData(m_List, aSubDic, "List", true);
                        GUILayout.EndVertical();
                        break;
                    }
                case JsonType.Dictionary:
                    {
                        GUILayout.BeginVertical();
                        GUILayout.BeginHorizontal();
                        aDrawFieldName();
                        GUILayout.EndHorizontal();
                        if (aShow) UCL_GUILayout.DrawObjectData(m_Dic, aSubDic, "Dic", true);
                        GUILayout.EndVertical();
                        break;
                    }
                case JsonType.None:
                    {
                        GUILayout.BeginHorizontal();
                        aDrawFieldName();
                        GUILayout.Label($": null", UCL_GUIStyle.LabelStyle);
                        GUILayout.EndHorizontal();
                        break;
                    }
                default:
                    {
                        GUILayout.BeginHorizontal();
                        aDrawFieldName();
                        //GUILayout.Label(iFieldName, UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                        m_Obj = UCL_GUILayout.DrawObjectData(m_Obj, aSubDic, m_Type.ToString(), true);
                        GUILayout.EndHorizontal();
                        break;
                    }
            }

            GUILayout.EndHorizontal();

            return this;
        }
        #endregion
        /// <summary>
        /// Parse Json string to JsonData
        /// </summary>
        /// <param name="iStr"></param>
        /// <returns></returns>
        static public JsonData ParseJson(string iStr) {
            using(var aParser = new JsonParser(iStr)) {
                return new JsonData(aParser.Parse());
            }
        }
        static public JsonData ParseJson(StringReader iStringReader)
        {
            using (var aParser = new JsonParser(iStringReader))
            {
                return new JsonData(aParser.Parse());
            }
        }
        #region Constructors
        public JsonData() {
            m_Type = JsonType.None;
        }
        public void Init(JsonType iJsonType)
        {
            m_Type = iJsonType;
            m_Obj = null;
            m_Dic = null;
            m_List = null;
            m_ObjectList = null;
            switch (m_Type)
            {
                case JsonType.Dictionary:
                    {
                        Init(new Dictionary<string, object>()); 
                        break;
                    }
                case JsonType.List:
                    {
                        Init(new List<object>()); 
                        break;
                    }
                case JsonType.String:
                    {
                        m_Obj = string.Empty;
                        break;
                    }
                case JsonType.Int:
                    {
                        m_Obj = (int)0; 
                        break;
                    }
                case JsonType.UInt:
                    {
                        m_Obj = (uint)0;
                        break;
                    }
                case JsonType.Long:
                    {
                        m_Obj = (long)0;
                        break;
                    }
                case JsonType.ULong:
                    {
                        m_Obj = (ulong)0;
                        break;
                    }
                case JsonType.Double:
                    {
                        m_Obj = (double)0;
                        break;
                    }
                case JsonType.Boolean:
                    {
                        m_Obj = false;
                        break;
                    }
            }
        }
        public void Init(object iObj)
        {
            if (iObj == null)
            {
                m_Type = JsonType.None;
                return;
            }

            if (iObj is IList aList)
            {
                m_Type = JsonType.List;
                m_List = new List<JsonData>();
                foreach (object item in aList)
                {
                    m_List.Add(ToJsonData(item));
                }
            }
            else if (iObj is IDictionary)
            {
                m_Type = JsonType.Dictionary;
                Dictionary<string, object> dict = iObj as Dictionary<string, object>;
                m_Dic = new Dictionary<string, JsonData>(dict.Count);
                m_ObjectList = new List<KeyValuePair<string, JsonData>>(dict.Count);

                KeyValuePair<string, JsonData> entry;
                JsonData value;
                foreach (KeyValuePair<string, object> item in dict)
                {
                    value = ToJsonData(item.Value);
                    entry = new KeyValuePair<string, JsonData>(item.Key, value);
                    m_Dic.Add(entry);
                    m_ObjectList.Add(entry);
                }
            }
            else
            {
                if (iObj is bool)
                {
                    m_Type = JsonType.Boolean;
                }
                else if (iObj is double)
                {
                    m_Type = JsonType.Double;
                }
                else if (iObj is float)
                {
                    m_Type = JsonType.Double;
                    iObj = (double)(float)iObj;
                }
                else if (iObj is int)
                {
                    m_Type = JsonType.Int;
                }
                else if (iObj is uint)
                {
                    m_Type = JsonType.UInt;
                }
                else if (iObj is long)
                {
                    m_Type = JsonType.Long;
                }
                else if (iObj is ulong)
                {
                    m_Type = JsonType.ULong;
                }
                else if (iObj is string)
                {
                    m_Type = JsonType.String;
                }
                else
                {
                    m_Type = JsonType.Dictionary;
                }
                m_Obj = iObj;
            }
        }
        public JsonData(object iObj) {
            Init(iObj);
        }
        public JsonData(bool boolean) {
            m_Type = JsonType.Boolean;
            m_Obj = boolean;
        }
        public JsonData(double number) {
            m_Type = JsonType.Double;
            m_Obj = number;
        }
        public JsonData(float number) {
            m_Type = JsonType.Double;
            m_Obj = (double)number;
        }
        public JsonData(int number) {
            m_Type = JsonType.Int;
            m_Obj = number;
        }
        public JsonData(uint number)
        {
            m_Type = JsonType.UInt;
            m_Obj = number;
        }
        public JsonData(long number) {
            m_Type = JsonType.Long;
            m_Obj = number;
        }
        public JsonData(ulong number)
        {
            m_Type = JsonType.ULong;
            m_Obj = number;
        }
        public JsonData(string str) {
            m_Type = JsonType.String;
            m_Obj = str;
        }

        private object ToObject(object iObj) {
            if(iObj is JsonData) {
                JsonData aData = iObj as JsonData;
                switch (aData.m_Type)
                {
                    case JsonType.Boolean:
                    case JsonType.Double:
                    case JsonType.Int:
                    case JsonType.UInt:
                    case JsonType.Long:
                    case JsonType.ULong:
                    case JsonType.String:
                        return aData.m_Obj;
                    case JsonType.List:
                        List<object> list = new List<object>();
                        foreach (JsonData item in aData.m_List)
                        {
                            list.Add(ToObject(item));
                        }
                        return list;
                    case JsonType.Dictionary:
                        Dictionary<string, object> aDict = new Dictionary<string, object>();
                        if (aData.m_Dic != null)
                        {
                            foreach (KeyValuePair<string, JsonData> aItem in aData.m_Dic)
                            {
                                aDict[aItem.Key] = ToObject(aItem.Value);
                            }
                        }
                        return aDict;
                }
                return null;
            } else {
                return iObj;
            }
        }
        #endregion
        public JsonData ToArray() {
            m_Type = JsonType.List;
            m_List = new List<JsonData>();
            return this;
        }
        public object GetObj() {
            return ToObject(this);
        }
        public object GetValue(Type iType) {
            if(iType == typeof(string)) {
                return GetString();
            }
            if(iType == typeof(float)) {
                return GetFloat();
            }
            if(iType == typeof(double)) {
                return GetDouble();
            }
            if(iType == typeof(int)) {
                return GetInt();
            }
            if(iType == typeof(uint))
            {
                return GetUInt();
            }
            if(iType == typeof(long)) {
                return GetLong();
            }
            if(iType == typeof(ulong))
            {
                return GetULong();
            }
            return null;
        }
        public string GetString()
        {
            if (m_Type == JsonType.String) return m_Obj as string;
            return string.Empty;
        }
        public string GetStringWithDefaultValue(string iDefaultVal)
        {
            if (m_Type == JsonType.String) return m_Obj as string;
            return iDefaultVal;
        }
        public string GetString(string iKey) {
            return GetString(iKey, string.Empty);
        }
        public string GetString(string iKey, string iDefaultVal) {
            var aVal = Get(iKey);
            if (aVal == null) return iDefaultVal;
            if(aVal == this) return iDefaultVal;
            if(aVal.m_Type == JsonType.String) return aVal.GetString();
            return iDefaultVal;
        }

        public float GetFloat(float iDefaultVal = 0) {
            if(m_Type == JsonType.Double) return (float) (double)m_Obj;
            if (m_Type == JsonType.Int) return (float) (int)m_Obj;
            if (m_Type == JsonType.UInt) return (float)(uint)m_Obj;
            if (m_Type == JsonType.Long) return (float) (long)m_Obj;
            if (m_Type == JsonType.ULong) return (float)(ulong)m_Obj;
            return iDefaultVal;
        }
        public float GetFloat(string iKey, float iDefaultVal = 0)
        {
            var aVal = Get(iKey);
            if (aVal == this) return iDefaultVal;

            if (aVal.m_Type == JsonType.Double) return (float)(double)aVal;
            if (aVal.m_Type == JsonType.Int) return (float)(int)aVal;
            if (aVal.m_Type == JsonType.UInt) return (float)(uint)aVal;
            if (aVal.m_Type == JsonType.Long) return (float)(long)aVal;
            if (aVal.m_Type == JsonType.ULong) return (float)(ulong)aVal;
            return iDefaultVal;
        }
        public double GetDouble(double iDefaultVal = 0) {
            if(m_Type == JsonType.Double) return (double)m_Obj;
            if (m_Type == JsonType.Int) return (double)(int)m_Obj;
            if (m_Type == JsonType.UInt) return (double)(uint)m_Obj;
            if (m_Type == JsonType.Long) return (double)(long)m_Obj;
            if (m_Type == JsonType.ULong) return (double)(ulong)m_Obj;
            return iDefaultVal;
        }
        public double GetDouble(string iKey, double iDefaultVal = 0)
        {
            var aVal = Get(iKey);
            if (aVal == this) return iDefaultVal;

            if (aVal.m_Type == JsonType.Double) return (double)aVal;
            if (aVal.m_Type == JsonType.Int) return (double)(int)aVal;
            if (aVal.m_Type == JsonType.UInt) return (double)(uint)aVal;
            if (aVal.m_Type == JsonType.Long) return (double)(long)aVal;
            if (aVal.m_Type == JsonType.ULong) return (double)(ulong)aVal;
            return iDefaultVal;
        }
        public T GetEnum<T>(string iKey, T iDefaultVal = default) where T : Enum
        {
            var aVal = Get(iKey);
            if (aVal == this) return iDefaultVal;
            if (aVal.m_Type == JsonType.String)
            {
                return (T)Enum.Parse(typeof(T), aVal.GetString());
            }
            return iDefaultVal;
        }
        public int GetInt(string iKey, int iDefaultVal = 0) {
            var aVal = Get(iKey);
            if(aVal == this) return iDefaultVal;
            return aVal.GetInt(iDefaultVal);
        }
        // 區塊職責：數值取值器 (Getter)
        // 物理意義：從 JsonData 中嘗試提取目標類型的數值。
        // 數值影響：不直接影響模擬，但影響資料讀取的正確性。若類型不匹配但數值範圍相容，則進行強制轉型。

        /// <summary>
        /// 取得 int 數值。支援從 Int, UInt, Long, ULong 自動轉型。
        /// </summary>
        public int GetInt(int iDefaultVal = 0) {
            if(m_Type == JsonType.Int) return (int)m_Obj;  
            if(m_Type == JsonType.UInt) return (int)(uint)m_Obj;
            if(m_Type == JsonType.Long) return (int)(long)m_Obj;
            if(m_Type == JsonType.ULong) return (int)(ulong)m_Obj;
            return iDefaultVal;
        }
        /// <summary>
        /// 取得 uint 數值。支援從 UInt, Int, Long, ULong 自動轉型。
        /// 解決了 Unix Timestamp (如 1734834037) 被解析器判定為 int 時，無法以 uint 讀取的問題。
        /// </summary>
        public uint GetUInt(uint iDefaultVal = 0)
        {
            if (m_Type == JsonType.UInt) return (uint)m_Obj;
            if (m_Type == JsonType.Int) return (uint)(int)m_Obj;
            if (m_Type == JsonType.Long) return (uint)(long)m_Obj;
            if (m_Type == JsonType.ULong) return (uint)(ulong)m_Obj;
            return iDefaultVal;
        }
        /// <summary>
        /// 取得 long 數值。支援從 Long, Int, UInt, ULong 自動轉型。
        /// </summary>
        public long GetLong(long iDefaultVal = 0) {
            if(m_Type == JsonType.Long) return (long)m_Obj;
            if(m_Type == JsonType.Int) return (long)(int)m_Obj;
            if(m_Type == JsonType.UInt) return (long)(uint)m_Obj;
            if(m_Type == JsonType.ULong) return (long)(ulong)m_Obj;
            return iDefaultVal;
        }
        /// <summary>
        /// 取得 ulong 數值。支援從 ULong, Long, Int, UInt 自動轉型。
        /// </summary>
        public ulong GetULong(ulong iDefaultVal = 0)
        {
            if (m_Type == JsonType.ULong) return (ulong)m_Obj;
            if (m_Type == JsonType.Long) return (ulong)(long)m_Obj;
            if (m_Type == JsonType.Int) return (ulong)(int)m_Obj;
            if (m_Type == JsonType.UInt) return (ulong)(uint)m_Obj;
            return iDefaultVal;
        }
        public bool GetBool(string iKey, bool iDefaultVal = false)
        {
            var aVal = Get(iKey);
            if (aVal == this) return iDefaultVal;
            if (aVal.m_Type == JsonType.Boolean)
            {
                return (bool)aVal;
            }
            return iDefaultVal;
        }
        public bool GetBool(bool iDefaultVal = false)
        {
            if (m_Type == JsonType.Boolean) return (bool)m_Obj;
            return iDefaultVal;
        }
        public IDictionary<string, JsonData> GetJsonDic()
        {
            if (m_Dic == null)
            {
                return null;
            }
            return m_Dic;
        }
        public Dictionary<string, object> GetDic() {
            if(m_Dic == null) {
                return null;
            }
            var aDic = new Dictionary<string, object>();
            foreach(var aObj in m_Dic) {
                aDic.Add(aObj.Key, aObj.Value.GetObj());
            }
            return aDic;
        }
        /// <summary>
        /// Get IJsonSerializable from JsonData
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="iKey"></param>
        /// <returns></returns>
        public T Get<T>(string iKey) where T : IJsonSerializable, new()
        {
            return Get(iKey, new T());
        }
        /// <summary>
        /// Get IJsonSerializable from JsonData
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="iKey"></param>
        /// <param name="iData"></param>
        /// <returns></returns>
        public T Get<T>(string iKey, T iData) where T : IJsonSerializable
        {
            GetIDic();
            if (m_Dic.ContainsKey(iKey))
            {
                iData.DeserializeFromJson(this[iKey]);
            }
            return iData;
        }
        public JsonData Get(string iKey) {
            GetIDic();
            if(!m_Dic.ContainsKey(iKey)) {
                return this;
            }
            return this[iKey];
        }
        #region Conversions
        public static implicit operator JsonData(bool data) { return new JsonData(data); }
        public static implicit operator JsonData(double data) { return new JsonData(data); }
        public static implicit operator JsonData(float data) { return new JsonData((double)data); }
        public static implicit operator JsonData(int data) { return new JsonData(data); }
        public static implicit operator JsonData(uint data) { return new JsonData(data); }
        public static implicit operator JsonData(long data) { return new JsonData(data); }
        public static implicit operator JsonData(ulong data) { return new JsonData(data); }

        public static implicit operator JsonData(string data) { return new JsonData(data); }
        public static implicit operator JsonData(JsonSerializable data){ return data.SerializeToJson(); }
        public static implicit operator JsonData(UnityJsonSerializable data) { return data.SerializeToJson(); }
        //explicit
        public static implicit operator bool(JsonData data) {
            if(data.m_Type != JsonType.Boolean) throw new InvalidCastException($"JsonData doesn't hold a bool,Type:{data.m_Type}");
            return (bool)data.m_Obj;
        }
        public static implicit operator double(JsonData data) {
            if(data.m_Type != JsonType.Double) throw new InvalidCastException($"JsonData doesn't hold a double,Type:{data.m_Type}");
            return (double)data.m_Obj;
        }
        public static implicit operator float(JsonData data) {
            if(data.m_Type != JsonType.Double) throw new InvalidCastException($"JsonData doesn't hold a float,Type:{data.m_Type}");
            return (float) (double)data.m_Obj;
        }
        public static implicit operator int(JsonData data) {
            if(data.m_Type != JsonType.Int) throw new InvalidCastException($"JsonData doesn't hold an int,Type:{data.m_Type}");
            return (int)data.m_Obj;
        }
        public static implicit operator uint(JsonData data)
        {
            if (data.m_Type != JsonType.UInt) throw new InvalidCastException($"JsonData doesn't hold an uint,Type:{data.m_Type}");
            return (uint)data.m_Obj;
        }
        public static implicit operator long(JsonData data) {
            if(data.m_Type != JsonType.Long) throw new InvalidCastException($"JsonData doesn't hold an long,Type:{data.m_Type}");
            return (long)data.m_Obj;
        }
        public static implicit operator ulong(JsonData data)
        {
            if (data.m_Type != JsonType.ULong) throw new InvalidCastException($"JsonData doesn't hold an ulong,Type:{data.m_Type}");
            return (ulong)data.m_Obj;
        }
        //public static implicit operator string(JsonData data) {
        //    if(data.m_Type != JsonType.String) data.ToJson();
        //    return data.m_Obj as string;
        //}
        #endregion

        #region Interface
        object IDictionary.this[object key] {
            get {
                return GetIDic()[key];
            }
            set {
                string str = key as string;
                if(string.IsNullOrEmpty(str)) throw new ArgumentException("JsonData IDictionary[key], key has to be string or not null!!");
                this[str] = ToJsonData(value);
            }
        }
        object IList.this[int index] {
            get {
                return GetList()[index];
            }
            set {
                GetList();
                this[index] = ToJsonData(value);
            }
        }
        public ICollection<string> Keys => m_Dic.Keys;
        #endregion
        #region ToJson
        /// <summary>
        /// Convert to json string
        /// </summary>
        /// <returns></returns>
        public string ToJson() {
            StringBuilder builder = new StringBuilder();
            // 直接從 JsonData 樹序列化, 不再 ToObject 深拷成一整棵 Dictionary/List<object> 平行樹
            // (省一棵樹的 GC garbage + 一趟遍歷)。葉節點複用既有 SerializeValue, 容器沿用
            // m_Dic/m_List 列舉序(與 ToObject 同源)→ 輸出 byte-identical。
            SerializeJsonData(this, builder);
            return builder.ToString();
        }
        void SerializeValue(object value, StringBuilder builder) {
            IList list;
            IDictionary dic;

            if(value == null) {
                builder.Append("null");
            } else if(value is string) {
                SerializeString(value as string, builder);
            } else if(value is bool) {
                builder.Append((bool)value ? "true" : "false");
            } else if((list = value as IList) != null) {
                builder.Append('[');
                bool first = true;
                foreach(object obj in list) {
                    if(!first) builder.Append(',');
                    SerializeValue(obj, builder);
                    first = false;
                }

                builder.Append(']');
            } else if((dic = value as IDictionary) != null) {
                bool first = true;
                builder.Append('{');
                foreach(object obj in dic.Keys) {
                    if(!first) builder.Append(',');
                    SerializeString(obj.ToString(), builder);
                    builder.Append(':');
                    SerializeValue(dic[obj], builder);
                    first = false;
                }
                builder.Append('}');
            } else if(value is char) {
                SerializeString(new string((char)value, 1), builder);
            } else if(value is float) {
                builder.Append(((float)value).ToString("R", CultureInfo.InvariantCulture));
            } else if(value is int
                || value is uint
                || value is long
                || value is sbyte
                || value is byte
                || value is short
                || value is ushort
                || value is ulong) {
                builder.Append(value);
            } else if(value is double || value is decimal) {
                builder.Append(Convert.ToDouble(value).ToString("R", CultureInfo.InvariantCulture));
            } else {
                SerializeString(value.ToString(), builder);
            }
        }

        // 區塊職責：直接從 JsonData 樹序列化（A1 優化，取代 SerializeValue(ToObject(this))）
        // 物理意義：容器（List/Dictionary）直接遞迴 m_List/m_Dic，葉節點（scalar / None）
        //          delegate 給既有 SerializeValue，完全不建中間 Dictionary/List<object> 樹。
        // 數值影響：輸出 byte-identical —— 容器列舉序沿用 m_Dic/m_List（與 ToObject 同一來源），
        //          葉節點格式化沿用同一支 SerializeValue；差別僅少配置一整棵物件樹 + 少一趟遍歷。
        //          注意：Dictionary 刻意走 m_Dic（非 m_ObjectList）以維持與舊路徑相同的列舉序；
        //          若要改用 m_ObjectList 的 insertion-order，那是「行為改變」須另立 task 單獨驗證。
        void SerializeJsonData(JsonData iData, StringBuilder builder) {
            switch (iData.m_Type) {
                case JsonType.None:
                    builder.Append("null");
                    break;
                case JsonType.List: {
                    builder.Append('[');
                    bool first = true;
                    if (iData.m_List != null) {
                        foreach (JsonData item in iData.m_List) {
                            if (!first) builder.Append(',');
                            if (item == null) builder.Append("null");
                            else SerializeJsonData(item, builder);
                            first = false;
                        }
                    }
                    builder.Append(']');
                    break;
                }
                case JsonType.Dictionary: {
                    builder.Append('{');
                    bool first = true;
                    if (iData.m_Dic != null) {
                        foreach (KeyValuePair<string, JsonData> kv in iData.m_Dic) {
                            if (!first) builder.Append(',');
                            SerializeString(kv.Key, builder);
                            builder.Append(':');
                            if (kv.Value == null) builder.Append("null");
                            else SerializeJsonData(kv.Value, builder);
                            first = false;
                        }
                    }
                    builder.Append('}');
                    break;
                }
                default:
                    // scalar：m_Obj 已是 boxed primitive，直接複用既有葉節點序列化
                    SerializeValue(iData.m_Obj, builder);
                    break;
            }
        }

        void SerializeString(string str, StringBuilder builder) {
            builder.Append('\"');

            // 直接迭代 string（本身即 IEnumerable<char>），省掉 ToCharArray() 每次多配一份等長 char[]。
            foreach(var c in str) {
                switch(c) {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\b':
                        builder.Append("\\b");
                        break;
                    case '\f':
                        builder.Append("\\f");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        int codepoint = Convert.ToInt32(c);
                        if((codepoint >= 32) && (codepoint <= 126)) {
                            builder.Append(c);
                        } else {
                            builder.Append("\\u");
                            builder.Append(codepoint.ToString("x4"));
                        }
                        break;
                }
            }

            builder.Append('\"');
        }
        /// <summary>
        /// Convert to beautify json string
        /// </summary>
        /// <returns></returns>
        public string ToJsonBeautify() {
            StringBuilder builder = new StringBuilder();
            // 同 ToJson: 直接序列化 JsonData 樹, 不經 ToObject 深拷。
            SerializeJsonDataBeautify(this, builder, 0);
            return builder.ToString();
        }
        void SerializeValueBeautify(object value, StringBuilder builder, int layer) {
            IList list;
            IDictionary dic;
            string layer_str = new string('\t', layer);
            if(value == null) {
                builder.Append("null");
            } else if(value is string) {
                SerializeString(value as string, builder);
            } else if(value is bool) {
                builder.Append((bool)value ? "true" : "false");
            } else if((list = value as IList) != null) {
                builder.Append(System.Environment.NewLine);
                builder.Append(layer_str);
                builder.Append('[');
                builder.Append(System.Environment.NewLine);
                bool first = true;
                foreach(object obj in list) {
                    if(!first) {
                        builder.Append(',');
                        builder.Append(System.Environment.NewLine);
                    }
                    builder.Append(layer_str);
                    builder.Append('\t');
                    SerializeValueBeautify(obj, builder, layer + 1);
                    first = false;
                }
                builder.Append(System.Environment.NewLine);
                builder.Append(layer_str);
                builder.Append(']');
                //builder.Append(System.Environment.NewLine);
            } else if((dic = value as IDictionary) != null) {
                bool first = true;
                builder.Append('{');
                builder.Append(System.Environment.NewLine);
                foreach(object obj in dic.Keys) {
                    if(!first) {
                        builder.Append(',');
                        builder.Append(System.Environment.NewLine);
                    }
                    builder.Append(layer_str);
                    builder.Append('\t');
                    SerializeString(obj.ToString(), builder);
                    builder.Append(':');
                    SerializeValueBeautify(dic[obj], builder, layer + 1);
                    first = false;
                }
                builder.Append(System.Environment.NewLine);
                builder.Append(layer_str);
                builder.Append('}');
            } else if(value is char) {
                SerializeString(new string((char)value, 1), builder);
            } else if(value is float) {
                builder.Append(((float)value).ToString("R", CultureInfo.InvariantCulture));
            } else if(value is int
                || value is uint
                || value is long
                || value is sbyte
                || value is byte
                || value is short
                || value is ushort
                || value is ulong) {
                builder.Append(value);
            } else if(value is double || value is decimal) {
                builder.Append(Convert.ToDouble(value).ToString("R", CultureInfo.InvariantCulture));
            } else {
                SerializeString(value.ToString(), builder);
            }
        }

        // Beautify 版直接序列化（A1）：結構鏡射 SerializeValueBeautify 的縮排/換行格式，
        // 容器直走 m_List/m_Dic、葉節點 delegate SerializeValueBeautify，輸出 byte-identical。
        void SerializeJsonDataBeautify(JsonData iData, StringBuilder builder, int layer) {
            switch (iData.m_Type) {
                case JsonType.None:
                    builder.Append("null");
                    break;
                case JsonType.List: {
                    string layer_str = new string('\t', layer);
                    builder.Append(System.Environment.NewLine);
                    builder.Append(layer_str);
                    builder.Append('[');
                    builder.Append(System.Environment.NewLine);
                    bool first = true;
                    if (iData.m_List != null) {
                        foreach (JsonData item in iData.m_List) {
                            if (!first) {
                                builder.Append(',');
                                builder.Append(System.Environment.NewLine);
                            }
                            builder.Append(layer_str);
                            builder.Append('\t');
                            if (item == null) builder.Append("null");
                            else SerializeJsonDataBeautify(item, builder, layer + 1);
                            first = false;
                        }
                    }
                    builder.Append(System.Environment.NewLine);
                    builder.Append(layer_str);
                    builder.Append(']');
                    break;
                }
                case JsonType.Dictionary: {
                    string layer_str = new string('\t', layer);
                    builder.Append('{');
                    builder.Append(System.Environment.NewLine);
                    bool first = true;
                    if (iData.m_Dic != null) {
                        foreach (KeyValuePair<string, JsonData> kv in iData.m_Dic) {
                            if (!first) {
                                builder.Append(',');
                                builder.Append(System.Environment.NewLine);
                            }
                            builder.Append(layer_str);
                            builder.Append('\t');
                            SerializeString(kv.Key, builder);
                            builder.Append(':');
                            if (kv.Value == null) builder.Append("null");
                            else SerializeJsonDataBeautify(kv.Value, builder, layer + 1);
                            first = false;
                        }
                    }
                    builder.Append(System.Environment.NewLine);
                    builder.Append(layer_str);
                    builder.Append('}');
                    break;
                }
                default:
                    SerializeValueBeautify(iData.m_Obj, builder, layer);
                    break;
            }
        }
        #endregion

        #region Public Indexers
        public JsonData this[string iPropName] {
            get {
                GetIDic();
                if (!m_Dic.ContainsKey(iPropName))
                {
                    Debug.LogError("JsonData !m_Dic.ContainsKey:" + iPropName);
                    return null;
                }
                return m_Dic[iPropName];
            }
            set {
                GetIDic();
                KeyValuePair<string, JsonData> entry = new KeyValuePair<string, JsonData>(iPropName, value);
                if(m_Dic.ContainsKey(iPropName)) {
                    for(int i = 0; i < m_ObjectList.Count; i++) {
                        if(m_ObjectList[i].Key == iPropName) {
                            m_ObjectList[i] = entry;
                            break;
                        }
                    }
                } else {
                    m_ObjectList.Add(entry);
                }
                m_Dic[iPropName] = value;
            }
        }

        public JsonData this[int index] {
            get {
                GetCollection();
                if(m_Type == JsonType.List) return m_List[index];
                return m_ObjectList[index].Value;
            }
            set {
                GetCollection();
                if(m_Type == JsonType.List) {
                    m_List[index] = value;
                } else {
                    var key = m_ObjectList[index].Key;
                    m_ObjectList[index] = new KeyValuePair<string, JsonData>(key, value);
                    m_Dic[key] = value;
                }
            }
        }
        #endregion

        #region Public Methods

        public int Add(object value) {
            return GetList().Add(ToJsonData(value));
        }

        public void Clear() {
            if(IsObject) {
                ((IDictionary)this).Clear();
                return;
            }
            if(IsArray) {
                ((IList)this).Clear();
                return;
            }
        }

        public bool Contains(object key) {
            if(m_Type != JsonType.Dictionary && m_Type != JsonType.None) return false;
            return GetIDic().Contains(key);
        }
        public void Remove(object key)
        {
            if (m_Type != JsonType.Dictionary && m_Type != JsonType.None) return;
            GetIDic().Remove(key);
        }
        public IDictionaryEnumerator Enumerator() {
            return GetIDic().GetEnumerator();
        }

        public bool Equals(JsonData data) {
            if(data == null) return false;
            if(data.m_Type != m_Type) return false;

            switch(m_Type) {
                case JsonType.None: return true;
                case JsonType.Dictionary: return m_Dic.Equals(data.m_Dic);
                case JsonType.List: return m_List.Equals(data.m_List);

                case JsonType.String:
                case JsonType.Int:
                case JsonType.Long:
                case JsonType.ULong:
                case JsonType.Double:
                case JsonType.Boolean:
                    return m_Obj.Equals(data.m_Obj);
            }
            return false;
        }

        public override string ToString() {
            switch(m_Type) {
                case JsonType.Boolean:
                case JsonType.Double:
                case JsonType.Int:
                case JsonType.Long:
                case JsonType.ULong:
                    return m_Obj.ToString();

                case JsonType.List:
                    return "Array";
                case JsonType.Dictionary:
                    return "Object";

                case JsonType.String:
                    return m_Obj as string;
            }

            return "";
        }

        public static bool IsNull(JsonData jd) {
            if(jd == null || jd.m_Type == JsonType.None)
                return true;
            return false;
        }
        #endregion

        #region Private Methods
        private ICollection GetCollection() {
            if(m_Type == JsonType.List) return (ICollection)m_List;
            if(m_Type == JsonType.Dictionary) return (ICollection)m_Dic;
            // 區塊職責：None 型別回 null，不再走 GetIDic()
            // 物理意義：GetIDic() 對 None 會把 m_Type 改成 Dictionary + 配置容器（有副作用）。
            //          過去唯讀存取（Count / ICollection.*）碰到 None 會偷偷把它 mutate 成空 Dictionary，
            //          造成「觀察改變被觀察者」的 Heisenbug（debug 加一行讀 Count 就讓 None 變 Object）。
            // 數值影響：改回 null（None = 無容器）。Count getter 已對 null 回 0；需要初始化請顯式賦型
            //          （indexer set / Add / ToArray），不允許靠讀取隱式生 Dictionary。
            return null;//Not avaliable (含 None)
        }

        private IDictionary GetIDic() {
            if(m_Type == JsonType.Dictionary) return (IDictionary)m_Dic;
            if(m_Type != JsonType.None) {
                //if(m_List != null) {
                //    foreach(var item in m_List) {
                //        Debug.LogError("JsonData data:" + item.ToString());
                //    }
                //}
                throw new InvalidOperationException("JsonData already has type:" + m_Type.ToString() + ",Cant convert to dictionary!!");
            }
            m_Type = JsonType.Dictionary;
            m_Dic = new Dictionary<string, JsonData>();
            m_ObjectList = new List<KeyValuePair<string, JsonData>>();
            return (IDictionary)m_Dic;
        }

        private IList GetList() {
            if(m_Type == JsonType.List) return (IList)m_List;
            if(m_Type != JsonType.None) throw new InvalidOperationException("JsonData already has type:"+m_Type.ToString()
                + ",Cant convert to array!!");
            m_Type = JsonType.List;
            m_List = new List<JsonData>();
            return (IList)m_List;
        }

        private JsonData ToJsonData(object iObj) {
            if(iObj == null) return null;
            if(iObj is JsonData) return (JsonData)iObj;
            if(iObj is IJsonSerializable) return ((IJsonSerializable)iObj).SerializeToJson();
            
            return new JsonData(iObj);
        }

        #endregion

        #region ICollection Properties & Methods
        int ICollection.Count { get { return Count; } }
        bool ICollection.IsSynchronized { get { return GetCollection().IsSynchronized; } }
        object ICollection.SyncRoot { get { return GetCollection().SyncRoot; } }
        void ICollection.CopyTo(Array iArray, int iIndex) { GetCollection().CopyTo(iArray, iIndex); }
        #endregion

        #region IDictionary Properties & Methods
        bool IDictionary.IsFixedSize { get { return GetIDic().IsFixedSize; } }
        bool IDictionary.IsReadOnly { get { return GetIDic().IsReadOnly; } }
        ICollection IDictionary.Keys {
            get {
                GetIDic();
                IList<string> aKeys = new List<string>();
                foreach(KeyValuePair<string, JsonData> aEntry in m_ObjectList) {
                    aKeys.Add(aEntry.Key);
                }
                return (ICollection)aKeys;
            }
        }
        ICollection IDictionary.Values {
            get {
                GetIDic();
                IList<JsonData> aValues = new List<JsonData>();
                foreach(KeyValuePair<string, JsonData> aEntry in m_ObjectList) {
                    aValues.Add(aEntry.Value);
                }
                return (ICollection)aValues;
            }
        }
        void IDictionary.Add(object iKey, object iValue) {
            JsonData aData = ToJsonData(iValue);
            GetIDic().Add(iKey, aData);
            m_ObjectList.Add(new KeyValuePair<string, JsonData>((string)iKey, aData));   
        }
        void IDictionary.Clear() {
            GetIDic().Clear();
            m_ObjectList.Clear();
        }
        bool IDictionary.Contains(object iKey) {
            return GetIDic().Contains(iKey);
        }
        IDictionaryEnumerator IDictionary.GetEnumerator() {
            GetIDic();
            return new JsonDataEnumerator(m_ObjectList.GetEnumerator());
        }
        void IDictionary.Remove(object iKey) {
            GetIDic().Remove(iKey);
            for(int i = 0; i < m_ObjectList.Count; i++) {
                if(m_ObjectList[i].Key == (string)iKey) {
                    m_ObjectList.RemoveAt(i);
                    break;
                }
            }
        }
        #endregion

        #region IEnumerable Methods
        IEnumerator IEnumerable.GetEnumerator() {
            return GetCollection().GetEnumerator();
        }
        #endregion


        #region IList Properties & Methods
        bool IList.IsFixedSize { get { return GetList().IsFixedSize; } }
        bool IList.IsReadOnly { get { return GetList().IsReadOnly; } }
        int IList.Add(object value) { return Add(value); }
        void IList.Clear() {
            GetList().Clear();
        }
        bool IList.Contains(object value) {
            return GetList().Contains(value);
        }
        int IList.IndexOf(object value) {
            return GetList().IndexOf(value);
        }
        void IList.Insert(int index, object value) {
            GetList().Insert(index, value);
        }
        void IList.Remove(object value) {
            GetList().Remove(value);
        }
        void IList.RemoveAt(int index) {
            GetList().RemoveAt(index);
        }
        #endregion
    }

    internal class JsonDataEnumerator : IDictionaryEnumerator {
        IEnumerator<KeyValuePair<string, JsonData>> m_Enumerator;
        public JsonDataEnumerator(IEnumerator<KeyValuePair<string, JsonData>> enumerator) {
            m_Enumerator = enumerator;
        }
        public object Current { get { return Entry; } }
        public DictionaryEntry Entry { get { return new DictionaryEntry(m_Enumerator.Current.Key, m_Enumerator.Current.Value); } }
        public object Key { get { return m_Enumerator.Current.Key; } }
        public object Value { get { return m_Enumerator.Current.Value; } }
        public bool MoveNext() { return m_Enumerator.MoveNext(); }
        public void Reset() { m_Enumerator.Reset(); }
    }
}