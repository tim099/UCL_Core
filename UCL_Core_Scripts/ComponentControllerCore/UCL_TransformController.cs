using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.ComponentController {
    public class UCL_TransformController : MonoBehaviour {
        public void SetPosition(Transform target) {
            transform.SetPosition(target);
        }
        public void SetValue(Transform target) {
            transform.SetValue(target);
        }
    }
}

