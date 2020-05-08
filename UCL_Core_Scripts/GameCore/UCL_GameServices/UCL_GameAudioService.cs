using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.IO;
using System.Runtime.InteropServices;

namespace UCL.Core.Game {
    public class UCL_GameAudioService : UCL_GameService {
        [System.Serializable]
        public struct VolumeSetting {
            /// <summary>
            /// Base Volume
            /// </summary>
            //[MarshalAs(UnmanagedType.I2, SizeConst = 32)]
            public float m_Volume;

            /// <summary>
            /// Sound Effect
            /// </summary>
            public float m_SE;

            public int m_DD;
        }

        public VolumeSetting m_VolumeSetting = new VolumeSetting();// { get; protected set; } 

        public static UCL_GameAudioService Instance { get; protected set; }
        override public void Init() {
            Instance = this;
            m_VolumeSetting.m_SE = 1.75f;
            m_VolumeSetting.m_Volume = 0.235f;
            m_VolumeSetting.m_DD = 33;
        }
        public override void Save(string dir) {
            string path = Path.Combine(dir, "VolumeSetting");//+ ".txt"
            //Core.FileLib.Lib.WriteBinaryToFile(path, m_VolumeSetting);
            Core.FileLib.Lib.SerializeToFile(path, m_VolumeSetting);
            //File.WriteAllBytes(path,)
        }
        public override void Load(string dir) {
            string path = Path.Combine(dir, "VolumeSetting");// + ".txt"
            if(!File.Exists(path)) {
                return;
            }
            Debug.LogWarning("m_VolumeSetting.m_SE:" + m_VolumeSetting.m_SE);
            Debug.LogWarning("m_VolumeSetting.m_Volume:" + m_VolumeSetting.m_Volume);
            Debug.LogWarning("m_VolumeSetting.m_DD:" + m_VolumeSetting.m_DD);
            m_VolumeSetting = Core.FileLib.Lib.DeserializeFromFile<VolumeSetting>(path);
            //Core.FileLib.Lib.ReadBinaryFromFile<VolumeSetting>(path);
            Debug.LogWarning("Prev m_VolumeSetting.m_SE:" + m_VolumeSetting.m_SE);
            Debug.LogWarning("Prev m_VolumeSetting.m_Volume:" + m_VolumeSetting.m_Volume);
            Debug.LogWarning("Prev m_VolumeSetting.m_DD:" + m_VolumeSetting.m_DD);
        }
    }
}