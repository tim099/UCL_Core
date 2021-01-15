using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core {
    [System.Serializable] public class UCL_Event : UnityEngine.Events.UnityEvent { }
    [System.Serializable] public class UCL_FloatEvent : UnityEngine.Events.UnityEvent<float> { }
    [System.Serializable] public class UCL_IntEvent : UnityEngine.Events.UnityEvent<int> { }
    [System.Serializable] public class UCL_BoolEvent : UnityEngine.Events.UnityEvent<bool> { }
}

