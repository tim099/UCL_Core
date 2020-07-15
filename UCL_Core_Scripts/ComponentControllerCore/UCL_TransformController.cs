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
        public void SetX(float val) {
            transform.SetX(val);
        }
        public void SetY(float val) {
            transform.SetY(val);
        }
        public void SetZ(float val) {
            transform.SetZ(val);
        }
        public void SetLocalPositionX(float val) {
            transform.SetLocalPositionX(val);
        }
        public void SetLocalPositionY(float val) {
            transform.SetLocalPositionY(val);
        }
        public void SetLocalPositionZ(float val) {
            transform.SetLocalPositionZ(val);
        }
    }
}

