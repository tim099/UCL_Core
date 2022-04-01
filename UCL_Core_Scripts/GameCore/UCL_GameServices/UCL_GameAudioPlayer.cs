using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.Game
{
    public class UCL_GameAudioPlayer : MonoBehaviour
    {
        public enum StartOption
        {
            None = 0,//No Auto Play
            OnStart,//Play On Start()
            OnEnable,//Play On OnEnable()
            OnAwake,//Play On Awake()
        }
        public enum EndOption
        {
            None = 0,//No Auto End
            /// <summary>
            /// Auto End On OnDestroy()
            /// </summary>
            OnDestroy,
            /// <summary>
            /// Auto End On OnDisable
            /// </summary>
            OnDisable,
        }
        public float m_Volume = 1f;
        public AudioClip m_Clip = null;
        public AudioType m_AudioType = AudioType.None;
        public StartOption m_StartOption = StartOption.OnStart;
        public EndOption m_EndOption = EndOption.None;
        public AudioPlayer m_AudioPlayer = null;
        public bool m_IsLoop = false;
        private void Awake()
        {
            if (m_StartOption == StartOption.OnAwake) Play();
        }
        void Start()
        {
            if (m_StartOption == StartOption.OnStart) Play();
        }
        private void OnDestroy()
        {
            if(m_EndOption == EndOption.OnDestroy)
            {
                if (m_AudioPlayer != null) m_AudioPlayer.Stop();
            }
        }
        private void OnDisable()
        {
            if (m_EndOption == EndOption.OnDisable)
            {
                if (m_AudioPlayer != null) m_AudioPlayer.Stop();
            }
        }
        private void OnEnable()
        {
            if (m_StartOption == StartOption.OnEnable) Play();
        }
        public void Play()
        {
            //Debug.LogError("Play");
            var Ins = UCL_GameAudioService.Ins;
            if (Ins == null) return;
            m_AudioPlayer = Ins.Play(m_Clip, m_AudioType, m_Volume);
            m_AudioPlayer.m_AudioSource.loop = m_IsLoop;
        }
    }
}