namespace UCL.Core.JsonLib {

    /// <summary>
    /// interface that support Json serialization
    /// </summary>
    public interface IJsonSerializable
    {
        JsonData SerializeToJson();
        void DeserializeFromJson(JsonData iJson);
    }
    public class UnityJsonSerializableObject : IJsonSerializable
    {
        virtual public JsonData SerializeToJson()
        {
            var aData = new JsonData();
            aData["ClassName"] = this.GetType().AssemblyQualifiedName;
            aData["ClassData"] = JsonConvert.SaveDataToJson(this, JsonConvert.SaveMode.Unity, (iName) => iName.Replace("m_", string.Empty));
            return aData;
        }
        virtual public void DeserializeFromJson(JsonData iJson)
        {
            if (iJson.Contains("ClassData"))
            {
                JsonConvert.LoadDataFromJson(this, iJson["ClassData"], JsonConvert.SaveMode.Unity, (iFName) => iFName.Replace("m_", string.Empty));
            }
        }
    }
    public class UnityJsonSerializable : IJsonSerializable
    {
        virtual public JsonData SerializeToJson()
        {
            return UCL.Core.JsonLib.JsonConvert.SaveDataToJson(this, JsonConvert.SaveMode.Unity, (iName) => iName.Replace("m_", string.Empty));
        }
        virtual public void DeserializeFromJson(JsonData iJson)
        {
            JsonConvert.LoadDataFromJson(this, iJson, JsonConvert.SaveMode.Unity, (iFName) => iFName.Replace("m_", string.Empty));
        }
    }
    public class JsonSerializable : IJsonSerializable
    {
        virtual public JsonData SerializeToJson()
        {
            return UCL.Core.JsonLib.JsonConvert.SaveDataToJson(this, JsonConvert.SaveMode.Normal);
        }
        virtual public void DeserializeFromJson(JsonData iJson)
        {
            JsonConvert.LoadDataFromJson(this, iJson, JsonConvert.SaveMode.Normal);
        }
    }

}