using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.Physic {
    public interface Collider2D {
        bool ContainPoint(Vector2 point);
    }

    public class UCL_Collider2D : MonoBehaviour , Collider2D {
        virtual public bool ContainPoint(Vector2 point) { return false; }
    }
}