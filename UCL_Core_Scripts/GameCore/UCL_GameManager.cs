using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace UCL.Core.Game {
#if UNITY_EDITOR
    [ATTR.EnableUCLEditor]
#endif
    public class UCL_GameManager : UCL_Singleton<UCL_GameManager> {

#if UNITY_EDITOR
        [ATTR.UCL_FunctionButton]
        public void Editor_OpenGameFolder() {
            string folder = GetGameFolderPath();
            FileLib.Lib.CreateDirectory(folder);//FileLib.Lib.GetFilesPath(true);

            FileLib.EditorLib.ExploreFile(folder);
        }
#endif

        public static bool f_ApplicationQuit = false;

        public UnityEngine.Events.UnityEvent m_ExitGameEvent = new UnityEngine.Events.UnityEvent();
        public HashSet<string> m_BlockExitGameFlag = new HashSet<string>();
        public bool f_ExitGame { get; protected set; } = false;
        public int m_ExitGameMaxWaitFrame = 1000;

        public string m_GameName = "UCL";
        public UCL_GameConfig m_GameConfig { get; protected set; }

        protected bool f_Inited = false;

        [RuntimeInitializeOnLoadMethod]
        static void RuntimeInitializeInit() {
            //UCL_GameManager.Get();
        }

        virtual protected void Init() {
            if(f_Inited) return;
            f_Inited = true;
            if(!SetInstance(this)) return;
            
            FileLib.Lib.CreateDirectory(GetGameFolderPath());
            CreateGameConfig();
            m_GameConfig.Init();

            LoadGameConfig();
            //m_GameConfig.Load();
        }
        /// <summary>
        /// overload this function to customize GameConfig
        /// </summary>
        virtual protected void CreateGameConfig() {
            m_GameConfig = new UCL_GameConfig();
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

        #region FolderPath
        virtual public string GetGameFolderPath() {
            return Path.Combine(FileLib.Lib.GetFilesPath(), m_GameName);
        }
        virtual public string GetGameConfigPath() {
            return Path.Combine(GetGameFolderPath(), "GameConfig.txt");
        }
        #endregion
        /// <summary>
        /// Called when OnDestroy
        /// </summary>
        virtual protected void GameExit() {
            SaveGameConfig();
        }
        private void Awake() {
            Init();
        }
        private void OnApplicationQuit() {
            Debug.LogWarning("UCL_GameManager OnApplicationQuit()");
            f_ApplicationQuit = true;
        }
        protected override void OnDestroy() {
            base.OnDestroy();
            GameExit();
            Debug.LogWarning("UCL_GameManager OnDestroy()");
        }
        virtual public void ExitGame() {
            Debug.LogWarning("ExitGame()");
            if(f_ExitGame) return;
            f_ExitGame = true;
            m_ExitGameEvent?.Invoke();
            StartCoroutine(ExitGameCoroutine());
        }
        IEnumerator ExitGameCoroutine() {
            int n = 0;
            while(m_BlockExitGameFlag.Count > 0 && n < m_ExitGameMaxWaitFrame) {
                n++;
                yield return null;
            }

            yield return new WaitForSeconds(1.0f);

            Application.Quit();
        }
    }

}

