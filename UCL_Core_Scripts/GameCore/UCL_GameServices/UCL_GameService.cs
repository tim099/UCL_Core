using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.Game {
    public class UCL_GameService : MonoBehaviour {
        /// <summary>
        /// Init game service
        /// </summary>
        virtual public void Init() { }
        /// <summary>
        /// this will call after all GameService Init
        /// </summary>
        virtual public void InitEnd() { }
        /// <summary>
        /// Save GameService Setting to target directory
        /// </summary>
        /// <param name="iDir"></param>
        virtual public void Save(string iDir) { }
        /// <summary>
        /// Load GameService Setting from target directory
        /// </summary>
        /// <param name="iDir"></param>
        virtual public void Load(string iDir) { }
    }
}

