using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.LocalizeLib {

    [CreateAssetMenu(fileName = "New LocalizeEditor", menuName = "UCL/LocalizeEditor")]
    [UCL.Core.ATTR.EnableUCLEditor]
    public class UCL_LocalizeEditor : ScriptableObject {
        [UnityEditor.Callbacks.OnOpenAssetAttribute(2)]
        public static bool step2(int instanceID, int line) {
            var data = UnityEditor.EditorUtility.InstanceIDToObject(instanceID) as UCL_LocalizeEditor;
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
            var data = new Core.LocalizeLib.LocalizeData(m_LocalizeData.text);
            m_LocalizeDic = new List<KeyPair>();
            var dic = data.GetDic();
            foreach(var key in dic.Keys) {
                m_LocalizeDic.Add(new KeyPair(key, dic[key]));
            }
        }
    }
}

