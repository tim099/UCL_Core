using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UCL.Core.ServiceLib
{
    [UCL.Core.ATTR.EnableUCLEditor]
    public class UCL_UpdateService : UCL_Singleton<UCL_UpdateService>
    {
        /// <summary>
        /// Action trigger once!!
        /// </summary>
        private Queue<System.Action> m_ActQue = new Queue<System.Action>();

        private event System.Action m_UpdateAction = null;

        [RuntimeInitializeOnLoadMethod]
        private static void Initialize()
        {

        }
        /// <summary>
        /// Add action that invoke every Update
        /// </summary>
        /// <param name="iAction"></param>
        public void AddUpdateAction(System.Action iAction)
        {
            m_UpdateAction += iAction;
        }
        public void RemoveUpdateAction(System.Action iAction)
        {
            m_UpdateAction -= iAction;
        }
        /// <summary>
        /// Add action that only invoke once
        /// </summary>
        /// <param name="act"></param>
        public void AddAction(System.Action act)
        {
            m_ActQue.Enqueue(act);
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
            while (m_ActQue.Count > 0)
            {
                try
                {
                    m_ActQue.Dequeue()?.Invoke();
                }
                catch (System.Exception e)
                {
                    Debug.LogError(e);
                }
            }
            try
            {
                if (m_UpdateAction != null)
                {
                    m_UpdateAction.Invoke();
                }
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
            }
        }
        private void Update()
        {
            UpdateAction();
        }

    }
}

