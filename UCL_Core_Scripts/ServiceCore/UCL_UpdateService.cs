using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UCL.Core.ServiceLib
{
    [UCL.Core.ATTR.EnableUCLEditor]
    public class UCL_UpdateService : UCL_Singleton<UCL_UpdateService>
    {
        event System.Action m_UpdateAction = null;
        [RuntimeInitializeOnLoadMethod]
        public static void Initialize()
        {

        }
        public void AddUpdateAction(System.Action iAction)
        {
            m_UpdateAction += iAction;
        }
        public void RemoveUpdateAction(System.Action iAction)
        {
            m_UpdateAction -= iAction;
        }
        public void Clear()
        {
            m_UpdateAction = null;
        }
        [UCL.Core.ATTR.UCL_DrawString]
        string UpdateCount()
        {
            return "UpdateAction Count:" + m_UpdateAction.GetInvocationCount().ToString();
        }
        private void UpdateAction()
        {
            try
            {
                if (m_UpdateAction != null)
                {
                    m_UpdateAction.Invoke();
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError(e);
            }
        }
        private void Update()
        {
            UpdateAction();
        }

    }
}

