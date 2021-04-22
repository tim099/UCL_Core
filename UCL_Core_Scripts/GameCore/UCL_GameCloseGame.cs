using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UCL.Core.Game
{
    public class UCL_GameCloseGame : MonoBehaviour
    {
        public void CloseGame()
        {
            UCL_GameManager.Instance.ExitGame();
        }
    }
}