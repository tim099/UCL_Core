
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 12/24 2024
using UnityEngine;


namespace UCL.Core
{
    public class UCL_MiscUtil
    {
        /// <summary>
        /// . .. ...base on time(Second)
        /// </summary>
        /// <param name="dotCount"></param>
        /// <returns></returns>
        public static string LoadingDot(int dotCount = 3)
        {
            var now = System.DateTime.Now.Second % (dotCount + 1);
            string dynamicStr = new string('.', now);
            return dynamicStr;
        }
    }
}