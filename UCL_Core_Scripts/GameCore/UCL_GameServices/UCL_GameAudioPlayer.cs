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

        public float m_Volume = 1f;
        public AudioClip m_Clip = null;
        public AudioType m_AudioType = AudioType.None;
        public StartOption m_StartOption = StartOption.OnStart;
        private void Awake()
        {
            if (m_StartOption == StartOption.OnAwake) Play();
        }
        void Start()
        {
            if (m_StartOption == StartOption.OnStart) Play();
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
            Ins.Play(m_Clip, m_AudioType, m_Volume);
        }
    }
}