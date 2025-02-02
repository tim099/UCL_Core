
// ATS_AutoHeader
// to change the auto header please go to ATS_AutoHeader.cs
using UCL.Core.Game;
using UnityEngine;

namespace UCL.Core.EditorLib
{
    public static class UCL_LocalizeInitializeOnLoad
    {
        [UnityEditor.InitializeOnLoadMethod]
        public static void InitializeOnLoad()
        {
            //Debug.Log("UCL_LocalizeInitializeOnLoad InitializeOnLoad");
            EditorInitLocalize();
        }
        /// <summary>
        /// 初始化LocalizeManager 讓非PlayMode也可以抓取翻譯文本(編輯器用)
        /// </summary>
        public static async void EditorInitLocalize()
        {
            if (!UCL_ModuleService.Initialized)
            {
                await UCL_ModuleService.WaitUntilInitialized(default);
            }
            try
            {
                UCL_LocalizeService.SetLanguage(UCL_LocalizeService.CurLang);
            }
            catch (System.Exception ex)
            {
                Debug.LogException(ex);
            }

        }
    }
}

