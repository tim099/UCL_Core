using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UCL.Core.ServiceLib
{
    public class UCL_UpdateController
    {
        public static UCL_UpdateController Ins
        {
            get
            {
                if (m_Ins == null) m_Ins = new UCL_UpdateController();
                return m_Ins;
            }
        }
        static UCL_UpdateController m_Ins = null;
        /// <summary>
        /// total count of registered update
        /// </summary>
        public int UpdateActionCount => m_UpdateAction.GetInvocationCount();
        /// <summary>
        /// Action trigger once!!
        /// </summary>
        protected Queue<System.Action> m_ActQue = new Queue<System.Action>();
        /// <summary>
        /// Action with delay trigger once!!
        /// </summary>
        protected Queue<System.Tuple<System.Action, float>> m_DelaySecondsQue = new Queue<System.Tuple<System.Action, float>>();
        protected Queue<System.Tuple<int, System.Action>> m_DelayActQue = new Queue<System.Tuple<int, System.Action>>();
        protected static Queue<System.Tuple<int, System.Action>> m_DelayActQueBuffer = new Queue<System.Tuple<int, System.Action>>();
        protected event System.Action m_UpdateAction = null;

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
        /// <param name="iDelay">delay in seconds</param>
        public void AddDelayAction(System.Action iAct, float iDelay)
        {
            m_DelaySecondsQue.Enqueue(new System.Tuple<System.Action, float>(iAct, iDelay));
        }
        /// <summary>
        /// Add action that invoke after delay_frame
        /// </summary>
        /// <param name="iAct"></param>
        /// <param name="iDelayFrames">Delay frame count</param>
        public void AddDelayAction(System.Action iAct, int iDelayFrames)
        {
            m_DelayActQue.Enqueue(new System.Tuple<int, System.Action>(iDelayFrames, iAct));
        }
        public void ClearUpdateAction()
        {
            m_UpdateAction = null;
        }
        
        /// <summary>
        /// Call this function per frame and pass the delta time
        /// </summary>
        /// <param name="aDeltaTime"></param>
        virtual public void UpdateAction(float aDeltaTime)
        {
            //Debug.LogWarning("UpdateAction:" + aDeltaTime);
            foreach (var aAct in m_DelayActQue)
            {
                if (aAct.Item1 > 0)
                {
                    m_DelayActQueBuffer.Enqueue(new System.Tuple<int, System.Action>(aAct.Item1 - 1, aAct.Item2));
                }
                else
                {
                    AddAction(aAct.Item2);
                }
            }
            m_DelayActQue.Clear();
            Core.GameObjectLib.Swap(ref m_DelayActQue, ref m_DelayActQueBuffer);


            while (m_ActQue.Count > 0)
            {
                try
                {
                    m_ActQue.Dequeue()?.Invoke();
                }
                catch (System.Exception iE)
                {
                    Debug.LogException(iE);
                }
            }
            int aCount = m_DelaySecondsQue.Count;
            for (int i = 0; i < aCount; i++)
            {
                try
                {
                    var aAct = m_DelaySecondsQue.Dequeue();
                    float aTime = aAct.Item2 - aDeltaTime;
                    if (aTime > 0)
                    {
                        m_DelaySecondsQue.Enqueue(new System.Tuple<System.Action, float>(aAct.Item1, aTime));
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
    }
    [UCL.Core.ATTR.EnableUCLEditor]
    public class UCL_UpdateService : UCL_Singleton<UCL_UpdateService>
    {
        #region static
        /// <summary>
        /// Add UpdateAction in both Edit mode and Play mode
        /// </summary>
        /// <param name="iAction"></param>
        public static void AddUpdateAction(System.Action iAction)
        {
            if (Application.isPlaying) CheckInstance();
            UCL_UpdateController.Ins.AddUpdateAction(iAction);
        }
        /// <summary>
        /// Remove UpdateAction in both Edit mode and Play mode
        /// </summary>
        /// <param name="iAction"></param>
        public static void RemoveUpdateAction(System.Action iAction)
        {
            if (Application.isPlaying) CheckInstance();
            UCL_UpdateController.Ins.RemoveUpdateAction(iAction);
        }

        /// <summary>
        /// Add action that only invoke once
        /// </summary>
        /// <param name="iAct"></param>
        public static void AddAction(System.Action iAction)
        {
            if (Application.isPlaying) CheckInstance();
            UCL_UpdateController.Ins.AddAction(iAction);
            //if (!Application.isPlaying)
            //{//Edit Mode
            //    UCL.Core.EditorLib.UCL_EditorUpdateManager.AddAction(iAction);
            //}
            //else
            //{
            //    Instance.AddAction(iAction);
            //}
        }
        /// <summary>
        /// Add action with delay that only invoke once!!
        /// </summary>
        /// <param name="iAct"></param>
        /// <param name="iDelay">delay in second</param>
        public static void AddDelayAction(System.Action iAction, float iDelay)
        {
            if (Application.isPlaying) CheckInstance();
            UCL_UpdateController.Ins.AddDelayAction(iAction, iDelay);
        }
        /// <summary>
        /// Add action with delay that only invoke once!!
        /// </summary>
        /// <param name="iAction"></param>
        /// <param name="iDelay">Delay in frame</param>
        public static void AddDelayAction(System.Action iAction, int iDelay)
        {
            if (Application.isPlaying) CheckInstance();
            UCL_UpdateController.Ins.AddDelayAction(iAction, iDelay);
        }
        #endregion

        [RuntimeInitializeOnLoadMethod]
        private static void Initialize()
        {

        }
        [UCL.Core.ATTR.UCL_DrawString]
        private string UpdateCount()
        {
            return "UpdateAction Count:" + UCL_UpdateController.Ins.UpdateActionCount;
        }
        private void Update()
        {
            try
            {
                UCL_UpdateController.Ins.UpdateAction(Time.deltaTime);
            }
            catch (System.Exception iE)
            {
                Debug.LogException(iE);
            }
        }

    }
}

