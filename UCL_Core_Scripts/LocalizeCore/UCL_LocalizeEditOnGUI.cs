using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UCL.Core.LocalizeLib
{
    public class UCL_LocalizeEditOnGUI
    {
        protected LocalizeData mLocalizeData = null;

        public void SetData(LocalizeData iLocalizeData)
        {
            mLocalizeData = iLocalizeData;
        }
        public void OnGUI()
        {
            if(mLocalizeData == null)
            {
                return;
            }
            GUILayout.BeginVertical();
            var aDic = mLocalizeData.GetDic();
            foreach (var aKey in aDic.Keys)
            {
                var aLocalize = mLocalizeData.GetLocalize(aKey);
                var aResult = UCL.Core.UI.UCL_GUILayout.TextField(aKey, aLocalize);
                if (aLocalize != aResult)
                {
                    mLocalizeData.SetLocalize(aKey, aResult);
                    break;
                }
            }

            GUILayout.EndVertical();
        }
    }
}