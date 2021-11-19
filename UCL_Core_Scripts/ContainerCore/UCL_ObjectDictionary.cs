﻿using System.Collections;
using System.Collections.Generic;

namespace UCL.Core
{
    public interface UCLI_ObjectDictionary
    {
        bool ContainsKey(string iKey);
        void SetData(string iKey, object iObj);
        void Remove(string iKey);
        object GetData(string iKey, object iDefaultValue = null);
        T GetData<T>(string iKey, T iDefaultValue = default);
        //public UCLI_ObjectDictionary GetSubDic(string iKey);
    }
    public class UCL_ObjectDictionary : UCLI_ObjectDictionary
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
        /// Swap two element in this dic
        /// </summary>
        /// <param name="iKeyA"></param>
        /// <param name="iKeyB"></param>
        public void Swap(string iKeyA, string iKeyB)
        {
            if(iKeyA == iKeyB || !m_DataDic.ContainsKey(iKeyA) || !m_DataDic.ContainsKey(iKeyB))
            {
                return;
            }
            var aTmp = m_DataDic[iKeyA];
            m_DataDic[iKeyA] = m_DataDic[iKeyB];
            m_DataDic[iKeyB] = aTmp;
        }
        /// <summary>
        /// Get sub UCL_ObjectDictionary inside this UCL_ObjectDictionary
        /// if not exist, then this will create a new one
        /// </summary>
        /// <param name="iKey"></param>
        /// <returns></returns>
        public UCL_ObjectDictionary GetSubDic(string iKey)
        {
            if (!m_DataDic.ContainsKey(iKey))
            {
                m_DataDic.Add(iKey, new UCL_ObjectDictionary());
            }
            if (!(m_DataDic[iKey] is UCL_ObjectDictionary))
            {
                m_DataDic[iKey] = new UCL_ObjectDictionary();
            }
            return m_DataDic[iKey] as UCL_ObjectDictionary;
        }
        public object this[string iKey]
        {
            get { return m_DataDic[iKey]; }
            set { m_DataDic[iKey] = value; }
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
        /// Add data
        /// </summary>
        /// <param name="iKey"></param>
        /// <param name="iObj"></param>
        public void Add(string iKey, object iObj)
        {
            m_DataDic.Add(iKey, iObj);
        }

        /// <summary>
        /// Remove data from Dic
        /// </summary>
        /// <param name="iKey"></param>
        public void Remove(string iKey)
        {
            m_DataDic.Remove(iKey);
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
}

