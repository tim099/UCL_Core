namespace UCL.Core.JsonLib {

    /// <summary>
    /// interface that support Json serialization
    /// </summary>
    public interface IJsonSerializable : UCL.Core.UCLI_CopyPaste
    {
        JsonData SerializeToJson();
        void DeserializeFromJson(JsonData iJson);
    }
    /// <summary>
    /// Object inherit from this class will save ClassName in JsonData
    /// and if the object is inside a list, the object can be restore to it's original class instead of generic class of the list
    /// </summary>
    public class UnityJsonSerializableObject : IJsonSerializable
    {
        virtual public JsonData SerializeToJson()
        {
            var aData = new JsonData();
            aData["ClassName"] = this.GetType().AssemblyQualifiedName;
            aData["ClassData"] = JsonConvert.SaveFieldsToJsonUnityVer(this);
            return aData;
        }
        virtual public void DeserializeFromJson(JsonData iJson)
        {
            if (iJson.Contains("ClassData"))
            {
                JsonConvert.LoadFieldFromJsonUnityVer(this, iJson["ClassData"]);
            }
        }
    }
    public class UnityJsonSerializable : IJsonSerializable
    {
        virtual public JsonData SerializeToJson()
        {
            return JsonConvert.SaveFieldsToJsonUnityVer(this);
        }
        virtual public void DeserializeFromJson(JsonData iJson)
        {
            JsonConvert.LoadFieldFromJsonUnityVer(this, iJson);
        }
    }
    public class JsonSerializable : IJsonSerializable
    {
        virtual public JsonConvert.SaveMode SaveMode => JsonConvert.SaveMode.Unity;
        virtual public string FieldNameFunction(string iFieldName) => UCL.Core.UCL_StaticFunctions.FieldNameUnityVer(iFieldName);
        virtual public JsonData SerializeToJson()
        {
            return UCL.Core.JsonLib.JsonConvert.SaveFieldsToJson(this, SaveMode, FieldNameFunction);
        }
        virtual public void DeserializeFromJson(JsonData iJson)
        {
            JsonConvert.LoadFieldFromJson(this, iJson, SaveMode, FieldNameFunction);
        }
    }

}