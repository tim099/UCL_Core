using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.UI
{
    /// <summary>
    /// GUIPage only show the top page on GUI
    /// </summary>
    public class UCL_GUIPage
    {
        /// <summary>
        /// this will create a UIOverlay panel that block all UIs if BlockCanvas is true
        /// </summary>
        virtual public bool IsBlockCanvas => true;
        static Color s_BlockCanvasColor = new Color(0, 0, 0, 0.7f);
        virtual public Color BlockCanvasColor => s_BlockCanvasColor;
        public UCL_GUIPageController p_Controller { get; protected set; } = null;

        public event System.Action OnCloseEvent = null;

        /// <summary>
        /// called when this page push into stack
        /// </summary>
        virtual public void Init(UCL_GUIPageController iGUIPageController) {
            p_Controller = iGUIPageController;
        }
        /// <summary>
        /// Close this page(remove this page from p_Controller)
        /// </summary>
        virtual public void Close()
        {
            if (p_Controller != null) p_Controller.Remove(this);
        }
        /// <summary>
        /// called when new page replace this page and became the TopPage
        /// </summary>
        virtual public void OnPause() { }
        /// <summary>
        /// called when this page became the TopPage
        /// </summary>
        virtual public void OnResume() { }
        /// <summary>
        /// called when this page close(pop from pages)
        /// </summary>
        virtual public void OnClose() {
            OnCloseEvent?.Invoke();
        }
        virtual public void Update()
        {

        }
        virtual public void OnGUI() { 
        
        }
    }
}

