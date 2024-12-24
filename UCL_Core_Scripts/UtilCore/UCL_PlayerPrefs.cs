
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 12/24 2024
using System;
using UnityEngine;

namespace UCL.Core
{
    public static class UCL_PlayerPrefs
    {
        public static void SetEnum<T>(string key, T value) where T : struct, System.Enum
        {
            PlayerPrefs.SetString(key, value.ToString());
        }

        public static T GetEnum<T>(string key, T defaultValue = default) where T : struct, System.Enum
        {
            string value = PlayerPrefs.GetString(key, defaultValue.ToString());
            if(Enum.TryParse<T>(value, true, out var result))
            {
                return result;
            }
            return defaultValue;
        }
    }
}

