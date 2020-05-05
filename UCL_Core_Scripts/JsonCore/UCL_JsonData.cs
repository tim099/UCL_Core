using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace UCL.Core.JsonLib {
    public enum JsonType {
        None,
        Object,
        Array,
        String,
        Int,
        Long,
        Double,
        Boolean
    }
    public class JsonData : IList, IDictionary {
        public JsonData(object obj) {
            if(obj is IList) {
                JsonDataList((IList)obj);
            } else if(obj is IDictionary) {
                JsonDataDictionary((IDictionary)obj);
            } else {
                if(obj is bool) {
                    m_Type = JsonType.Boolean;
                } else if(obj is double) {
                    m_Type = JsonType.Double;
                } else if(obj is int) {
                    m_Type = JsonType.Int;
                } else if(obj is long) {
                    m_Type = JsonType.Long;
                } else if(obj is string) {
                    m_Type = JsonType.String;
                } else {
                    m_Type = JsonType.Object;
                }
                m_Obj = obj;
            } 
        }
        #region Fields

        private object m_Obj;
        private JsonType m_Type;

        private IList<JsonData> m_List;
        private IDictionary<string, JsonData> m_Dic;
        private IList<KeyValuePair<string, JsonData> > m_ObjectList;
        #endregion


        #region Properties
        public int Count { get { return GetCollection().Count; } }
        public bool IsArray { get { return m_Type == JsonType.Array; } }
        public bool IsBoolean { get { return m_Type == JsonType.Boolean; } }
        public bool IsDouble { get { return m_Type == JsonType.Double; } }
        public bool IsInt { get { return m_Type == JsonType.Int; } }
        public bool IsLong { get { return m_Type == JsonType.Long; } }
        public bool IsObject { get { return m_Type == JsonType.Object; } }
        public bool IsString { get { return m_Type == JsonType.String; } }
        #endregion


        #region Constructors
        public JsonData() {
            m_Type = JsonType.None;
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
        public JsonData(long number) {
            m_Type = JsonType.Long;
            m_Obj = number;
        }
        public JsonData(string str) {
            m_Type = JsonType.String;
            m_Obj = str;
        }

        private void JsonDataList(IList obj) {
            m_Type = JsonType.Array;
            List<object> lst = obj as List<object>;
            m_List = new List<JsonData>(lst.Count);
            foreach(object item in lst) {
                m_List.Add(ToJsonData(item));
            }
        }

        private void JsonDataDictionary(IDictionary obj) {
            m_Type = JsonType.Object;
            Dictionary<string, object> dict = obj as Dictionary<string, object>;
            m_Dic = new Dictionary<string, JsonData>(dict.Count);
            m_ObjectList = new List<KeyValuePair<string, JsonData>>(dict.Count);

            KeyValuePair<string, JsonData> entry;
            JsonData value;
            foreach(KeyValuePair<string, object> item in dict) {
                value = ToJsonData(item.Value);
                entry = new KeyValuePair<string, JsonData>(item.Key, value);
                m_Dic.Add(entry);
                m_ObjectList.Add(entry);
            }
        }

        private object ToObject(object obj) {
            if(obj is JsonData) {
                JsonData data = obj as JsonData;
                switch(data.m_Type) {
                    case JsonType.Boolean: return data.m_Obj;
                    case JsonType.Double: return data.m_Obj;
                    case JsonType.Int: return data.m_Obj;
                    case JsonType.Long: return data.m_Obj;
                    case JsonType.String: return data.m_Obj;
                    case JsonType.Array:
                        List<object> list = new List<object>();
                        foreach(JsonData item in data.m_List) {
                            list.Add(ToObject(item));
                        }
                        return list;
                    case JsonType.Object:
                        Dictionary<string, object> dict = new Dictionary<string, object>();
                        foreach(KeyValuePair<string, JsonData> item in data.m_Dic) {
                            dict[item.Key] = ToObject(item.Value);
                        }
                        return dict;
                    default:
                        return null;
                }
            } else {
                return obj;
            }
        }
        #endregion


        #region Implicit Conversions
        public static implicit operator JsonData(bool data) {
            return new JsonData(data);
        }

        public static implicit operator JsonData(double data) {
            return new JsonData(data);
        }

        public static implicit operator JsonData(float data) {
            return new JsonData((double)data);
        }

        public static implicit operator JsonData(int data) {
            return new JsonData(data);
        }

        public static implicit operator JsonData(long data) {
            return new JsonData(data);
        }

        public static implicit operator JsonData(string data) {
            return new JsonData(data);
        }
        #endregion


        #region Explicit Conversions
        public static explicit operator bool(JsonData data) {
            if(data.m_Type != JsonType.Boolean)
                throw new InvalidCastException("JsonData doesn't hold a bool");
            return (bool)data.m_Obj;
        }

        public static explicit operator double(JsonData data) {
            if(data.m_Type != JsonType.Double)
                throw new InvalidCastException("JsonData doesn't hold a double");
            return (double)data.m_Obj;
        }

        public static explicit operator float(JsonData data) {
            if(data.m_Type != JsonType.Double)
                throw new InvalidCastException("JsonData doesn't hold a float");
            return (float) (double)data.m_Obj;
        }
        public static explicit operator int(JsonData data) {
            if(data.m_Type != JsonType.Int)
                throw new InvalidCastException("JsonData doesn't hold an int");
            return (int)data.m_Obj;
        }
        public static explicit operator long(JsonData data) {
            if(data.m_Type != JsonType.Long)
                throw new InvalidCastException("JsonData doesn't hold an int");
            return (long)data.m_Obj;
        }

        public static explicit operator string(JsonData data) {
            if(data.m_Type != JsonType.String)
                throw new InvalidCastException("JsonData doesn't hold a string");
            return data.m_Obj as string;
        }
        #endregion

        object IDictionary.this[object key] {
            get {
                return GetDic()[key];
            }
            set {
                if(!(key is string))
                    throw new ArgumentException(
                        "The key has to be a string");

                JsonData data = ToJsonData(value);

                this[(string)key] = data;
            }
        }
        object IList.this[int index] {
            get {
                return GetList()[index];
            }

            set {
                GetList();
                JsonData data = ToJsonData(value);

                this[index] = data;
            }
        }
        #region ToJson
        public string ToJson() {
            StringBuilder builder = new StringBuilder();
            SerializeValue(ToObject(this), builder);
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
                builder.Append(((float)value).ToString("R"));
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
                builder.Append(Convert.ToDouble(value).ToString("R"));
            } else {
                SerializeString(value.ToString(), builder);
            }
        }

        void SerializeString(string str, StringBuilder builder) {
            builder.Append('\"');

            char[] charArray = str.ToCharArray();
            foreach(var c in charArray) {
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

        #endregion
        #region Public Indexers
        public JsonData this[string prop_name] {
            get {
                GetDic();
                return m_Dic[prop_name];
            }

            set {
                GetDic();

                KeyValuePair<string, JsonData> entry = new KeyValuePair<string, JsonData>(prop_name, value);

                if(m_Dic.ContainsKey(prop_name)) {
                    for(int i = 0; i < m_ObjectList.Count; i++) {
                        if(m_ObjectList[i].Key == prop_name) {
                            m_ObjectList[i] = entry;
                            break;
                        }
                    }
                } else
                    m_ObjectList.Add(entry);

                m_Dic[prop_name] = value;
            }
        }

        public JsonData this[int index] {
            get {
                GetCollection();

                if(m_Type == JsonType.Array)
                    return m_List[index];

                return m_ObjectList[index].Value;
            }

            set {
                GetCollection();

                if(m_Type == JsonType.Array) {
                    m_List[index] = value;
                } else {
                    KeyValuePair<string, JsonData> entry = m_ObjectList[index];
                    KeyValuePair<string, JsonData> new_entry =
                        new KeyValuePair<string, JsonData>(entry.Key, value);

                    m_ObjectList[index] = new_entry;
                    m_Dic[entry.Key] = value;
                }
            }
        }
        #endregion

        #region Public Methods
        public int Add(object value) {
            JsonData data = ToJsonData(value);
            return GetList().Add(data);
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
            return GetDic().Contains(key);
        }

        public IDictionaryEnumerator Enumerator() {
            return GetDic().GetEnumerator();
        }

        public bool Equals(JsonData data) {
            if(data == null) return false;
            if(data.m_Type != m_Type) return false;

            switch(m_Type) {
                case JsonType.None: return true;
                case JsonType.Object: return m_Dic.Equals(data.m_Dic);
                case JsonType.Array: return m_List.Equals(data.m_List);

                case JsonType.String:
                case JsonType.Int:
                case JsonType.Long:
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
                    return m_Obj.ToString();

                case JsonType.Array:
                    return "Array";
                case JsonType.Object:
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
            if(m_Type == JsonType.Array)
                return (ICollection)m_List;

            if(m_Type == JsonType.Object)
                return (ICollection)m_Dic;

            throw new InvalidOperationException(
                "The JsonData instance has to be initialized first");
        }

        private IDictionary GetDic() {
            if(m_Type == JsonType.Object)
                return (IDictionary)m_Dic;

            if(m_Type != JsonType.None)
                throw new InvalidOperationException(
                    "JsonData is not a dictionary");

            m_Type = JsonType.Object;
            m_Dic = new Dictionary<string, JsonData>();
            m_ObjectList = new List<KeyValuePair<string, JsonData>>();

            return (IDictionary)m_Dic;
        }

        private IList GetList() {
            if(m_Type == JsonType.Array)
                return (IList)m_List;

            if(m_Type != JsonType.None)
                throw new InvalidOperationException(
                    "JsonData is not a list");

            m_Type = JsonType.Array;
            m_List = new List<JsonData>();

            return (IList)m_List;
        }

        private JsonData ToJsonData(object obj) {
            if(obj == null)
                return null;

            if(obj is JsonData)
                return (JsonData)obj;

            return new JsonData(obj);
        }

        #endregion

        #region ICollection Properties & Methods
        int ICollection.Count {
            get {
                return Count;
            }
        }
        bool ICollection.IsSynchronized {
            get {
                return GetCollection().IsSynchronized;
            }
        }
        object ICollection.SyncRoot {
            get { return GetCollection().SyncRoot; }
        }
        void ICollection.CopyTo(Array array, int index) {
            GetCollection().CopyTo(array, index);
        }
        #endregion

        #region IDictionary Properties & Methods
        bool IDictionary.IsFixedSize {
            get { return GetDic().IsFixedSize; }
        }
        bool IDictionary.IsReadOnly {
            get { return GetDic().IsReadOnly; }
        }
        ICollection IDictionary.Keys {
            get {
                GetDic();
                IList<string> keys = new List<string>();
                foreach(KeyValuePair<string, JsonData> entry in m_ObjectList) {
                    keys.Add(entry.Key);
                }
                return (ICollection)keys;
            }
        }
        ICollection IDictionary.Values {
            get {
                GetDic();
                IList<JsonData> values = new List<JsonData>();
                foreach(KeyValuePair<string, JsonData> entry in m_ObjectList) {
                    values.Add(entry.Value);
                }
                return (ICollection)values;
            }
        }
        void IDictionary.Add(object key, object value) {
            JsonData data = ToJsonData(value);
            GetDic().Add(key, data);
            m_ObjectList.Add(new KeyValuePair<string, JsonData>((string)key, data));   
        }
        void IDictionary.Clear() {
            GetDic().Clear();
            m_ObjectList.Clear();
            
        }
        bool IDictionary.Contains(object key) {
            return GetDic().Contains(key);
        }
        IDictionaryEnumerator IDictionary.GetEnumerator() {
            GetDic();
            return new OrderedDictionaryEnumerator(m_ObjectList.GetEnumerator());
        }
        void IDictionary.Remove(object key) {
            GetDic().Remove(key);
            for(int i = 0; i < m_ObjectList.Count; i++) {
                if(m_ObjectList[i].Key == (string)key) {
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
        bool IList.IsFixedSize {
            get {
                return GetList().IsFixedSize;
            }
        }
        bool IList.IsReadOnly {
            get {
                return GetList().IsReadOnly;
            }
        }
        int IList.Add(object value) {
            return Add(value);
        }
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

    internal class OrderedDictionaryEnumerator : IDictionaryEnumerator {
        IEnumerator<KeyValuePair<string, JsonData>> m_Enumerator;
        public object Current {
            get { return Entry; }
        }
        public DictionaryEntry Entry {
            get {
                KeyValuePair<string, JsonData> curr = m_Enumerator.Current;
                return new DictionaryEntry(curr.Key, curr.Value);
            }
        }
        public object Key {
            get { return m_Enumerator.Current.Key; }
        }
        public object Value {
            get { return m_Enumerator.Current.Value; }
        }
        public OrderedDictionaryEnumerator(
            IEnumerator<KeyValuePair<string, JsonData>> enumerator) {
            m_Enumerator = enumerator;
        }
        public bool MoveNext() {
            return m_Enumerator.MoveNext();
        }
        public void Reset() {
            m_Enumerator.Reset();
        }
    }
}