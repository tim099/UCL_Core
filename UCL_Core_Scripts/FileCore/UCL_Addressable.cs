
// ATS_AutoHeader
// to change the auto header please go to ATS_AutoHeader.cs
// Create time : 02/24 2024 09:46
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace UCL.Core
{
    public static class UCL_Addressable
    {

        #region Addressable
        private static List<string> s_AddressablePath = null;
        public static List<string> GetAddressablePath()
        {
            bool aRefreshAddressablePath = true;
#if UNITY_EDITOR
            //aRefreshAddressablePath = true;
#else
            aRefreshAddressablePath = (s_AddressablePath == null);//refresh if (s_AddressablePath == null)
#endif
            if (aRefreshAddressablePath)
            {
                HashSet<string> aSet = new HashSet<string>();
                aSet.Add(string.Empty);
                foreach (var aKey in GetAllAddressableKeys())
                {
                    if (aKey.Contains("/"))
                    {
                        aSet.Add(UCL.Core.FileLib.Lib.RemoveFolderPath(aKey, 1));
                    }
                }
                s_AddressablePath = aSet.ToList();
            }
            //Debug.LogError($"aList:{aList.ConcatString()}");
            return s_AddressablePath;
        }

        private static List<string> s_AllAddressableKeys = null;
        public static List<string> GetAllAddressableKeys()
        {
            bool aRefreshAddressableKeys = true;
#if !UNITY_EDITOR
            aRefreshAddressableKeys = (s_AllAddressableKeys == null);//refresh if (s_AllAddressableKeys == null)
#endif
            if (aRefreshAddressableKeys)
            {
                if(s_AllAddressableKeys == null)
                {
                    s_AllAddressableKeys = new List<string>();
                    UnityEngine.AddressableAssets.Addressables.InitializeAsync();
                }
                else
                {
                    s_AllAddressableKeys.Clear();
                }
                
                foreach (var aResourceLocator in UnityEngine.AddressableAssets.Addressables.ResourceLocators)
                {
                    foreach (var aKey in aResourceLocator.Keys)
                    {
                        if (aKey is string aStr)
                        {
                            s_AllAddressableKeys.Add(aStr);
                        }
                        //else
                        //{
                        //    Debug.LogError($"aKey:{aKey}");
                        //}
                    }
                }
            }

            //Debug.LogError($"aRefreshAddressableKeys:{aRefreshAddressableKeys}, GetAllAddressableKeys.aList:{s_AllAddressableKeys.ConcatString()}");
            return s_AllAddressableKeys;
        }
        public static List<string> GetAllAddressableKeys(string iAddressablePath)
        {
            List<string> aList = new List<string>();
            aList.Add(string.Empty);
            foreach (var aKey in GetAllAddressableKeys())
            {
                if (string.IsNullOrEmpty(iAddressablePath))
                {
                    aList.Add(aKey);
                }
                else if (aKey.Contains(iAddressablePath))
                {
                    aList.Add(aKey);
                }
            }
            //Debug.LogError($"GetAllAddressableKeys iAddressablePath:{iAddressablePath} ,aList:{aList.ConcatString()}");
            return aList;
        }
        #endregion
    }
}