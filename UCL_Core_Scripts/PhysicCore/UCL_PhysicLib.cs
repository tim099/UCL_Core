using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.Physic {
    public static class UCL_PhysicLib
    {
        public static List<string> GetAllLayerNames() {
            List<string> layer_names = new List<string>();
            for(int i = 0; i < 32; i++) {
                var layer_name = LayerMask.LayerToName(i);
                if(layer_name.Length > 0) {
                    layer_names.Add(layer_name);
                }
            }
            return layer_names;
        }
    }
}

