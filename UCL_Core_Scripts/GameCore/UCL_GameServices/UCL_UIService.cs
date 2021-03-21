using System.Collections;
using System.Collections.Generic;
using UnityEngine;


namespace UCL.Core.Game
{
    public class UCL_UIService : UCL_GameService
    {
        static public UCL_UIService Ins { get; protected set; }

        [SerializeField] RectTransform m_UIRoot = null;
        /// <summary>
        /// Use List to simulate Stack
        /// </summary>
        List<UCL_GameUI> m_UIStack = new List<UCL_GameUI>();
        public override void Init()
        {
            Ins = this;
        }
        public T CreateUI<T>(T iTemplate) where T : UCL_GameUI
        {
            T iUI = Instantiate(iTemplate, m_UIRoot);
            m_UIStack.Add(iUI);
            iUI.Init();
            return iUI;
        }
        public void CloseUI(UCL_GameUI iUI)
        {
            m_UIStack.Remove(iUI);
        }
        public void EscapeKeyDown()
        {
            if (m_UIStack.Count > 0)
            {
                m_UIStack.LastElement().OnEscape();
            }
        }
        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                EscapeKeyDown();
            }
        }
    }
}

