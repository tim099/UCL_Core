using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UCL.Core.EditorLib {
    public class UCL_MenuWindow : EditorWindow
    {
        Rect m_GridRegion = new Rect();
        public UCL_EditorMenu m_Editor;
        public void Init(UCL_EditorMenu iEditor)
        {
            m_Editor = iEditor;
        }
        [UnityEditor.MenuItem("UCL/Menu")]
        public static void ShowMenu()
        {
            ShowWindow(new UCL_EditorMenu());
        }
        public static UCL_MenuWindow ShowWindow(UCL_EditorMenu iTarget)
        {
            var aWindow = EditorWindow.GetWindow<UCL_MenuWindow>("UCL_EditorMenu");
            aWindow.Init(iTarget);
            return aWindow;
        }
        private void OnGUI()
        {
            if (m_Editor == null)
            {
                m_Editor = new UCL_EditorMenu();
            }
            UCL.Core.UI.UCL_GUIStyle.IsInEditorWindow = true;
            m_Editor.Init();
            m_Editor.EditWindow(0);
            if (Event.current.type == EventType.Repaint)
            {
                var aNewRgn = GUILayoutUtility.GetLastRect();
                if (aNewRgn != m_GridRegion)
                {
                    m_GridRegion = aNewRgn;
                    Repaint();
                }
            }
            UCL.Core.UI.UCL_GUIStyle.IsInEditorWindow = false;
        }
    }
}

