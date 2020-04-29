using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.UI {
    public static class UCL_GUI{
        static Stack<Color> m_BackGroundColorStack = new Stack<Color>();
        static Stack<System.Action> m_UndoStack = new Stack<System.Action>();
        public static void Undo() {
            if(m_UndoStack.Count == 0) return;
            m_UndoStack.Pop()?.Invoke();
        }
        public static void PushBackGroundColor(Color col,bool record_undo = false) {
            m_BackGroundColorStack.Push(GUI.backgroundColor);
            GUI.backgroundColor = col;
            if(record_undo) m_UndoStack.Push(PopBackGroundColor);
        }
        public static void PopBackGroundColor() {
            if(m_BackGroundColorStack.Count == 0) {
                return;
            }
            GUI.backgroundColor = m_BackGroundColorStack.Pop();
        }
    }
}