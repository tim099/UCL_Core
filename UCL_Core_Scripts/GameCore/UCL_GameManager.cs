using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace UCL.Core.Game {
#if UNITY_EDITOR
    [ATTR.EnableUCLEditor]
#endif
    public class UCL_GameManager : UCL_Singleton<UCL_GameManager> {

#if UNITY_EDITOR_WIN
        [ATTR.UCL_FunctionButton]
        public void Editor_OpenGameFolder() {
            string folder = GetGameFolderPath();
            FileLib.Lib.CreateDirectory(folder);//FileLib.Lib.GetFilesPath(true);

            FileLib.WindowsLib.OpenExplorer(folder);
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
        public HashSet<string> m_BlockExitGameFlag = new HashSet<string>();
        public bool f_ExitGame { get; protected set; } = false;
        public int m_ExitGameMaxWaitFrame = 1000;

        public string m_GameName = "UCL";
        public UCL_GameConfig m_GameConfig { get; protected set; }
        [SerializeField] protected List<UCL_GameService> m_GameServices;

        protected bool m_Inited = false;
        protected bool m_End = false;
        [RuntimeInitializeOnLoadMethod]
        static void RuntimeInitializeInit() {
            //UCL_GameManager.Get();
        }
        private void Awake() {
            Init();
        }
        virtual protected void Init() {
            if(m_Inited) return;
            if(!SetInstance(this)) return;

            m_Inited = true;
            if(m_GameServices == null) m_GameServices = new List<UCL_GameService>();
            FileLib.Lib.CreateDirectory(GetGameFolderPath());
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
                    service.Init();
                }
                catch (System.Exception e)
                {
                    Debug.LogError(service.name + ".Init() Exception:" + e);
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

            var gameservice_path = GetGameServicePath();
            FileLib.Lib.CreateDirectory(gameservice_path);

            foreach(var service in m_GameServices) {
                service.Save(gameservice_path);
            }
        }
        virtual public void LoadAllSetting() {
            LoadGameConfig();

            var gameservice_path = GetGameServicePath();
            FileLib.Lib.CreateDirectory(gameservice_path);

            foreach(var service in m_GameServices) {
                service.Load(gameservice_path);
            }
        }

        virtual public void SaveGameConfig() {
            if(m_GameConfig == null) {
                Debug.LogError("SaveGameConfig() m_GameConfig == null");
                return;
            }
            var config_path = GetGameConfigPath();
            File.WriteAllText(config_path, m_GameConfig.Save());
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
            return Path.Combine(FileLib.Lib.GetFilesPath(), m_GameName);
        }
        virtual public string GetGameConfigPath() {
            return Path.Combine(GetGameFolderPath(), "GameConfig.txt");
        }
        virtual public string GetGameServicePath() {
            return Path.Combine(GetGameFolderPath(), "GameService");
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
        protected override void OnDestroy() {
            base.OnDestroy();
            GameExit();
            //Debug.LogWarning("UCL_GameManager OnDestroy()");
        }
        virtual public void ExitGame() {
            Debug.LogWarning("ExitGame()");
            if(f_ExitGame) return;
            f_ExitGame = true;
            m_ExitGameEvent?.Invoke();
            StartCoroutine(ExitGameCoroutine());
        }
        IEnumerator ExitGameCoroutine() {
            int aWaitFrame = 0;
            while(m_BlockExitGameFlag.Count > 0 && aWaitFrame < m_ExitGameMaxWaitFrame) {
                aWaitFrame++;
                yield return null;
            }

            yield return new WaitForSeconds(1.0f);

            Application.Quit();
        }
    }

}

