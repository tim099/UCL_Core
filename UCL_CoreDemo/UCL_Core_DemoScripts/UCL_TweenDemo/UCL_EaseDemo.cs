using System;
using System.Collections;
using System.Collections.Generic;
using UCL.Core.Tween.Ease;
using UnityEngine;

namespace UCL.Core.Tween.Demo {
#if UNITY_EDITOR
    [ATTR.EnableUCLEditor]
#endif
    public class UCL_EaseDemo : MonoBehaviour {
        //public UCL.Core.Tween.Ease.Spring m_EaseSpring;
        //public UCL.Core.Tween.Ease.Bounce m_EaseBounce;
        public bool f_LoopDir = true;
        public int m_LoopTime = 300;
        public EaseDir m_Dir;
        UCL.Core.Tween.Ease.UCL_Ease m_Ease;

        //public bool f_SetByEaseType = false;
        //public EaseType m_EaseType;//[PA.UCL_ReadOnly] 
        
        public UCL.Core.Tween.EaseClass m_Type = EaseClass.Bounce;
        public Vector2Int m_TextureSize = new Vector2Int(128, 128);
        UCL_EaseTexture m_Texture;
        int m_LoopTimer = 0;
        private void Start() {
            //m_Ease = m_EaseSpring;

            m_Texture = new UCL_EaseTexture(m_TextureSize);
            //m_Texture.SetEase(m_Ease);
        }
#if UNITY_EDITOR
        [ATTR.UCL_FunctionButton]
        public void OpenEaseScript() {
            var ease = EaseCreator.Get(m_Type, m_Dir);
            if(ease == null) return;

            string sc_path = ease.GetScriptPath(FileLib.EditorLib.GetCoreFolderPath(), ease.GetType().Name);
            Debug.Log("EaseScript:" + sc_path);
            var obj = UnityEditor.AssetDatabase.LoadMainAssetAtPath(sc_path);
            if(obj != null) {
                UnityEditor.Selection.activeObject = obj;
            } else {
                Debug.LogWarning("EaseScript:" + sc_path + " ,Not Found!!");
            }
            
        }
        [ATTR.UCL_DrawTexture2D(128,128, TextureFormat.ARGB32, "UCL_EaseTexture")]
        public void DrawEaseCurve(Core.TextureLib.UCL_Texture2D texture) {
            var ease = EaseCreator.Get(m_Type, m_Dir);
            if(ease == null) return;
            UCL_EaseTexture tex = texture as UCL_EaseTexture;
            if(tex == null) return;
            tex.SetEase(ease);
        }
#endif
        private void Update() {
            if(f_LoopDir) {
                m_LoopTimer++;
                if(m_LoopTimer > m_LoopTime) {
                    m_LoopTimer = 0;
                    int val = (int)m_Dir;
                    val++;
                    if(val >= Enum.GetNames(typeof(EaseDir)).Length) {
                        val = 0;
                    }
                    m_Dir = (EaseDir)val;
                }
            }
            m_Ease = EaseCreator.Get(m_Type, m_Dir);
            /*
            if(!f_SetByEaseType) {
                m_EaseType = EaseCreator.GetType(m_Type, m_Dir);
            } else {
                m_Type = EaseCreator.GetClass(m_EaseType);
                m_Dir = EaseCreator.GetDir(m_EaseType);
            }
            */
            //m_Ease.SetDir(m_Dir);
            m_Texture.SetEase(m_Ease);
            Core.DebugLib.UCL_DebugOnGUI.Instance.CreateData().SetOnGUIAct(() => {
                GUILayout.BeginVertical();
                //string target_name = this.GetType().UnderlyingSystemType.Name.Replace("UCL_", "");
                GUILayout.Box("Min:" + m_Texture.m_Min.ToString("N2") + ",Max:" + m_Texture.m_Max.ToString("N2"), GUILayout.Width(m_TextureSize.x + 4));
                GUILayout.Box(m_Type.ToString() + " " + m_Ease.m_Dir.ToString(), GUILayout.Width(m_TextureSize.x + 4));
                //GUILayout.Box("Range:" + m_Texture.m_Range, GUILayout.Width(m_TextureSize.x + 4));
                //GUILayout.Box(name, GUILayout.Width(m_TextureSize.x + 4));
                GUILayout.Box(m_Texture.texture);
                GUILayout.EndVertical();
            });
        }
    }
}