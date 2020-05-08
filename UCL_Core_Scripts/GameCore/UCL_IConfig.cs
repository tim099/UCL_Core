using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.Game {
    public interface UCL_IConfig {
        string Save();
        void Load(string config);
    }
}

