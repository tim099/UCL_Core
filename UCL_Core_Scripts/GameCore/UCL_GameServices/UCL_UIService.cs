using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;


namespace UCL.Core.Game
{
    [UCL.Core.ATTR.EnableUCLEditor]
#if UNITY_EDITOR
    [UCL.Core.ATTR.RequiresConstantRepaint]
#endif
    public class UCL_UIService : UCL_GameService
    {
        static public UCL_UIService Ins { get => s_Ins; protected set => s_Ins = value; }
        static public UCL_UIService s_Ins = null;
        public RectTransform UIRoot { get => m_UIRoot; }
        /// <summary>
        /// Root folder of UI Resource
        /// </summary>
        static public string UIResourceFolder = string.Empty;
        [SerializeField] RectTransform m_UIRoot = null;
        [SerializeField] RectTransform m_UIOverlayRoot = null;
        [SerializeField] Canvas m_Canvas = null;
        Dictionary<Type, Queue<UCL_GameUI>> m_UIPools = new();
        //[SerializeField] Canvas m_UIOverlayCanvas = null;
        /// <summary>
        /// Use List to simulate Stack
        /// </summary>
        List<UCL_GameUI> m_UIStack = new List<UCL_GameUI>();
        public override void Init()
        {
            Ins = this;
        }

        [UCL.Core.ATTR.UCL_DrawOnGUI]
        public void DrawOnGUI(UCL.Core.UCL_ObjectDictionary iDic)
        {
            if (m_UIStack.IsNullOrEmpty()) return;
            for (int i = 0; i < m_UIStack.Count; i++)
            {
                GUILayout.Box(string.Format("{0}. {1}", i, m_UIStack[i].name));
            }
        }

        public T CreateUI<T>(T iTemplate) where T : UCL_GameUI
        {
            T iUI = null;
            try
            {
                var aType = typeof(T);


                RectTransform aParent = iTemplate.IsUIOverlay ? m_UIOverlayRoot : m_UIRoot;

                if (m_UIPools.ContainsKey(aType) && m_UIPools[aType].Count > 0)
                {
                    iUI = m_UIPools[aType].Dequeue() as T;

                    //iUI.transform.SetParent(aParent, false);
                    iUI.transform.SetAsLastSibling();
                    iUI.transform.localPosition = Vector3.zero;
                    iUI.transform.localScale = Vector3.one;
                }
                else
                {
                    iUI = Instantiate(iTemplate, aParent);
                }

                
                m_UIStack.Add(iUI);
                iUI.Init();
            }
            catch (System.Exception iE)
            {
                Debug.LogException(iE);
            }

            return iUI;
        }
        /// <summary>
        /// return 
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        public T GetUI<T>() where T : UCL_GameUI
        {
            foreach(var aUI in m_UIStack)
            {
                T aTarget = aUI as T;
                if (aTarget != null) return aTarget;
            }
            return null;
        }
        public T CreateUIFromResource<T>() where T : UCL_GameUI
        {
            string aPath = typeof(T).Name;
            return CreateUIFromResource<T>(aPath);
        }
        public UCL_GameUI CreateUIFromResource(string iPath)
        {
            return CreateUIFromResource<UCL_GameUI>(iPath);
        }
        public T CreateUIFromResource<T>(string iPath) where T : UCL_GameUI
        {
            if (!string.IsNullOrEmpty(UIResourceFolder))
            {
                iPath = Path.Combine(UIResourceFolder, iPath);
            }
            try
            {
                T aTemplate = Resources.Load<T>(iPath);
                if (aTemplate == null)
                {
                    Debug.LogError("CreateUIFromResource aTemplate == null iPath:" + iPath);
                    return null;
                }
                return CreateUI(aTemplate);
            }
            catch(System.Exception iE)
            {
                Debug.LogException(iE);
            }

            return default;//fail to create
        }
        /// <summary>
        /// Remove closed ui from UIStack
        /// </summary>
        /// <param name="iUI"></param>
        public void OnUIClosed(UCL_GameUI iUI)
        {
            if(iUI == null)
            {
                return;
            }
            if (!m_UIStack.Contains(iUI))
            {
                Debug.LogError("!m_UIStack.Contains:" + iUI.name);
            }
            m_UIStack.Remove(iUI);
            //Debug.LogError("OnCLose:" + iUI.name);
            if (iUI.gameObject != null)
            {
                if (iUI.Reusable)
                {
                    iUI.gameObject.SetActive(false);
                    //iUI.transform.SetParent(transform);
                    var aType = iUI.GetType();
                    //Debug.LogError($"OnUIClosed Reusable aType:{aType}");
                    if (!m_UIPools.ContainsKey(aType))
                    {
                        m_UIPools[aType] = new Queue<UCL_GameUI>();
                    }

                    iUI.OnRecycle();
                    m_UIPools[aType].Enqueue(iUI);
                }
                else
                {
                    Destroy(iUI.gameObject);
                }
            }

        }
        /// <summary>
        /// Close all UI
        /// </summary>
        public void CloseAllUI()
        {
            var aList = m_UIStack.Clone();
            for(int i = 0; i < aList.Count; i++)
            {
                aList[i].Close();
            }
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

