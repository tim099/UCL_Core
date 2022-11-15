using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
namespace UCL.Core.LocalizeLib {
    public class UCL_LocalizeText : MonoBehaviour {
        public string m_Key;
        private void Start() {
            UCL_LocalizeManager.OnLanguageChanged += SetText;

            if(UCL_LocalizeManager.GetInstance() == null) return;
            SetText();
        }
        private void OnDestroy() {
            UCL_LocalizeManager.OnLanguageChanged -= SetText;
        }
        public void SetKey(string iKey)
        {
            m_Key = iKey;
            SetText();
        }
        public void SetText() {
            var text = GetComponent<Text>();
            if(text != null) {
                text.text = UCL_LocalizeManager.Get(m_Key);
            }
        }
    }
}