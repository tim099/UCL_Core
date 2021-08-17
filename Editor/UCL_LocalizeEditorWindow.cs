using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
namespace UCL.Core.LocalizeLib
{
    public class UCL_LocalizeEditorWindow : EditorWindow
    {
        public static UCL_LocalizeEditorWindow ShowWindow(LocalizeData iTarget)
        {
            var aWindow = EditorWindow.GetWindow<UCL_LocalizeEditorWindow>("LocalizeEditor");
            aWindow.Init(iTarget);
            return aWindow;
        }
        Rect m_GridRegion = new Rect();
        public UCL_LocalizeEditOnGUI m_LocalizeEditOnGUI = null;
        public void Init(LocalizeData iTarget)
        {
            m_LocalizeEditOnGUI = new UCL_LocalizeEditOnGUI();
            m_LocalizeEditOnGUI.SetData(iTarget);
        }

        private void OnGUI()
        {
            if (m_LocalizeEditOnGUI == null) return;
            m_LocalizeEditOnGUI.OnGUI();
            if (Event.current.type == EventType.Repaint)
            {
                var aNewRgn = GUILayoutUtility.GetLastRect();
                if (aNewRgn != m_GridRegion)
                {
                    m_GridRegion = aNewRgn;
                    Repaint();
                }
            }
        }
    }
}