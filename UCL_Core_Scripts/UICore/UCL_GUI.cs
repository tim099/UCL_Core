using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.UI {
    public class DraggableButton
    {
        public string Name { get; set; }
        Rect mButtonRect = new Rect(10, 10, 200, 100);
        Vector2 mMouseToPositionDelta = Vector2.zero;
        bool mButtonPressed = false;
        bool mDraged = false;
        public bool OnGUI()
        {
            var aCurrentEvent = Event.current;
            var aMousePos = aCurrentEvent.mousePosition;
            if (mButtonRect.Contains(Event.current.mousePosition))
            {
                if (Event.current.type == EventType.MouseDown)
                {
                    mMouseToPositionDelta = mButtonRect.position - aMousePos;
                    mButtonPressed = true;
                }
                else if (Event.current.type == EventType.MouseUp)
                {
                    mButtonPressed = false;
                }
                
            }
            mDraged = false;
            if (mButtonPressed && Event.current.type == EventType.MouseDrag)
            {
                mButtonRect.position = mMouseToPositionDelta + aMousePos;
                mDraged = true;
            }
            bool aIsClicked = false;
            if (GUI.Button(mButtonRect, Name))
            {
                if (!mDraged)
                {
                    aIsClicked = true;
                }
            }
            return aIsClicked;
        }
    }
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