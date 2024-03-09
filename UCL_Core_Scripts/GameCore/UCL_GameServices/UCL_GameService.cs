using Cysharp.Threading.Tasks;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace UCL.Core.Game {
    public class UCL_GameService : MonoBehaviour {
        protected UCL_GameManager p_GameManager;

        public void SetGameManager(UCL_GameManager iGameManager)
        {
            p_GameManager = iGameManager;
        }
        /// <summary>
        /// Init game service
        /// </summary>
        virtual public void Init() { }

        virtual public UniTask InitAsync(CancellationToken iToken)
        {
            //await UniTask.Yield(cancellationToken: iToken);
            return default;
        }

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
        /// <summary>
        /// Save GameService Setting
        /// </summary>
        virtual public void Save()
        {
            Save(p_GameManager.GetGameServicePath());
        }
        /// <summary>
        /// Load GameService Setting
        /// </summary>
        virtual public void Load()
        {
            Load(p_GameManager.GetGameServicePath());
        }
    }
}

