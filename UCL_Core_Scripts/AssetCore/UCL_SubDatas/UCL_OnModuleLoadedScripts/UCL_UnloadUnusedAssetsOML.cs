
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 01/10 2025
using Cysharp.Threading.Tasks;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UCL.Core;
using UCL.Core.LocalizeLib;
using UCL.Core.UI;
using UnityEngine;

namespace UCL.Core
{
    /// <summary>
    /// Resources.UnloadUnusedAssets
    /// </summary>
    public class UCL_UnloadUnusedAssetsOML : UCL_OnModuleLoaded
    {
        public override UniTask OnModuleLoaded(CancellationToken token)
        {
            //Debug.LogError($"Resources.UnloadUnusedAssets");
            Resources.UnloadUnusedAssets();
            return UniTask.CompletedTask;
        }
    }
}

