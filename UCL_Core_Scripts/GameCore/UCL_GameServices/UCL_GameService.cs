using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.Game {
    public class UCL_GameService : MonoBehaviour {
        virtual public void Init() { }
        virtual public void Save(string dir) { }
        virtual public void Load(string dir) { }
    }
}

