using Cysharp.Threading.Tasks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.AddressableAssets;


namespace UCL.Core.Game
{
    [UCL.Core.ATTR.EnableUCLEditor]
#if UNITY_EDITOR
    [UCL.Core.ATTR.RequiresConstantRepaint]
#endif
    public class UCL_UIService : UCL_GameService
    {
        #region static
        static public UCL_UIService Ins { get => s_Ins; protected set => s_Ins = value; }
        static public UCL_UIService s_Ins = null;
        /// <summary>
        /// Root folder of UI Resource
        /// </summary>
        static public string UIResourceFolder = string.Empty;
        #endregion

        




        [SerializeField] protected RectTransform m_UIRoot = null;
        [SerializeField] protected RectTransform m_UIOverlayRoot = null;
        [SerializeField] protected Canvas m_Canvas = null;

        protected Dictionary<int, RectTransform> m_UIRootLayer = new Dictionary<int, RectTransform>();
        protected Dictionary<int, RectTransform> m_UIOverlayRootLayer = new Dictionary<int, RectTransform>();
        protected Dictionary<Type, Queue<UCL_GameUI>> m_UIPools = new();
        //[SerializeField] Canvas m_UIOverlayCanvas = null;
        /// <summary>
        /// Use List to simulate Stack
        /// </summary>
        protected List<UCL_GameUI> m_UIStack = new List<UCL_GameUI>();

        virtual public RectTransform UIRoot => m_UIRoot;
        virtual protected string UIAddressablesPathFormat => "Assets/Addressables/UI/{0}.prefab";
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
        virtual protected RectTransform GetRoot(UCL_GameUI iTemplate)
        {
            RectTransform aRoot = iTemplate.IsUIOverlay ? m_UIOverlayRoot : m_UIRoot;
            var aLayerDic = iTemplate.IsUIOverlay ? m_UIOverlayRootLayer : m_UIRootLayer;
            int aLayer = iTemplate.Layer;
            if (!aLayerDic.ContainsKey(aLayer))
            {
                var aLayerObj = new GameObject($"UILayer_{aLayer}");
                
                var aRectTransform = aLayerObj.GetOrAddComponent<RectTransform>();
                aRectTransform.SetParent(aRoot);

                //aRectTransform.CopyValue(aRoot.GetOrAddComponent<RectTransform>());
                aRectTransform.localScale = Vector3.one;
                aRectTransform.anchorMin = Vector2.zero;
                aRectTransform.anchorMax = Vector2.one;
                aRectTransform.anchoredPosition3D = Vector3.zero;
                aRectTransform.sizeDelta = aRoot.sizeDelta;
                aLayerDic[aLayer] = aRectTransform;

                if(aLayerDic.Count > 1)
                {
                    var aKeys = aLayerDic.Keys.ToList();
                    aKeys.Sort();
                    foreach (var aKey in aKeys)
                    {
                        //Debug.LogError($"aKey:{aKey}");
                        aLayerDic[aKey].SetAsLastSibling();
                    }
                }

            }
            return aLayerDic[aLayer];
        }
        public T CreateUI<T>(T iTemplate) where T : UCL_GameUI
        {
            T iUI = null;
            try
            {
                var aType = iTemplate.GetType();//typeof(T);


                RectTransform aParent = GetRoot(iTemplate);
                //Debug.LogError($"CreateUI aType:{aType},m_UIPools.Keys:{m_UIPools.Keys.ConcatString(iKey => iKey.FullName)}");
                if (m_UIPools.ContainsKey(aType))
                {
                    var aPool = m_UIPools[aType];
                    while(aPool.Count > 0)
                    {
                        iUI = aPool.Dequeue() as T;
                        if(iUI != null)//Create from pool success
                        {
                            //iUI.transform.SetParent(aParent, false);
                            iUI.transform.SetAsLastSibling();//To Top
                            iUI.transform.localPosition = Vector3.zero;
                            iUI.transform.localScale = Vector3.one;
                            break;
                        }
                    }

                }

                if(iUI == null)
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

        virtual public async UniTask<UCL_GameUI> CreateUIFromAddressable(string iPath)
        {
            try
            {
                var aObject = await Addressables.LoadAssetAsync<GameObject>(iPath);
                if (aObject == null)
                {
                    Debug.LogError("CreateUIFromAddressable aObject == null iPath:" + iPath);
                    return null;
                }
                UCL_GameUI aTemplate = aObject.GetComponent<UCL_GameUI>();
                if (aTemplate == null)
                {
                    Debug.LogError("CreateUIFromAddressable aTemplate == null iPath:" + iPath);
                    return null;
                }
                return CreateUI(aTemplate);
            }
            catch (System.Exception iE)
            {
                Debug.LogException(iE);
            }

            return null;//fail to create
        }
        virtual public async UniTask<T> CreateUIFromAddressable<T>(string iPath = "") where T : UCL_GameUI
        {
            if (string.IsNullOrEmpty(iPath))
            {
                iPath = string.Format(UIAddressablesPathFormat, typeof(T).Name);// $"Assets/Addressables/UI/{typeof(T).Name}.prefab";
            }
            return await CreateUIFromAddressable(iPath) as T;
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

