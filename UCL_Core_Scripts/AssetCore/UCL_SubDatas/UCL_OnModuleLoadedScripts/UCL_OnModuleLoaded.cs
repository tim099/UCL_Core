
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
    public interface UCLI_OnModuleLoaded : UCLI_TypeListable
    {
        UniTask OnModuleLoaded(CancellationToken token);
    }

    [UCL.Core.ATTR.UCL_IgnoreInTypeListable]
    public class UCL_OnModuleLoaded : UCL.Core.JsonLib.UnityJsonSerializable, UCLI_OnModuleLoaded
    {
        virtual public UniTask OnModuleLoaded(CancellationToken token)
        {
            throw new System.NotImplementedException($"{GetType().FullName}.{nameof(OnModuleLoaded)} not Implemented!");
        }
    }
}
