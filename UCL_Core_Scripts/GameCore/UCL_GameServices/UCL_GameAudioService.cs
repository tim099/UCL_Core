using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.IO;
using System.Runtime.InteropServices;
using UCL.Core.JsonLib;
namespace UCL.Core.Game {
    public enum AudioType
    {
        None = 0,
        /// <summary>
        /// Sound Effect
        /// </summary>
        SE,
        /// <summary>
        /// Back Ground Music
        /// </summary>
        BGM,
        /// <summary>
        /// Voice
        /// </summary>
        Voice,
    }
    public class AudioPlayer
    {
        public AudioPlayer(AudioSource iSource)
        {
            m_AudioSource = iSource;
        }
        public void Play(AudioClip iClip, float iVolume = 1f)
        {
            m_IsPlaying = true;
            m_AudioSource.gameObject.SetActive(true);
            m_AudioSource.clip = iClip;
            m_AudioSource.Play();
        }
        public void Stop()
        {
            if (!m_IsPlaying)
            {
                return;
            }
            m_IsPlaying = false;
            m_AudioSource.Stop();
            m_AudioSource.gameObject.SetActive(false);
            m_AudioSource.clip = null;
            if (m_EndAct != null)
            {
                m_EndAct.Invoke();
            }
            m_EndAct = null;
            var Ins = UCL_GameAudioService.Instance;
            if (Ins != null)
            {
                Ins.PlayEnd(this);
            }
        }
        public void SetEndAct(System.Action iAct)
        {
            m_EndAct = iAct;
        }
        /// <summary>
        /// Only update when IsPlaying
        /// </summary>
        internal void Update()
        {
            if (!m_AudioSource.isPlaying || m_AudioSource.clip == null)
            {
                Stop();
            }
        }
        System.Action m_EndAct = null;
        bool m_IsPlaying = true;
        public AudioSource m_AudioSource;
    }
    public class UCL_GameAudioService : UCL_GameService {
        [System.Serializable]
        public class VolumeSetting {
            /// <summary>
            /// Base Volume
            /// </summary>
            public float m_Volume = 1f;

            /// <summary>
            /// Sound Effect
            /// </summary>
            public float m_SE = 1f;

            /// <summary>
            /// Voice
            /// </summary>
            public float m_Voice = 1f;
        }

        public VolumeSetting m_VolumeSetting = new VolumeSetting();// { get; protected set; } 
        /// <summary>
        /// Active players
        /// </summary>
        List<AudioPlayer> m_ActivePlayers = new List<AudioPlayer>();
        /// <summary>
        /// Avaliable Players
        /// </summary>
        Queue<AudioPlayer> m_AvaliablePlayers = new Queue<AudioPlayer>();


        public static UCL_GameAudioService Instance { get; protected set; }
        override public void Init() {
            Instance = this;
        }
        public AudioPlayer GetAudioPlayer()
        {
            AudioPlayer aPlayer = null;
            if (m_AvaliablePlayers.Count == 0)
            {
                AudioSource aSource = UCL.Core.GameObjectLib.Create<AudioSource>("AudioPlayer(" + (m_ActivePlayers.Count + 1) + ")", transform);
                aPlayer = new AudioPlayer(aSource);
            }
            else
            {
                aPlayer = m_AvaliablePlayers.Dequeue();
            }

            m_ActivePlayers.Add(aPlayer);
            return aPlayer;
        }
        /// <summary>
        /// Play the audioClip
        /// </summary>
        /// <param name="iClip">AudioClip</param>
        /// <param name="iVolume">Volume</param>
        /// <returns></returns>
        public AudioPlayer Play(AudioClip iClip, float iVolume = 1f)
        {
            var aPlayer = GetAudioPlayer();
            aPlayer.Play(iClip, iVolume);

            return aPlayer;
        }
        /// <summary>
        /// Play Sound Effect
        /// </summary>
        /// <param name="iClip"></param>
        /// <param name="iVolume"></param>
        /// <returns></returns>
        public AudioPlayer PlaySE(AudioClip iClip, float iVolume = 1f)
        {
            var aPlayer = GetAudioPlayer();
            aPlayer.Play(iClip, iVolume);

            return aPlayer;
        }
        public AudioPlayer PlayBGM(AudioClip iClip, float iVolume = 1f)
        {
            var aPlayer = GetAudioPlayer();
            aPlayer.Play(iClip, iVolume);

            return aPlayer;
        }
        public AudioPlayer PlayVoice(AudioClip iClip, float iVolume = 1f)
        {
            var aPlayer = GetAudioPlayer();
            aPlayer.Play(iClip, iVolume);

            return aPlayer;
        }
        public AudioPlayer Play(AudioClip iClip, AudioType iType, float iVolume = 1f)
        {
            switch (iType)
            {
                case AudioType.SE: return PlaySE(iClip, iVolume);
                case AudioType.BGM: return PlayBGM(iClip, iVolume);
                case AudioType.Voice: return PlayVoice(iClip, iVolume);
            }
            return Play(iClip, iVolume);
        }

        /// <summary>
        /// Play end and recycle player
        /// </summary>
        /// <param name="iPlayer"></param>
        internal void PlayEnd(AudioPlayer iPlayer)
        {
            m_ActivePlayers.Remove(iPlayer);
            m_AvaliablePlayers.Enqueue(iPlayer);
        }
        public override void Save(string iDir) {
            try
            {
                string aPath = Path.Combine(iDir, "VolumeSetting.txt");
                var aJson = JsonConvert.SaveDataToJson(m_VolumeSetting);
                string aJsonText = aJson.ToJson();
                Debug.LogWarning("Save aJsonText:" + aJsonText);
                File.WriteAllText(aPath, aJsonText);
            }
            catch (System.Exception iException)
            {
                Debug.LogError("UCL_GameAudioService Save:" + iException);
            }

            //Core.FileLib.Lib.SerializeToFile(path, m_VolumeSetting);
        }
        public override void Load(string iDir) {
            try
            {
                string aPath = Path.Combine(iDir, "VolumeSetting.txt");// + ".txt"
                if (!File.Exists(aPath))
                {
                    return;
                }
                //m_VolumeSetting = Core.FileLib.Lib.DeserializeFromFile<VolumeSetting>(path);
                string aJsonText = File.ReadAllText(aPath);
                Debug.LogWarning($"Load aJsonText:{aJsonText}");
                m_VolumeSetting = (VolumeSetting)JsonConvert.LoadDataFromJson(m_VolumeSetting, JsonData.ParseJson(aJsonText));
            }
            catch(System.Exception iException)
            {
                m_VolumeSetting = new VolumeSetting();
                Debug.LogError("UCL_GameAudioService Load:" + iException);
            }

        }
        private void Update()
        {
            for(int i = 0; i < m_ActivePlayers.Count; i++)
            {
                m_ActivePlayers[i].Update();
            }
        }
    }
}