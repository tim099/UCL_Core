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
        public void SetLocalScaleX(float iX)
        {
            transform.localScale = new Vector3(iX, transform.localScale.y, transform.localScale.z);
        }
        public void SetLocalScaleY(float iY)
        {
            transform.localScale = new Vector3(transform.localScale.x, iY,  transform.localScale.z);
        }
        public void SetLocalScaleZ(float iZ)
        {
            transform.localScale = new Vector3(transform.localScale.x, transform.localScale.y, iZ);
        }
        public void SetLocalScale(Vector3 iScale)
        {
            transform.localScale = iScale;
        }
        public void SetLossyScale(Vector3 iScale)
        {
            transform.SetLossyScale(iScale);
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
            transform.SetLocalX(val);
        }
        public void SetLocalPositionY(float val) {
            transform.SetLocalY(val);
        }
        public void SetLocalPositionZ(float val) {
            transform.SetLocalZ(val);
        }
        public void DestroyGameObject() {
            Destroy(gameObject);
        }
    }
}

