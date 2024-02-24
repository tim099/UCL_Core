
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
        public static List<string> GetAddressablePath()
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
            var aList = aSet.ToList();
            //Debug.LogError($"aList:{aList.ConcatString()}");
            return aList;
        }
        public static List<string> GetAllAddressableKeys()
        {
            List<string> aList = new List<string>();
            foreach (var aResourceLocator in UnityEngine.AddressableAssets.Addressables.ResourceLocators)
            {
                int aIndex = 0;
                foreach (var aKey in aResourceLocator.Keys)
                {
                    if (aKey is string aStr)
                    {
                        aList.Add(aStr);
                        ++aIndex;
                    }
                }
            }
            //Debug.LogError($"GetAllAddressableKeys.aList:{aList.ConcatString()}");
            return aList;
        }
        public static List<string> GetAllAddressableKeys(string iAddressablePath)
        {
            List<string> aList = new List<string>();
            aList.Add(string.Empty);
            foreach (var aResourceLocator in UnityEngine.AddressableAssets.Addressables.ResourceLocators)
            {
                int aIndex = 0;
                foreach (var aKey in aResourceLocator.Keys)
                {
                    if (aKey is string aStr)
                    {
                        if (string.IsNullOrEmpty(iAddressablePath))
                        {
                            aList.Add(aStr);
                        }
                        else if (aStr.Contains(iAddressablePath))
                        {
                            aList.Add(aStr);
                        }
                        ++aIndex;
                    }
                }
            }
            //Debug.LogError($"GetAllAddressableKeys iAddressablePath:{iAddressablePath} ,aList:{aList.ConcatString()}");
            return aList;
        }
        #endregion
    }
}