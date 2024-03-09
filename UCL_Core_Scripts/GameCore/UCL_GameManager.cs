using Cysharp.Threading.Tasks;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;

namespace UCL.Core.Game {
#if UNITY_EDITOR
    [ATTR.EnableUCLEditor]
#endif
    public class UCL_GameManager : UCL_Singleton<UCL_GameManager>
    {
        //public static UCL_GameManager Instance { get; protected set; }
        public static bool IsExitGame { get
            {
                if (m_Destroyed) return true;
                if (GetInstance() == null) return false;
                return GetInstance().m_IsExitGame;
            } 
        }
        public static void RemoveExitGameFlag(string iFlag)
        {
            if (GetInstance() == null) return;
            GetInstance().m_BlockExitGameFlag.Remove(iFlag);
        }
        public static void AddExitGameFlag(string iFlag)
        {
            if (GetInstance() == null) return;
            GetInstance().m_BlockExitGameFlag.Add(iFlag);
        }
#if UNITY_EDITOR_WIN
        [ATTR.UCL_FunctionButton]
        public void Editor_OpenGameFolder() {
            FileLib.WindowsLib.OpenExplorer(GetGameFolderPath());
        }
#endif
#if UNITY_EDITOR
        /// <summary>
        /// Refresh all game data
        /// </summary>
        [ATTR.UCL_FunctionButton]
        virtual public void RefreshGamedata(){ }
#endif
        public static bool f_ApplicationQuit = false;

        public UnityEngine.Events.UnityEvent m_ExitGameEvent = new UnityEngine.Events.UnityEvent();
        protected HashSet<string> m_BlockExitGameFlag = new HashSet<string>();
        protected bool m_IsExitGame { get; set; } = false;
        public int m_ExitGameMaxWaitFrame = 1000;

        public string m_GameName = "UCL";
        public UCL_GameConfig m_GameConfig { get; protected set; }
        [SerializeField] protected List<UCL_GameService> m_GameServices;
        [SerializeField] protected bool m_InitOnAwake = true;
        protected bool m_Inited = false;
        protected bool m_End = false;
        [RuntimeInitializeOnLoadMethod]
        static void RuntimeInitializeInit() {
            //UCL_GameManager.Get();
        }
        virtual protected void Awake() {
            if (m_InitOnAwake)
            {
                Init();
            }
        }
        virtual public async UniTask InitAsync()
        {
            CancellationToken aToken = gameObject.GetCancellationTokenOnDestroy();
            await UnityEngine.AddressableAssets.Addressables.InitializeAsync();

            Init();

            foreach (var aService in m_GameServices)
            {
                try
                {
                    await aService.InitAsync(aToken);
                }
                catch (System.Exception e)
                {
                    Debug.LogError(aService.name + ".InitAsync() Exception:" + e);
                    Debug.LogException(e);
                }
                
            }
        }
        virtual public void Init() {
            if(m_Inited) return;
            if(!SetInstance(this)) return;
            //Instance = this;

            m_Inited = true;
            GetGameFolderPath();
            if (m_GameServices == null) m_GameServices = new List<UCL_GameService>();

#if UNITY_EDITOR
            Core.EditorLib.UCL_EditorPlayStateNotifier.AddExitingPlayModeAct(
            ()=> {
                //Debug.LogWarning("UCL_GameManager ExitingPlayMode");
                GameExit();
            });
#endif
            foreach (var service in m_GameServices)
            {
                try
                {
                    service.SetGameManager(this);
                    service.Init();
                }
                catch (System.Exception e)
                {
                    Debug.LogError(service.name + ".Init() Exception:" + e);
                    Debug.LogException(e);
                }

            }
            foreach (var aService in m_GameServices)
            {
                try
                {
                    aService.InitEnd();
                }
                catch (System.Exception e)
                {
                    Debug.LogError(aService.name + ".InitEnd() Exception:" + e);
                }

            }


            CreateGameConfig();
            m_GameConfig.Init();

            LoadAllSetting();
        }
        /// <summary>
        /// Override this function to customize GameConfig
        /// </summary>
        virtual protected void CreateGameConfig() {
            m_GameConfig = new UCL_GameConfig();
        }
        #region Save & Load
        virtual public void SaveAllSetting() {
            SaveGameConfig();

            var aGameServicePath = GetGameServicePath();

            foreach(var aService in m_GameServices) {
                try
                {
                    aService.Save(aGameServicePath);
                }
                catch (System.Exception iE)
                {
                    Debug.LogException(iE);
                }
            }
        }
        virtual public void LoadAllSetting() {
            LoadGameConfig();

            var aGameServicePath = GetGameServicePath();
            FileLib.Lib.CreateDirectory(aGameServicePath);

            foreach(var aService in m_GameServices) {
                try
                {
                    aService.Load(aGameServicePath);
                }
                catch(System.Exception iE)
                {
                    Debug.LogException(iE);
                }
            }
        }

        virtual public void SaveGameConfig() {
            if(m_GameConfig == null) {
                Debug.LogError("SaveGameConfig() m_GameConfig == null");
                return;
            }
            var aConfigPath = GetGameConfigPath();
            File.WriteAllText(aConfigPath, m_GameConfig.Save());
        }
        virtual public void LoadGameConfig() {
            var config_path = GetGameConfigPath();
            if(File.Exists(config_path)) {
                m_GameConfig.Load(File.ReadAllText(config_path));
            }
        }
        #endregion

        #region FolderPath
        virtual public string GetGameFolderPath() {
            string aPath = Path.Combine(FileLib.Lib.GetFilesPath(), m_GameName);
            FileLib.Lib.CreateDirectory(aPath);
            return aPath;
        }
        virtual public string GetGameConfigPath() {
            return Path.Combine(GetGameFolderPath(), "GameConfig.txt");
        }
        virtual public string GetGameServicePath() {
            string aPath = Path.Combine(GetGameFolderPath(), "GameService");
            FileLib.Lib.CreateDirectory(aPath);
            return aPath;
        }
        #endregion
        /// <summary>
        /// Called when OnDestroy
        /// </summary>
        virtual protected void GameExit() {
            if(!m_Inited || m_End) return;
            m_End = true;
            SaveAllSetting();
        }
        virtual protected void OnApplicationQuit() {
            //Debug.LogWarning("UCL_GameManager OnApplicationQuit()");
            f_ApplicationQuit = true;
        }
        virtual protected void OnApplicationPause(bool pauseStatus) {
            //pauseStatus == true if paused
        }
        override protected void OnDestroy() {
            GameExit();
            //Debug.LogWarning("UCL_GameManager OnDestroy()");
        }
        virtual public void ExitGame() {
            Debug.LogWarning("ExitGame()");
            if(m_IsExitGame) return;
            m_IsExitGame = true;
            m_ExitGameEvent?.Invoke();
            StartCoroutine(ExitGameCoroutine());
        }
        IEnumerator ExitGameCoroutine() {
            int aWaitFrame = 0;
            while(m_BlockExitGameFlag.Count > 0 && aWaitFrame < m_ExitGameMaxWaitFrame) {
                aWaitFrame++;
                yield return null;
            }

            yield return new WaitForSeconds(0.5f);

            Application.Quit();
        }
    }

}

