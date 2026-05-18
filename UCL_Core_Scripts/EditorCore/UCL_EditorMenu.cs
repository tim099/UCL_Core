// 2026-05-18 (gura T19 Round 2 fix): 整檔包 #if UNITY_EDITOR — 用 UCL_EditorMenuPage (Editor-only),
// Player Build 找不到 → CS0246. Family A 跨檔 guard 一致 (對齊 UCL_EditorMenuPage 整檔 guard).
#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UCL.Core.EditorLib.Page;
namespace UCL.Core.EditorLib
{
    public class UCL_EditorMenu
    {
        /// <summary>
        /// true if running in EditWindow function
        /// </summary>
        public static bool IsInEditWindow = false;
        UCL.Core.UI.UCL_GUIPageController m_GUIPageController = new UCL.Core.UI.UCL_GUIPageController();
        bool m_Inited = false;
        virtual public void Init()
        {

            if (m_Inited) return;
            m_Inited = true;
            UCL.Core.ServiceLib.UCL_UpdateService.AddUpdateAction(() =>
            {
                m_GUIPageController.Update();
            });
            //Debug.LogError("Application.isPlaying:" + Application.isPlaying);
        }
        virtual public void EditWindow(int iID)
        {
            //if (!Application.isPlaying) UCL.Core.UI.UCL_GUIPageController.Ins = m_GUIPageController;
            IsInEditWindow = true;
            var aPrevCol = GUI.contentColor;
            GUI.contentColor = Color.white;

            GUILayout.BeginVertical();
            if (m_GUIPageController.TopPage == null)
            {
                UCL_EditorPage.Create<UCL_EditorMenuPage>(m_GUIPageController);
                //Page.RCG_EditorMenuPage.CreateAndPush(m_GUIPageController);
            }
            m_GUIPageController.DrawOnGUI();

            GUILayout.EndVertical();

            GUI.contentColor = aPrevCol;
            IsInEditWindow = false;
        }
    }
}
#endif // UNITY_EDITOR
