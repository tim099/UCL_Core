using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UCL.Core.LocalizeLib
{
    public class UCL_LocalizeWindow
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
        }
    }
}