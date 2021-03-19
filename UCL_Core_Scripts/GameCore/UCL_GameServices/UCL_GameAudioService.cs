using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.IO;
using System.Runtime.InteropServices;
using UCL.Core.JsonLib;
using UnityEngine.Audio;

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
        /// <summary>
        /// Root of All AudioGroup
        /// </summary>
        Master,
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
        const string SaveName = "VolumeSetting.txt";
        public float BGMVolume { 
            get { return m_VolumeSetting.m_BGM; }
            set {
                m_VolumeSetting.m_BGM = value;
                SetMixerVolume(AudioType.BGM, value);
            }
        }
        public float Volume
        {
            get { return m_VolumeSetting.m_Volume; }
            set
            {
                m_VolumeSetting.m_Volume = value;
                SetMixerVolume(AudioType.Master, value);
            }
        }
        public float SEVolume
        {
            get { return m_VolumeSetting.m_SE; }
            set
            {
                m_VolumeSetting.m_SE = value;
                SetMixerVolume(AudioType.SE, value);
            }
        }
        public float VoiceVolume
        {
            get { return m_VolumeSetting.m_Voice; }
            set
            {
                m_VolumeSetting.m_Voice = value;
                SetMixerVolume(AudioType.Voice, value);
            }
        }
        public class BGMData
        {
            public BGMData(AudioClip iClip, float iVolume)
            {
                m_Clip = iClip;
                m_Volume = iVolume;
            }
            public AudioClip m_Clip = null;
            public float m_Volume = 1f;
        }
        [System.Serializable]
        public class VolumeSetting {
            /// <summary>
            /// Base Volume
            /// </summary>
            public float m_Volume = 0.5f;

            /// <summary>
            /// Sound Effect
            /// </summary>
            public float m_SE = 0.5f;

            /// <summary>
            /// Voice
            /// </summary>
            public float m_Voice = 0.5f;

            /// <summary>
            /// Back Ground Music
            /// </summary>
            public float m_BGM = 0.5f;
        }

        public VolumeSetting m_VolumeSetting = new VolumeSetting();// { get; protected set; } 
        [SerializeField] AudioMixer m_AudioMixer = null;

        /// <summary>
        /// Active players
        /// </summary>
        List<AudioPlayer> m_ActivePlayers = new List<AudioPlayer>();
        /// <summary>
        /// Avaliable Players
        /// </summary>
        Queue<AudioPlayer> m_AvaliablePlayers = new Queue<AudioPlayer>();

        BGMData m_CurBGM = null;
        Stack<BGMData> m_BGMStack = new Stack<BGMData>();
        AudioSource m_BGMSource = null;
        Dictionary<AudioType, AudioMixerGroup> m_AudioMixerGroupDic = new Dictionary<AudioType, AudioMixerGroup>();
        public static UCL_GameAudioService Instance { get; protected set; }
        /// <summary>
        /// Just Loaded the setting, need to refresh AudioMixer On Update!!
        /// </summary>
        bool m_Loaded = false;
        override public void Init() {
            Instance = this;
            if (m_AudioMixer == null) m_AudioMixer = Resources.Load<AudioMixer>("UCL_AudioMixer");
            foreach(AudioType aType in System.Enum.GetValues(typeof(AudioType)))
            {
                m_AudioMixerGroupDic.Add(aType, m_AudioMixer.FindMatchingGroups(aType.ToString())[0]);
            }
            

            m_BGMSource = UCL.Core.GameObjectLib.Create<AudioSource>("BGM", transform);
            m_BGMSource.loop = true;
            m_BGMSource.outputAudioMixerGroup = m_AudioMixerGroupDic[AudioType.BGM];
            //Debug.LogWarning("Init() GameAudioService");
        }
        private void SetMixerVolume(AudioType iAudioType, float iVal)
        {
            SetMixerVolume(iAudioType.ToString(), iVal);
        }
        private void SetMixerVolume(string aGroupName, float iVal)
        {
            float aVolume = 0;
            if (iVal < 0.001f)
            {
                aVolume = -80f;
            }
            else
            {
                aVolume = 30f * Mathf.Log10(iVal) + 9f;//Mathf.Pow(10f, 0.05f * iVal);
            }
            //Debug.LogWarning("iVal:" + iVal + ",aVolume:" + aVolume);
            if (aVolume < -80) aVolume = -80;
            m_AudioMixer.SetFloat(aGroupName, aVolume);
        }

        public AudioPlayer GetAudioPlayer(AudioType iAudioType)
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
            aPlayer.m_AudioSource.outputAudioMixerGroup = m_AudioMixerGroupDic[iAudioType];
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
            var aPlayer = GetAudioPlayer(AudioType.None);
            aPlayer.Play(iClip, iVolume);

            return aPlayer;
        }
        /// <summary>
        /// Play new BGM and push old BGM into stack
        /// </summary>
        /// <param name="iClip"></param>
        /// <param name="iVolume"></param>
        public void PushBGM(AudioClip iClip, float iVolume = 1f)
        {
            if (m_BGMSource.clip != null)
            {
                m_BGMStack.Push(new BGMData(m_BGMSource.clip, m_BGMSource.volume));
            }
            PlayBGM(iClip, iVolume);
        }
        /// <summary>
        /// Pop current BGM and Play previous BGM in stack
        /// </summary>
        public void PopBGM()
        {
            StopBGM();
            if (m_BGMStack.Count > 0)
            {
                var aBGM = m_BGMStack.Pop();
                PlayBGM(aBGM.m_Clip, aBGM.m_Volume);
            }
        }
        public void StopBGM()
        {
            if(m_BGMSource == null)
            {
                return;
            }
            m_BGMSource.clip = null;
            m_BGMSource.Stop();
            m_CurBGM = null;
        }
        public AudioPlayer PlayBGM(AudioClip iClip, float iVolume = 1f)
        {
            if(m_BGMSource == null)
            {
                return null;
            }
            m_CurBGM = new BGMData(iClip, iVolume);

            m_BGMSource.clip = iClip;
            m_BGMSource.volume = iVolume;//* BGMVolume
            m_BGMSource.Play();
            return null;
        }
        /// <summary>
        /// Play Sound Effect
        /// </summary>
        /// <param name="iClip"></param>
        /// <param name="iVolume"></param>
        /// <returns></returns>
        public AudioPlayer PlaySE(AudioClip iClip, float iVolume = 1f)
        {
            var aPlayer = GetAudioPlayer(AudioType.SE);
            aPlayer.Play(iClip, iVolume);

            return aPlayer;
        }

        public AudioPlayer PlayVoice(AudioClip iClip, float iVolume = 1f)
        {
            var aPlayer = GetAudioPlayer(AudioType.Voice);
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
                string aPath = Path.Combine(iDir, SaveName);
                var aJson = JsonConvert.SaveDataToJson(m_VolumeSetting);
                string aJsonText = aJson.ToJson();
                //Debug.LogWarning("Save aJsonText:" + aJsonText);
                File.WriteAllText(aPath, aJsonText);
            }
            catch (System.Exception iException)
            {
                Debug.LogError("UCL_GameAudioService Save:" + iException);
            }

            //Core.FileLib.Lib.SerializeToFile(path, m_VolumeSetting);
        }
        public override void Load(string iDir) {
            //Debug.LogWarning("Load VolumeSetting");
            try
            {
                string aPath = Path.Combine(iDir, SaveName);// + ".txt"
                if (!File.Exists(aPath))
                {
                    return;
                }
                //m_VolumeSetting = Core.FileLib.Lib.DeserializeFromFile<VolumeSetting>(path);
                string aJsonText = File.ReadAllText(aPath);
                //Debug.LogWarning($"Load aJsonText:{aJsonText}");
                m_VolumeSetting = (VolumeSetting)JsonConvert.LoadDataFromJson(m_VolumeSetting, JsonData.ParseJson(aJsonText));
            }
            catch(System.Exception iException)
            {
                m_VolumeSetting = new VolumeSetting();
                Debug.LogError("UCL_GameAudioService Load:" + iException);
            }
            m_Loaded = true;
        }
        void UpdateAudioMixer()
        {
            Volume = m_VolumeSetting.m_Volume;
            BGMVolume = m_VolumeSetting.m_BGM;
            SEVolume = m_VolumeSetting.m_SE;
            VoiceVolume = m_VolumeSetting.m_Voice;
        }
        private void Update()
        {
            if (m_Loaded)
            {
                m_Loaded = false;
                UpdateAudioMixer();
            }
            for(int i = 0; i < m_ActivePlayers.Count; i++)
            {
                m_ActivePlayers[i].Update();
            }
        }
    }
}