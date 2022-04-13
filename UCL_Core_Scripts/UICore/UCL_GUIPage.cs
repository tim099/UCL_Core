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
        public UCL_GUIPageController p_Controller { get; protected set; } = null;
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
        /// called when this page push into stack
        /// </summary>
        virtual public void Init(UCL_GUIPageController iGUIPageController) {
            p_Controller = iGUIPageController;
        }
        /// <summary>
        /// called when this page became the TopPage
        /// </summary>
        virtual public void OnResume() { }

        virtual public void OnGUI() { }
    }
}

