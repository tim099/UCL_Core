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
        public bool IsBlockCanvas => m_Pages.IsNullOrEmpty() ? false : TopPage.IsBlockCanvas;
        public Color BlockCanvasColor => m_Pages.IsNullOrEmpty() ? Color.clear : TopPage.BlockCanvasColor;
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
        public bool Update()
        {
            var aPage = TopPage;
            if (aPage == null) return false;
            aPage.Update();
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
            TopPage.OnClose();
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
            while (!m_Pages.IsNullOrEmpty())
            {
                Pop();
            }
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