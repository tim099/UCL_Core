using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.Game
{
    public class UCL_GameUI : MonoBehaviour
    {
        virtual public bool IsUIOverlay { get { return m_IsUIOverlay; } }
        [SerializeField] protected bool m_IsUIOverlay = false;
        protected System.Action m_OnCloseAction = null;
        protected bool m_IsClosed = false;
        virtual public void Init()
        {

        }
        /// <summary>
        /// iOnCloseAction will be called when this UI Closed
        /// </summary>
        /// <param name="iOnCloseAction"></param>
        virtual public void SetOnCloseAction(System.Action iOnCloseAction)
        {
            m_OnCloseAction = iOnCloseAction;
        }
        /// <summary>
        /// Close this UI
        /// </summary>
        virtual public void Close()
        {
            if (m_IsClosed) return;
            m_IsClosed = true;
            if (m_OnCloseAction != null)
            {
                m_OnCloseAction.Invoke();
            }
            
            UCL.Core.Game.UCL_UIService.Ins.OnUIClosed(this);
            if(gameObject != null) Destroy(gameObject);
        }
        /// <summary>
        /// When Input.GetKey(KeyCode.Escape) == true
        /// </summary>
        virtual public void OnEscape()
        {
            Close();
        }
    }
}