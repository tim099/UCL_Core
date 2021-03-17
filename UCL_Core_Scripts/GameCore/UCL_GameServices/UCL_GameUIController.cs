using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.Game
{
    public class UCL_GameUIController : MonoBehaviour
    {
        [SerializeField] UCL_GameUI m_UITemplate = null;
        UCL_GameUI m_UIIns = null;
        public void Toggle()
        {
            if (m_UIIns != null)
            {
                Hide();
            }
            else
            {
                Show();
            }
        }
        public void Show()
        {
            if (m_UIIns != null)
            {
                return;
            }
            m_UIIns = UCL.Core.Game.UCL_UIService.Ins.CreateUI(m_UITemplate);
        }
        public void Hide()
        {
            if (m_UIIns == null)
            {
                return;
            }
            m_UIIns.Close();
            m_UIIns = null;
        }
    }
}