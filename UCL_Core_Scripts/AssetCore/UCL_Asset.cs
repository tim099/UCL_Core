using System.Collections;
using System.Collections.Generic;
using UCL.Core.JsonLib;
using UnityEngine;

namespace UCL.Core
{
    public interface UCLI_ID
    {
        /// <summary>
        /// Unique ID of this Data
        /// </summary>
        string ID { get; set; }
    }


    public class UCL_Asset<T> : UnityJsonSerializable, UCLI_ID where T : class, new()
    {
        #region static
        /// <summary>
        /// Util object for this asset
        /// </summary>
        public static T Util
        {
            get
            {
                if(s_Util == null)
                {
                    s_Util = new T();
                }
                return s_Util;
            }
        }
        private static T s_Util = null;
        #endregion

        #region Interface
        public string ID { get => m_ID; set => m_ID = value; }
        #endregion


        [SerializeField]
        protected string m_ID;
    }
}

