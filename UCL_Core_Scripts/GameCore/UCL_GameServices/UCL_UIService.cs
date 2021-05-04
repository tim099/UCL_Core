﻿using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;


namespace UCL.Core.Game
{
    public class UCL_UIService : UCL_GameService
    {
        static public UCL_UIService Ins { get; protected set; }
        /// <summary>
        /// Root folder of UI Resource
        /// </summary>
        static public string UIResourceFolder = string.Empty;
        [SerializeField] RectTransform m_UIRoot = null;
        [SerializeField] RectTransform m_UIOverlayRoot = null;
        [SerializeField] Canvas m_Canvas = null;
        //[SerializeField] Canvas m_UIOverlayCanvas = null;
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
            T iUI = null;
            try
            {
                RectTransform aParent = iTemplate.IsUIOverlay ? m_UIOverlayRoot : m_UIRoot;
                iUI = Instantiate(iTemplate, aParent);
                m_UIStack.Add(iUI);
                iUI.Init();
            }
            catch (System.Exception iE)
            {
                Debug.LogException(iE);
            }

            return iUI;
        }
        public T CreateUIFromResource<T>() where T : UCL_GameUI
        {
            string aPath = typeof(T).Name;
            return CreateUIFromResource<T>(aPath);
        }
        public T CreateUIFromResource<T>(string iPath) where T : UCL_GameUI
        {
            if (!string.IsNullOrEmpty(UIResourceFolder))
            {
                iPath = Path.Combine(UIResourceFolder, iPath);
            }
            T aTemplate = Resources.Load<T>(iPath);
            if(aTemplate == null)
            {
                Debug.LogError("CreateUIFromResource aTemplate == null iPath:" + iPath);
                return null;
            }
            return CreateUI(aTemplate);
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
        public void SetCanvasCamera(Camera iCamera)
        {
            if (m_Canvas == null) return;
            if (m_Canvas.worldCamera != iCamera)
            {
                m_Canvas.worldCamera = iCamera;
            }
        }
        private void Update()
        {
            //if(m_Canvas.worldCamera != Camera.main)
            //{
            //    m_Canvas.worldCamera = Camera.main;
            //}
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                EscapeKeyDown();
            }
        }
    }
}

