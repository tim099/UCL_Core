using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UCL.Core.ServiceLib
{
    public class UCL_OnGUIService :  UCL_Singleton<UCL_OnGUIService>
    {
        List<System.Action> m_OnGUIActions = new List<System.Action>();
        List<System.Action> m_OnGUIActionsBuffer = new List<System.Action>();
        public void AddAction(System.Action iAct)
        {
            m_OnGUIActionsBuffer.Add(iAct);
        }
        private void Update()
        {
            
        }
        private void OnGUI()
        {
            //Debug.LogError("OnGUI() Event.current.type:" + Event.current.type);
            if(Event.current.type == EventType.Layout)
            {
                UCL.Core.GameObjectLib.Swap(ref m_OnGUIActions, ref m_OnGUIActionsBuffer);
            }
            foreach(var aAction in m_OnGUIActions)
            {
                aAction?.Invoke();
            }
            if(Event.current.type == EventType.Repaint)
            {
                m_OnGUIActions.Clear();
            }
        }
    }
}