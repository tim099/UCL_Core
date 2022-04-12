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
        #region static
        protected static List<UCL_GUIPage> Pages
        {
            get
            {
                if (s_Pages == null) s_Pages = new List<UCL_GUIPage>();
                return s_Pages;
            }
        }
        protected static List<UCL_GUIPage> s_Pages = null;
        public static UCL_GUIPage TopPage => s_Pages.IsNullOrEmpty() ? null : s_Pages.LastElement();
        /// <summary>
        /// Draw page on gui
        /// </summary>
        /// <returns>return true if pages not empty</returns>
        public static bool DrawOnGUI()
        {
            var aPage = TopPage;
            if (aPage == null) return false;
            aPage.OnGUI();
            return true;
        }
        /// <summary>
        /// push a page
        /// </summary>
        /// <param name="iPage"></param>
        public static void Push(UCL_GUIPage iPage)
        {
            if (!Pages.IsNullOrEmpty())
            {
                Pages.LastElement().OnPause();
            }
            iPage.Init();
            Pages.Add(iPage);
        }
        /// <summary>
        /// pop a page
        /// </summary>
        public static void Pop()
        {
            if (Pages.IsNullOrEmpty()) return;
            Pages.RemoveLast();
            if (!Pages.IsNullOrEmpty())
            {
                Pages.LastElement().OnResume();
            }
        }
        /// <summary>
        /// pop pages until iTarget become the top page
        /// </summary>
        /// <param name="iTarget"></param>
        public static void PopUntil(UCL_GUIPage iTarget)
        {
            while (!Pages.IsNullOrEmpty() && TopPage != iTarget)
            {
                Pop();
            }
        }
        /// <summary>
        /// Clear all pages
        /// </summary>
        public static void PopAll()
        {
            if (Pages.IsNullOrEmpty())
            {
                return;
            }
            Pages.Clear();
        }
        #endregion
        /// <summary>
        /// called when new page replace this page and became the TopPage
        /// </summary>
        virtual public void OnPause() { }
        /// <summary>
        /// called when this page push into stack
        /// </summary>
        virtual public void Init() { }
        /// <summary>
        /// called when this page became the TopPage
        /// </summary>
        virtual public void OnResume() { }

        virtual public void OnGUI() { }
    }
}

