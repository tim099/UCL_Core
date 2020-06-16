using System.Collections;
using System.Collections.Generic;
using UCL.Core.Tween.Ease;
using UnityEngine;

namespace UCL.Core.Tween.Demo {
    public class UCL_SequenceDemo : MonoBehaviour {
        public UCL.Core.MathLib.UCL_Curve m_Curve;
        public UCL.Core.Tween.Ease.Spring m_EaseSpring;
        public UCL.Core.Tween.Ease.Bounce m_EaseBounce;
        public EaseDir m_Dir;
        UCL.Core.Tween.Ease.UCL_Ease m_Ease;
        public UCL.Core.Tween.EaseClass m_Type = EaseClass.Bounce;
        public Vector2Int m_TextureSize = new Vector2Int(128, 128);
        UCL_EaseTexture m_Texture;
        private void Start() {
            //m_Ease = m_EaseSpring;

            m_Texture = new UCL_EaseTexture(m_TextureSize);
            //m_Texture.SetEase(m_Ease);
        }
        private void Update() {
            switch(m_Type) {
                case EaseClass.Spring: {
                        m_Ease = m_EaseSpring;
                        break;
                    }
                case EaseClass.Bounce: {
                        m_Ease = m_EaseBounce;
                        break;
                    }
                default: {
                        m_Ease = EaseCreator.Get(m_Type).SetDir(m_Dir);
                        break;
                    }
            }
            m_Texture.SetEase(m_Ease);
            Core.DebugLib.UCL_DebugOnGUI.Instance.CreateData().SetOnGUIAct(() => {
                GUILayout.BeginVertical();
                //string target_name = this.GetType().UnderlyingSystemType.Name.Replace("UCL_", "");
                GUILayout.Box("Min:" + m_Texture.m_Min.ToString("N2")+",Max:" +m_Texture.m_Max.ToString("N2"), GUILayout.Width(m_TextureSize.x + 4));
                GUILayout.Box(m_Type.ToString()+" "+m_Ease.m_Dir.ToString(), GUILayout.Width(m_TextureSize.x + 4));
                //GUILayout.Box("Range:" + m_Texture.m_Range, GUILayout.Width(m_TextureSize.x + 4));
                //GUILayout.Box(name, GUILayout.Width(m_TextureSize.x + 4));
                GUILayout.Box(m_Texture.texture);
                GUILayout.EndVertical();
            });
        }
    }
}

