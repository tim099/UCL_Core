using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.Game
{
    public class UCL_GameUI : MonoBehaviour
    {
        virtual public void Init()
        {

        }
        /// <summary>
        /// Close this UI
        /// </summary>
        virtual public void Close()
        {

        }
        /// <summary>
        /// When Input.GetKey(KeyCode.Escape) == true
        /// </summary>
        virtual public void OnEscape()
        {
            Close();
        }
    }
}