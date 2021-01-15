using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using System.Threading;
using System;

namespace UCL.Core.ThreadLib {
    public class ThreadCmd {
        public ThreadCmd() {
            End = false;
            m_Stop = false;
        }

        /// <summary>
        /// Action Run at thread
        /// </summary>
        /// <param name="act"></param>
        /// <returns></returns>
        public ThreadCmd SetAction(Action act) {
            m_Action = act;
            return this;
        }

        /// <summary>
        /// EndAction Run at Monobehavior Update!!
        /// </summary>
        /// <param name="act"></param>
        /// <returns></returns>
        public ThreadCmd SetEndAction(Action act) {
            m_EndAction = act;
            return this;
        }
        public void Stop() {
            m_Stop = true;
        }
        /// <summary>
        /// This funtion is only for UCL_ThreadManager, dont call this funtion
        /// </summary>
        internal void I_Run() {
            if(m_Stop) {
                End = true;
                return;
            }

            try {
                m_Action?.Invoke();
            } catch(Exception e) {
                Debug.LogError("ThreadCmd RunCmd Error:" + e.ToString());
            } finally {
                End = true;
            }
        }
        public Action m_Action { get; protected set; }
        public Action m_EndAction { get; protected set; }//Call in unity Update!!



        /// <summary>
        /// Action end flag!!
        /// </summary>
        public bool End { get; protected set; }

        /// <summary>
        /// Stop invoke m_Action
        /// </summary>
        protected bool m_Stop;
    }
    public class UCL_ThreadManager : UCL_Singleton<UCL_ThreadManager> {
        List<ThreadCmd> m_Cmds = new List<ThreadCmd>();
        Queue<System.Action> m_RunOnUpdateQue = new Queue<Action>();
        public bool Run(Action act) {
            return ThreadPool.QueueUserWorkItem(RunAction, act);
        }

        void RunAction(object action) {
            try {
                ((Action)action)();
            } catch (Exception e) {
                Debug.LogError("UCL_ThreadManager RunAction Error:" + e.ToString());
            } finally {
                //Interlocked.Decrement(ref m_ThreadCount);
            }
        }
        public void RunOnUpdate(System.Action act) {
            lock(m_RunOnUpdateQue) {
                m_RunOnUpdateQue.Enqueue(act);
            }
        }
        /// <summary>
        /// Run action on thread
        /// </summary>
        /// <param name="act"></param>Run in thread!!
        /// <param name="end_act"></param>Run In Monobehavior Update()
        /// <returns></returns>
        public ThreadCmd Run(Action act, Action end_act) {
            var cmd = new ThreadCmd();
            cmd.SetAction(act)
                .SetEndAction(end_act);
            ThreadPool.QueueUserWorkItem(RunCmd, cmd);
            m_Cmds.Add(cmd);
            return cmd;
        }

        void RunCmd(object obj) {
            ThreadCmd cmd = obj as ThreadCmd;
            if(cmd == null) {
                //Interlocked.Decrement(ref m_ThreadCount);
                return;
            }
            cmd.I_Run();
        }

        void Update() {
            {
                for(int i = m_Cmds.Count - 1; i >= 0; i--) {
                    var cmd = m_Cmds[i];
                    if(cmd.End) {
                        try {
                            cmd.m_EndAction?.Invoke();
                        } catch(Exception e) {
                            Debug.LogWarning("ThreadManager cmd.m_EndAction?.Invoke() Exception:" + e.ToString());
                        }

                        m_Cmds.Remove(cmd);
                    }
                }
                while(m_RunOnUpdateQue.Count > 0) {
                    var act = m_RunOnUpdateQue.Dequeue();
                    act.Invoke();
                }
            }

        }
    }
}

