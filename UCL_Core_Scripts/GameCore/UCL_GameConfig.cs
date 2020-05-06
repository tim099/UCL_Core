using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.Game {
    public class UCL_GameConfig {
        virtual public void Init() {

        }
        virtual public string Save() {
            return "Config test:" + System.DateTime.Now.ToString();
        }
        virtual public void Load(string config) {
            Debug.LogWarning("Config:" + config);
        }

    }
}