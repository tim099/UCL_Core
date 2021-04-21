using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.Game
{
    public class UCL_GameUIController : MonoBehaviour
    {
        [SerializeField] UCL_GameUI m_UITemplate = null;
        /// <summary>
        /// Load from resource
        /// </summary>
        [SerializeField] string m_UIName = string.Empty;
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
            if (m_UITemplate != null)
            {
                m_UIIns = UCL.Core.Game.UCL_UIService.Ins.CreateUI(m_UITemplate);
            }
            else if(!string.IsNullOrEmpty(m_UIName))
            {
                m_UIIns = UCL.Core.Game.UCL_UIService.Ins.CreateUIFromResource<UCL_GameUI>(m_UIName);
            }
            
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