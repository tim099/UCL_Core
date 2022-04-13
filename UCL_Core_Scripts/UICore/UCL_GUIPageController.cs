using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UCL.Core.UI
{
    public class UCL_GUIPageController
    {
        public static UCL_GUIPageController Ins
        {
            get
            {
                if (_Ins == null) _Ins = new UCL_GUIPageController();
                return _Ins;
            }
            set
            {
                _Ins = value;
            }
        }
        private static UCL_GUIPageController _Ins = null;
        protected List<UCL_GUIPage> m_Pages = new List<UCL_GUIPage>();
        public List<UCL_GUIPage> Pages => m_Pages;
        public UCL_GUIPage TopPage => m_Pages.IsNullOrEmpty() ? null : m_Pages.LastElement();
        /// <summary>
        /// Draw page on gui
        /// </summary>
        /// <returns>return true if pages not empty</returns>
        public bool DrawOnGUI()
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
        public void Push(UCL_GUIPage iPage)
        {
            if (!m_Pages.IsNullOrEmpty())
            {
                m_Pages.LastElement().OnPause();
            }
            iPage.Init(this);
            m_Pages.Add(iPage);
        }
        /// <summary>
        /// pop a page
        /// </summary>
        public void Pop()
        {
            if (m_Pages.IsNullOrEmpty()) return;
            m_Pages.RemoveLast();
            if (!m_Pages.IsNullOrEmpty())
            {
                m_Pages.LastElement().OnResume();
            }
        }
        /// <summary>
        /// pop pages until iTarget become the top page
        /// </summary>
        /// <param name="iTarget"></param>
        public void PopUntil(UCL_GUIPage iTarget)
        {
            while (!m_Pages.IsNullOrEmpty() && TopPage != iTarget)
            {
                Pop();
            }
        }
        /// <summary>
        /// Clear all pages
        /// </summary>
        public void PopAll()
        {
            if (m_Pages.IsNullOrEmpty())
            {
                return;
            }
            m_Pages.Clear();
        }
        /// <summary>
        /// Remove iPage from pages
        /// </summary>
        /// <param name="iPage"></param>
        public void Remove(UCL_GUIPage iPage)
        {
            m_Pages.Remove(iPage);
        }
    }
}