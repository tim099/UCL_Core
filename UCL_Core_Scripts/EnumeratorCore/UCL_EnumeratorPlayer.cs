using System.Collections;
using System.Collections.Generic;
namespace UCL.Core.EnumeratorLib {
    public static class Wait {

        /// <summary>
        /// Suspends the coroutine execution until the supplied delegate evaluates to false.
        /// </summary>
        /// <param name="_Predicate"></param>
        /// <returns></returns>
        public static UCL_WaitWhile WaitWhile(System.Func<bool> _Predicate) {
            return new UCL_WaitWhile(_Predicate);
        }

        /// <summary>
        /// Suspends the coroutine execution for the given amount of seconds.
        /// </summary>
        /// <param name="_WaitTime">Wait time in seconds</param>
        /// <returns></returns>
        public static UCL_Seconds WaitSeconds(float _WaitTime) {
            return new UCL_Seconds(_WaitTime);
        }

        /// <summary>
        /// Waits until target update times.
        /// </summary>
        public static UCL_WaitForUpdate WaitForUpdate(int _WaitTimes) {
            return new UCL_WaitForUpdate(_WaitTimes);
        }
        #region WaitClass
        /// <summary>
        /// Waits until target update times.
        /// </summary>
        public class UCL_WaitForUpdate : IEnumerator {
            int m_WaitTimes = 0;
            public UCL_WaitForUpdate(int _WaitTimes) {
                m_WaitTimes = _WaitTimes;
            }
            public object Current { get { return null; } }
            public bool MoveNext() {
                if(m_WaitTimes <= 1) return false;
                --m_WaitTimes;
                return true;
            }
            public void Reset() { }
        }

        /// <summary>
        /// Suspends the coroutine execution until the supplied delegate evaluates to false.
        /// </summary>
        public class UCL_WaitWhile : IEnumerator {
            System.Func<bool> m_Predicate;
            public UCL_WaitWhile(System.Func<bool> _Predicate) {
                m_Predicate = _Predicate;
            }
            public object Current { get { return null; } }
            public bool MoveNext() {
                if(m_Predicate == null) return false;
                return m_Predicate.Invoke();
            }
            public void Reset() { }
        }   
        /// <summary>
        /// Suspends the coroutine execution for the given amount of seconds.
        /// </summary>
        public class UCL_Seconds : IEnumerator {
            public float m_WaitTime { get; private set; }
            protected System.DateTime m_StartTime;
            bool m_Started = false;
            //System.Diagnostics.Stopwatch()
            public UCL_Seconds(float _WaitTime) {
                //UnityEngine.Debug.LogWarning("UCL_Seconds:" + _WaitTime);
                m_WaitTime = _WaitTime;
            }
            public object Current { get {
                    if(!m_Started) {
                        m_Started = true;
                        m_StartTime = System.DateTime.Now;
                        //UnityEngine.Debug.LogWarning("cur_time:" + m_StartTime.ToString("ss.f"));
                        return true;
                    }
                    return null;
                } }
            public bool MoveNext() {

                var cur_time = (System.DateTime.Now - m_StartTime).TotalSeconds;
                //UnityEngine.Debug.LogWarning("cur_time:" + cur_time);
                return cur_time < m_WaitTime;
            }
            public void Reset() { m_StartTime = System.DateTime.Now; }
        }
        #endregion
    }

    public class EnumeratorPlayer {
        const float RecurseLimit = 10;
        static public EnumeratorPlayer Play(IEnumerator _Enumerator) {
            return new EnumeratorPlayer(_Enumerator);
        }

        protected Stack<IEnumerator> m_EnumeratorStack = new Stack<IEnumerator>();
        protected IEnumerator m_Enumerator = null;
        protected System.Action m_EntAct = null;
        public EnumeratorPlayer(IEnumerator _Enumerator) {
            m_Enumerator = _Enumerator;
        }
        /// <summary>
        /// return true if play ended
        /// </summary>
        /// <returns></returns>
        public bool End() {
            return m_Enumerator == null;
        }
        /// <summary>
        /// return yield object
        /// </summary>
        /// <returns></returns>
        public object Update() {
            return UpdateAction(0);
        }
        protected object UpdateAction(int layer) {
            if(m_Enumerator == null) return null;
            var cur = m_Enumerator.Current;
            bool have_next = m_Enumerator.MoveNext();

            if(cur != null) {
                IEnumerator en = cur as IEnumerator;
                if(en != null) {
                    //if(cur is string) Debug.LogError("en is string!!");
                    if(have_next) m_EnumeratorStack.Push(m_Enumerator);
                    m_Enumerator = en;
                    return null;
                    //if(layer > RecurseLimit) return null;//over limit!!
                    //return UpdateAction(layer + 1);
                }
            }

            if(!have_next) {
                if(m_EnumeratorStack.Count > 0) m_Enumerator = m_EnumeratorStack.Pop();
                else m_Enumerator = null;
            }

            return cur;
        }
    }
}

