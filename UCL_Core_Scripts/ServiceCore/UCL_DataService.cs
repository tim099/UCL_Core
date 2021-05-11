using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.ServiceLib
{
    /// <summary>
    /// UCL_DataService can work on both Edit mode and Play mode
    /// </summary>
    public class UCL_DataService
    {
        public static UCL_DataService Instance
        {
            get
            {
                if(m_Instance == null)
                {
                    m_Instance = new UCL_DataService();
                    m_Instance.Init();
                }
                return m_Instance;
            }
        }
        static UCL_DataService m_Instance = null;
        /// <summary>
        /// FrameData only last for one frame(Clear after next frame update)
        /// </summary>
        protected Dictionary<string, object> m_FrameDataDic = new Dictionary<string, object>();
        /// <summary>
        /// Prev frame's FrameData
        /// </summary>
        protected Dictionary<string, object> m_PrevFrameDataDic = new Dictionary<string, object>();

        protected Dictionary<string, object> m_DataDic = new Dictionary<string, object>();
        protected void Init()
        {
#if UNITY_EDITOR
            UCL.Core.ServiceLib.UCL_UpdateService.AddUpdateActionStaticVer(Update);
#endif
        }
        /// <summary>
        /// Set a FrameData, FrameData only last for one frame
        /// </summary>
        /// <param name="iKey"></param>
        /// <param name="iVal"></param>
        public void SetFrameData(string iKey, object iVal)
        {
            m_FrameDataDic[iKey] = iVal;
        }
        /// <summary>
        /// Get the FrameData by key
        /// </summary>
        /// <param name="iKey"></param>
        /// <param name="iDefaultVal"></param>
        /// <returns></returns>
        public T GetFrameData<T>(string iKey, T iDefaultVal = default)
        {
            if (m_FrameDataDic.ContainsKey(iKey))
            {
                return (T)m_FrameDataDic[iKey];
            }
            if (m_PrevFrameDataDic.ContainsKey(iKey))
            {
                return (T)m_PrevFrameDataDic[iKey];
            }
            return iDefaultVal;
        }
        /// <summary>
        /// Save data to DataService
        /// </summary>
        /// <param name="iKey"></param>
        /// <param name="iVal"></param>
        public void SetData(string iKey, object iVal)
        {
            m_DataDic[iKey] = iVal;
        }
        /// <summary>
        /// Get data from DataService
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="iKey"></param>
        /// <param name="iDefaultVal"></param>
        /// <returns></returns>
        public T GetData<T>(string iKey, T iDefaultVal = default)
        {
            if (m_DataDic.ContainsKey(iKey))
            {
                return (T)m_DataDic[iKey];
            }
            return iDefaultVal;
        }
        /// <summary>
        /// Clear all Datas
        /// </summary>
        public void Clear()
        {
            m_DataDic.Clear();
            m_PrevFrameDataDic.Clear();
            m_FrameDataDic.Clear();
        }
        protected void Update()
        {
            //Clear prev frame datas
            m_PrevFrameDataDic.Clear();
            //preserve data for one frame
            m_PrevFrameDataDic = m_FrameDataDic;
        }
    }
}