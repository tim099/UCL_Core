
// ATS_AutoHeader
// to change the auto header please go to ATS_AutoHeader.cs
// Create time : 02/20 2024 22:46
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core
{
    public enum UCL_AssetType
    {
        StreamingAssets = 0,
        PersistentData,

        Addressables,
        /// <summary>
        /// Path xxx\SteamLibrary\steamapps\workshop\content\xxxx
        /// Example D:\SteamLibrary\steamapps\workshop\content\1158310\2973143830
        /// </summary>
        SteamMods,
    }
    /// <summary>
    /// UCL_Module contains info about how to load assets in this module
    /// </summary>
    public class UCL_Module : UCL.Core.JsonLib.UnityJsonSerializable, UCLI_ID
    {

        public UCL_AssetType m_AssetType = UCL_AssetType.StreamingAssets;

        [SerializeField] protected string m_ID;

        #region Interface
        /// <summary>
        /// Unique ID of this Module
        /// </summary>
        public string ID { get => m_ID; set => m_ID = value; }
        #endregion
    }
}