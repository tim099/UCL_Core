using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
namespace UCL.Core.Game {
    public class UCL_LocalizeText : MonoBehaviour {
        public string m_Key;
        private void Start() {
            if(UCL_LocalizeService.ins == null) return;
            SetText();
            UCL_LocalizeService.ins.OnLocalizationChanged += SetText;
        }
        private void OnDestroy() {
            if(UCL_LocalizeService.ins == null) return;
            UCL_LocalizeService.ins.OnLocalizationChanged -= SetText;
        }
        public void SetText() {
            if(UCL_LocalizeService.ins == null) return;
            var text = GetComponent<Text>();
            if(text != null) {
                text.text = UCL_LocalizeService.Get(m_Key);
            }
        }
    }
}