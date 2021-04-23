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
        /// <summary>
        /// Action with delay trigger once!!
        /// </summary>
        private Queue<System.Tuple<System.Action,float> > m_DelayQue = new Queue<System.Tuple<System.Action, float>>();

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
        /// <param name="iAct"></param>
        public void AddAction(System.Action iAct)
        {
            m_ActQue.Enqueue(iAct);
        }
        /// <summary>
        /// Add action with delay that only invoke once!!
        /// </summary>
        /// <param name="iAct"></param>
        /// <param name="iDelay">delay in second</param>
        public void AddDelayAction(System.Action iAct,float iDelay)
        {
            m_DelayQue.Enqueue(new System.Tuple<System.Action, float>(iAct, iDelay));
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
            float aTimeDel = Time.deltaTime;
            while (m_ActQue.Count > 0)
            {
                try
                {
                    m_ActQue.Dequeue()?.Invoke();
                }
                catch (System.Exception iE)
                {
                    Debug.LogError(iE);
                }
            }
            int aCount = m_DelayQue.Count;
            for(int i = 0; i < aCount; i++)
            {
                try
                {
                    var aAct = m_DelayQue.Dequeue();
                    float aTime = aAct.Item2 - aTimeDel;
                    if (aTime > 0)
                    {
                        m_DelayQue.Enqueue(new System.Tuple<System.Action, float>(aAct.Item1, aTime));
                    }
                    else
                    {
                        aAct.Item1?.Invoke();
                    }
                }
                catch (System.Exception iE)
                {
                    Debug.LogException(iE);
                }
            }
            //m_DelayQue
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

