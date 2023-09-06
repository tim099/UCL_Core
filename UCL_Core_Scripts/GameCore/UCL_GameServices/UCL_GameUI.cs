using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.Game
{
    public class UCL_GameUI : MonoBehaviour
    {
        public static T CreateFromResource<T>() where T : UCL_GameUI
        {
           return  UCL_UIService.Ins.CreateUIFromResource<T>();
        }

        virtual public bool IsUIOverlay => m_IsUIOverlay;
        virtual public bool Reusable => false;
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
            //if(gameObject != null) Destroy(gameObject); 改到UCL.Core.Game.UCL_UIService.Ins.OnUIClosed
        }
        /// <summary>
        /// Reusable UI enqueue
        /// </summary>
        virtual public void OnRecycle()
        {
            m_IsClosed = false;
        }
        /// <summary>
        /// When Input.GetKey(KeyCode.Escape) == true
        /// </summary>
        virtual public void OnEscape()
        {
            //Close();
        }
    }
}