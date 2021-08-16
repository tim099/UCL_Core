using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.LocalizeLib {

    [CreateAssetMenu(fileName = "New LocalizeEditor", menuName = "UCL/LocalizeEditor")]
    [UCL.Core.ATTR.EnableUCLEditor]
    public class UCL_LocalizeEditor : ScriptableObject {
        [UnityEditor.Callbacks.OnOpenAssetAttribute(2)]
        public static bool OnOpenAsset(int iInstanceID, int iLine) {
            var data = UnityEditor.EditorUtility.InstanceIDToObject(iInstanceID) as UCL_LocalizeEditor;
            if(data != null) {
                //ShowWindow(data);
                return true;
            }
            return false; // we did not handle the open
        }

        public TextAsset m_LocalizeData;
        public List<KeyPair> m_LocalizeDic = new List<KeyPair>();

        [UCL.Core.ATTR.UCL_FunctionButton]
        public void ParseDataToDic() {
            var aData = new Core.LocalizeLib.LocalizeData(m_LocalizeData.text);
            m_LocalizeDic = new List<KeyPair>();
            var aDic = aData.GetDic();
            foreach(var aKey in aDic.Keys) {
                m_LocalizeDic.Add(new KeyPair(aKey, aDic[aKey]));
            }
        }
    }
}

