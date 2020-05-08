using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.IO;
namespace UCL.Core.Game {
    public class UCL_GameAudioService : UCL_GameService {
        [System.Serializable]
        public class VolumeSetting {
            /// <summary>
            /// Base Volume
            /// </summary>
            public float m_Volume;

            /// <summary>
            /// Sound Effect
            /// </summary>
            public float m_SE;

            public int m_DD;
        }

        public VolumeSetting m_VolumeSetting { get; protected set; } = new VolumeSetting();

        public static UCL_GameAudioService Instance { get; protected set; }
        override public void Init() {
            Instance = this;
            m_VolumeSetting.m_SE = 3.75f;
            m_VolumeSetting.m_Volume = 0.35f;
            m_VolumeSetting.m_DD = 123;
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
            m_VolumeSetting = Core.FileLib.Lib.ReadBinaryFromFile<VolumeSetting>(path);
            Debug.LogWarning("Prev m_VolumeSetting.m_SE:" + m_VolumeSetting.m_SE);
            Debug.LogWarning("Prev m_VolumeSetting.m_Volume:" + m_VolumeSetting.m_Volume);
            Debug.LogWarning("Prev m_VolumeSetting.m_DD:" + m_VolumeSetting.m_DD);
        }
    }
}