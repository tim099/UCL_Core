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
    /// <summary>
    /// Stack 中單筆 UI 的描述 — 給 agent / debug tool 反射查詢用.
    /// 區塊職責：UI 反射 metadata 容器；序列化友善（純 POCO，無 Unity 物件 ref）
    /// 物理意義：對應 m_UIStack 內某 UCL_GameUI 的快照；agent 看 PublicMethods 知道可呼叫什麼
    /// 數值影響：純資料容器，不影響任何 UI state
    /// </summary>
    [Serializable]
    public class UCL_UIStackEntryInfo
    {
        public int StackIndex;
        public bool IsTop;
        public string TypeFullName;
        public string TypeShortName;
        public string GameObjectName;
        public bool IsActive;
        public int Layer;
        public bool IsFullScreen;
        public bool IsUIOverlay;
        public List<string> PublicMethods = new List<string>();
        public List<string> ProtectedMethods = new List<string>();
    }

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
            UCL_ModuleService.OnLoadModule += OnLoadModule;
        }
        private void OnDestroy()
        {
            UCL_ModuleService.OnLoadModule -= OnLoadModule;
        }
        virtual protected void OnLoadModule()
        {
            foreach(var key in m_UIPools.Keys)
            {
                var pool = m_UIPools[key];
                while (!pool.IsNullOrEmpty())
                {
                    var ui = pool.Dequeue();
                    if(ui != null)
                    {
                        GameObject.Destroy(ui.gameObject);
                    }
                }
            }
            m_UIPools.Clear();
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
            T ui = null;
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
                        ui = aPool.Dequeue() as T;
                        if(ui != null)//Create from pool success
                        {
                            //iUI.transform.SetParent(aParent, false);
                            ui.transform.SetAsLastSibling();//To Top
                            ui.transform.localPosition = Vector3.zero;
                            ui.transform.localScale = Vector3.one;
                            break;
                        }
                    }

                }

                if(ui == null)
                {
                    ui = Instantiate(iTemplate, aParent);
                }

                if (!m_UIStack.IsNullOrEmpty())
                {
                    if (ui.IsFullScreen)
                    {
                        m_UIStack.LastElement().OnCovered();//UI is covered by another UI
                    }
                }

                m_UIStack.Add(ui);

                ui.OnTop();
                ui.Init();
            }
            catch (System.Exception iE)
            {
                Debug.LogException(iE);
            }

            return ui;
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
            if (!m_UIStack.IsNullOrEmpty())
            {
                m_UIStack.LastElement().OnTop();
            }
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
        /// Read-only access to the UI stack (top = last).
        /// 區塊職責：給外部 agent / debug tool 公開讀取 UI 堆疊；不允許外部修改
        /// 物理意義：UI 顯示順序對應 Stack — 最新 push 的 UI 在最頂端
        /// 數值影響：純讀取，不影響堆疊；Cmd_UIInspect / Cmd_UIInvoke 等 agent cmd 用此 query
        /// </summary>
        public IReadOnlyList<UCL_GameUI> UIStack => m_UIStack;

        /// <summary>
        /// 取得 UI stack 摘要 — 給 agent 透過反射列出可用 method (action) 用.
        /// 區塊職責：對每筆 UI 抽出 type name / GameObject 名稱 / Layer / IsTop / IsFullScreen
        ///           以及該 type 上 public + protected 的 instance method（可被 Cmd_UIInvoke 呼叫）
        /// 物理意義：agent 知道現在頂層是 RCG_BattleEndUI → 看到 Next/ShowSelectCards/Finish 三個可呼叫
        ///           method → 可決定下一步 action
        /// 數值影響：純讀取，不修改任何 UI 狀態；method list 跟 type 綁定，不依當前 instance state
        /// 安全：非 Editor build / Unity 端用 reflection 反射不會崩，但 method invoke 自負風險
        ///       (private / 內部使用 method 不該對外暴露 — 由 Cmd_UIInvoke 端或 UI 作者自律)
        /// </summary>
        public List<UCL_UIStackEntryInfo> GetUIStackInfo(bool iIncludeMethods = true)
        {
            var aResult = new List<UCL_UIStackEntryInfo>();
            for (int i = 0; i < m_UIStack.Count; i++)
            {
                var aUI = m_UIStack[i];
                if (aUI == null) continue;
                var aType = aUI.GetType();
                var aEntry = new UCL_UIStackEntryInfo
                {
                    StackIndex = i,
                    IsTop = (i == m_UIStack.Count - 1),
                    TypeFullName = aType.FullName,
                    TypeShortName = aType.Name,
                    GameObjectName = aUI.gameObject != null ? aUI.gameObject.name : "(null)",
                    IsActive = aUI.gameObject != null && aUI.gameObject.activeInHierarchy,
                    Layer = aUI.Layer,
                    IsFullScreen = aUI.IsFullScreen,
                    IsUIOverlay = aUI.IsUIOverlay,
                };
                if (iIncludeMethods)
                {
                    aEntry.PublicMethods = ListInvokableMethods(aType, includeProtected: false);
                    aEntry.ProtectedMethods = ListInvokableMethods(aType, includeProtected: true);
                }
                aResult.Add(aEntry);
            }
            return aResult;
        }

        /// <summary>
        /// 列出 type 上可被 reflection 呼叫的 instance method（無參數或全 default 參數）.
        /// 物理意義：給 Cmd_UIInvoke 篩選候選 — 沒參數的 method 最容易直接呼叫
        ///           （帶參數的得 Cmd 額外傳，先排除）
        /// 過濾規則：
        ///   - 排除 GetType / ToString / Equals / GetHashCode / CompareTo / Finalize 等 Object base
        ///   - 排除 property getter/setter (get_X / set_X)
        ///   - 排除 Unity callback (Update / Awake / Start / OnDestroy / OnEnable / OnDisable)
        ///   - 只回 instance method，不回 static
        /// </summary>
        private static List<string> ListInvokableMethods(Type iType, bool includeProtected)
        {
            var aResult = new List<string>();
            if (iType == null) return aResult;
            var aFlags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                       | System.Reflection.BindingFlags.DeclaredOnly;
            if (includeProtected)
            {
                aFlags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
                       | System.Reflection.BindingFlags.DeclaredOnly;
            }
            // 走 type hierarchy 從子 → 父（避開 Object/MonoBehaviour 之外的）
            var aCurType = iType;
            var aSeen = new HashSet<string>();
            while (aCurType != null && aCurType != typeof(UnityEngine.MonoBehaviour) && aCurType != typeof(object))
            {
                var aMethods = aCurType.GetMethods(aFlags);
                foreach (var m in aMethods)
                {
                    if (m.IsSpecialName) continue;                  // skip get_X / set_X
                    if (aSeen.Contains(m.Name)) continue;
                    if (m.GetParameters().Length > 0)
                    {
                        // 帶參數的不列入「無參簡呼叫」清單；後期 Cmd_UIInvoke 加 param 支援可放寬
                        continue;
                    }
                    if (IsBlacklistedMethod(m.Name)) continue;
                    aSeen.Add(m.Name);
                    aResult.Add(m.Name);
                }
                aCurType = aCurType.BaseType;
            }
            return aResult;
        }

        private static readonly HashSet<string> s_MethodBlacklist = new HashSet<string>
        {
            // Unity callbacks (lifecycle, agent 不該手動 fire)
            "Update", "FixedUpdate", "LateUpdate", "Awake", "Start", "OnDestroy",
            "OnEnable", "OnDisable", "Reset", "OnValidate", "OnDrawGizmos",
            // Object / MonoBehaviour base
            "ToString", "Equals", "GetHashCode", "GetType", "Finalize",
            // UCL internal lifecycle (agent 用 OnEscape/Close 等再說)
            "MemberwiseClone",
        };

        private static bool IsBlacklistedMethod(string iName) => s_MethodBlacklist.Contains(iName);

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

