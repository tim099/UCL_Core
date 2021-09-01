using System.Collections;
using System.Collections.Generic;

public class UCL_ObjectDictionary
{
    protected Dictionary<string, object> m_DataDic = new Dictionary<string, object>();
    /// <summary>
    /// Check if the key exist in Dictionary
    /// </summary>
    /// <param name="iKey"></param>
    /// <returns></returns>
    public bool ContainsKey(string iKey)
    {
        return m_DataDic.ContainsKey(iKey);
    }
    /// <summary>
    /// Clear all data in Dictionary
    /// </summary>
    public void Clear()
    {
        m_DataDic.Clear();
    }
    /// <summary>
    /// Set data
    /// </summary>
    /// <param name="iKey"></param>
    /// <param name="iObj"></param>
    public void SetData(string iKey, object iObj)
    {
        m_DataDic[iKey] = iObj;
    }
    /// <summary>
    /// get data from Dic
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="iKey"></param>
    /// <param name="iDefaultValue"></param>
    /// <returns></returns>
    public T GetData<T>(string iKey, T iDefaultValue = default)
    {
        if (!m_DataDic.ContainsKey(iKey))
        {
            return iDefaultValue;
        }
        return (T)m_DataDic[iKey];
    }
    /// <summary>
    /// get data from Dic
    /// </summary>
    /// <param name="iKey"></param>
    /// <param name="iDefaultValue"></param>
    /// <returns></returns>
    public object GetData(string iKey, object iDefaultValue = null)
    {
        if (!m_DataDic.ContainsKey(iKey))
        {
            return iDefaultValue;
        }
        return m_DataDic[iKey];
    }
}
