using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UCL.Core.ServiceLib
{
    public class DelayAction
    {
        public DelayAction(float iDelay, System.Action iAction)
        {
            m_Delay = iDelay;
            m_Action = iAction;
        }
        /// <summary>
        /// return true if this Action not trigger yet
        /// </summary>
        /// <param name="iDeltaTime"></param>
        /// <returns></returns>
        public bool Update(float iDeltaTime)
        {
            if(m_Action == null)//nothing to trigger
            {
                return true;
            }
            m_Delay -= iDeltaTime;
            if (m_Delay <= 0)
            {
                m_Action?.Invoke();
                m_Action = null;
                return false;
            }
            return true;
        }
        /// <summary>
        /// Stop this delay action
        /// </summary>
        public void Kill()
        {
            m_Action = null;
        }
        public System.Action m_Action = null;
        public float m_Delay = 0f;
    }
    public class FrameDelayAction
    {
        public FrameDelayAction(int iDelay, System.Action iAction)
        {
            m_Delay = iDelay;
            m_Action = iAction;
        }
        /// <summary>
        /// return true if this Action not trigger yet
        /// </summary>
        /// <returns></returns>
        public bool Update()
        {
            if (m_Action == null)//nothing to trigger
            {
                return true;
            }
            if (--m_Delay <= 0)
            {
                m_Action?.Invoke();
                m_Action = null;
                return false;
            }
            return true;
        }
        /// <summary>
        /// Stop this delay action
        /// </summary>
        public void Kill()
        {
            m_Action = null;
        }
        public System.Action m_Action = null;
        public int m_Delay = 0;
    }
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
        protected Queue<DelayAction> m_DelaySecondsQue = new Queue<DelayAction>();
        protected Queue<FrameDelayAction> m_DelayFramesQue = new Queue<FrameDelayAction>();
        //protected static Queue<System.Tuple<int, System.Action>> m_DelayActQueBuffer = new Queue<System.Tuple<int, System.Action>>();
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
        public DelayAction AddDelayAction(float iDelay, System.Action iAct)
        {
            var aDelayAct = new DelayAction(iDelay, iAct);
            //Debug.LogError("AddDelayAction iDelay:" + iDelay);
            m_DelaySecondsQue.Enqueue(aDelayAct);
            return aDelayAct;
        }
        /// <summary>
        /// Add action that invoke after delay_frame
        /// </summary>
        /// <param name="iAct"></param>
        /// <param name="iDelayFrames">Delay frame count</param>
        public FrameDelayAction AddFrameDelayAction(int iDelayFrames, System.Action iAct)
        {
            var aAct = new FrameDelayAction(iDelayFrames, iAct);
            m_DelayFramesQue.Enqueue(aAct);
            return aAct;
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
            {
                var aBuffer = m_DelayFramesQue;
                m_DelayFramesQue = new Queue<FrameDelayAction>();

                int aCount = aBuffer.Count;
                for (int i = 0; i < aCount; i++)
                {
                    try
                    {
                        var aAct = aBuffer.Dequeue();
                        if (aAct.Update())
                        {
                            m_DelayFramesQue.Enqueue(aAct);
                        }
                    }
                    catch (System.Exception iE)
                    {
                        Debug.LogException(iE);
                    }
                }
            }
            {
                var aBuffer = m_DelaySecondsQue;
                m_DelaySecondsQue = new Queue<DelayAction>();

                int aCount = aBuffer.Count;
                for (int i = 0; i < aCount; i++)
                {
                    try
                    {
                        var aAct = aBuffer.Dequeue();
                        if (aAct.Update(aDeltaTime))
                        {
                            m_DelaySecondsQue.Enqueue(aAct);
                        }
                    }
                    catch (System.Exception iE)
                    {
                        Debug.LogException(iE);
                    }
                }
            }
            {
                var aBuffer = m_ActQue;
                m_ActQue = new Queue<System.Action>();
                while (aBuffer.Count > 0)
                {
                    try
                    {
                        aBuffer.Dequeue()?.Invoke();
                    }
                    catch (System.Exception iE)
                    {
                        Debug.LogException(iE);
                    }
                }
            }
            //Debug.LogWarning("UpdateAction:" + aDeltaTime);




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
        public static DelayAction AddDelayAction(float iDelay, System.Action iAction)
        {
            if (Application.isPlaying) CheckInstance();
            return UCL_UpdateController.Ins.AddDelayAction(iDelay, iAction);
        }
        /// <summary>
        /// Add action with delay that only invoke once!!
        /// </summary>
        /// <param name="iAction"></param>
        /// <param name="iDelay">Delay in frame</param>
        public static void AddFrameDelayAction(int iDelay, System.Action iAction)
        {
            if (Application.isPlaying) CheckInstance();
            UCL_UpdateController.Ins.AddFrameDelayAction(iDelay, iAction);
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

