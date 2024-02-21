using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace UCL.Core
{
    /// <summary>
    /// Responsible for all operations related to Module
    /// </summary>
    public class UCL_ModuleService
    {
        public static UCL_ModuleService Ins
        {
            get
            {
                if(s_Ins == null)
                {
                    s_Ins = new UCL_ModuleService();
                    s_Ins.Init();
                }
                return s_Ins;
            }
        }
        private static UCL_ModuleService s_Ins = null;
        public static bool Initialized
        {
            get
            {
                return Ins.m_Initialized;
            }
        }
        private bool m_Initialized = false;
        /// <summary>
        /// 暫定固定會有一個核心設定模組 放在StreammingAssets
        /// 只有在Editor內能夠編輯 Build出來的版本只能夠讀取(用UnityWebRequest讀取StreammingAssets 避免跨平台出問題)
        /// </summary>
        private void Init()
        {
            _ = InitAsync();
        }

        private async Task InitAsync()
        {
            //Debug.LogError("InitAsync()");
            //string aStr = await UCL_StreamingAssets.LoadString(".Install/CommonData/ATS_IconSprite/Icon_Heal.json");
            //Debug.LogError($"InitAsync() End aStr:{aStr}");
            m_Initialized = true;
        }
    }
}
