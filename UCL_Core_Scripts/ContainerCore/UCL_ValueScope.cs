
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UCL.Core
{
    public class UCL_ValueScope<T> : IDisposable
    {
        public static T CurInstance
        {
            get
            {
                if (s_Stack == null || s_Stack.Count == 0) return default;
                return s_Stack.Peek();
            }
        }
        private static Stack<T> s_Stack = null;

        public UCL_ValueScope(T iTarget) { 
            if(s_Stack == null)
            {
                s_Stack = new Stack<T>();
            }
            s_Stack.Push(iTarget);
        }

        public void Dispose()
        {
            if(s_Stack != null && s_Stack.Count > 0)
            {
                s_Stack.Pop();
            }
        }

    }
}