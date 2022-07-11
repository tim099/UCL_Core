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
            aData["ClassData"] = JsonConvert.SaveDataToJson(this, JsonConvert.SaveMode.Unity, UCL.Core.UCL_StaticFunctions.FieldNameUnityVer);
            return aData;
        }
        virtual public void DeserializeFromJson(JsonData iJson)
        {
            if (iJson.Contains("ClassData"))
            {
                JsonConvert.LoadDataFromJson(this, iJson["ClassData"], JsonConvert.SaveMode.Unity, UCL.Core.UCL_StaticFunctions.FieldNameUnityVer);
            }
        }
        virtual public UnityJsonSerializableObject CloneObject()
        {
            JsonData aData = this.SerializeToJson();
            var aObject = this.GetType().CreateInstance() as UnityJsonSerializableObject;
            aObject.DeserializeFromJson(aData);
            return aObject;
        }
    }
    public class UnityJsonSerializable : IJsonSerializable
    {
        virtual public JsonData SerializeToJson()
        {
            var aData = new JsonData();
            JsonConvert.SaveFieldsToJson(this, aData, JsonConvert.SaveMode.Unity, UCL.Core.UCL_StaticFunctions.FieldNameUnityVer);
            return aData;
        }
        virtual public void DeserializeFromJson(JsonData iJson)
        {
            JsonConvert.LoadDataFromJson(this, iJson, JsonConvert.SaveMode.Unity, UCL.Core.UCL_StaticFunctions.FieldNameUnityVer);
        }
    }
    public class JsonSerializable : IJsonSerializable
    {
        virtual public JsonConvert.SaveMode SaveMode => JsonConvert.SaveMode.Normal;
        virtual public JsonData SerializeToJson()
        {
            return UCL.Core.JsonLib.JsonConvert.SaveDataToJson(this, SaveMode);
        }
        virtual public void DeserializeFromJson(JsonData iJson)
        {
            JsonConvert.LoadDataFromJson(this, iJson, SaveMode);
        }
    }

}