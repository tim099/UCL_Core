using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.UI {
    public class PositionHandle2D
    {
        static GUIStyle sButtonStyle = null;
        static GUIStyle ButtonStyle { 
            get {
                if(sButtonStyle == null)
                {
                    sButtonStyle = new GUIStyle(GUI.skin.button);
                    sButtonStyle.fontSize = 38;
                    sButtonStyle.normal.textColor = Color.blue;
                    sButtonStyle.hover.textColor = Color.blue;
                }
                return sButtonStyle;
            }
        }
        static GUIStyle sButtonStyleV = null;
        static GUIStyle ButtonStyleV
        {
            get
            {
                if (sButtonStyleV == null)
                {
                    sButtonStyleV = new GUIStyle(GUI.skin.button);
                    sButtonStyleV.fontSize = 36;
                    sButtonStyleV.normal.textColor = Color.green;
                    sButtonStyleV.hover.textColor = Color.green;
                }
                return sButtonStyleV;
            }
        }
        static GUIStyle sButtonStyleH = null;
        static GUIStyle ButtonStyleH
        {
            get
            {
                if (sButtonStyleH == null)
                {
                    sButtonStyleH = new GUIStyle(GUI.skin.button);
                    sButtonStyleH.fontSize = 36;
                    sButtonStyleH.normal.textColor = Color.red;
                    sButtonStyleH.hover.textColor = Color.red;
                }
                return sButtonStyleH;
            }
        }

        Rect ButtonRect;
        Rect ButtonRectV;
        Rect ButtonRectH;
        public Vector2 Position { get; set; }
        Vector2 m_MouseToPositionDelta = Vector2.zero;
        enum PressType
        {
            None = 0,
            Pressed,
            PressedV,
            PressedH
        }
        PressType m_PressType = PressType.None;
        bool m_Draged = false;
        int m_Size = 20;
        public void SetSize(int iSize)
        {
            m_Size = iSize;
            ButtonRect = new Rect(Position.x + m_Size, Position.y, 2 * m_Size, 2 * m_Size);
            ButtonRectV = new Rect(Position.x + m_Size, Position.y - 2 * m_Size - 10, 2 * m_Size, 2 * m_Size);
            ButtonRectH = new Rect(Position.x + 3 * m_Size + 10, Position.y, 2 * m_Size, 2 * m_Size);
        }
        public void SetPosition(Vector2 iPosition)
        {
            Position = iPosition;
            ButtonRect.position = new Vector2(Position.x + m_Size, Position.y);
            ButtonRectV.position = new Vector2(Position.x + m_Size, Position.y - 2 * m_Size - 10);
            ButtonRectH.position = new Vector2(Position.x + 3 * m_Size + 10, Position.y);
        }
        public bool OnGUI()
        {
            if(ButtonRect == null)
            {
                SetSize(20);
            }
            var aCurrentEvent = Event.current;
            var aMousePos = aCurrentEvent.mousePosition;

            if (Event.current.type == EventType.MouseDown)
            {
                if (ButtonRect.Contains(Event.current.mousePosition))
                {
                    m_MouseToPositionDelta = Position - aMousePos;
                    m_PressType = PressType.Pressed;
                }
                else if (ButtonRectV.Contains(Event.current.mousePosition))
                {
                    m_MouseToPositionDelta = Position - aMousePos;
                    m_PressType = PressType.PressedV;
                }
                else if (ButtonRectH.Contains(Event.current.mousePosition))
                {
                    m_MouseToPositionDelta = Position - aMousePos;
                    m_PressType = PressType.PressedH;
                }
            }


            if (Event.current.type == EventType.MouseUp)
            {
                m_PressType = PressType.None;
            }
            m_Draged = false;
            if (Event.current.type == EventType.MouseDrag)
            {
                switch (m_PressType)
                {
                    case PressType.Pressed:
                        {
                            SetPosition(m_MouseToPositionDelta + aMousePos);
                            m_Draged = true;
                            break;
                        }
                    case PressType.PressedV:
                        {
                            SetPosition(new Vector2(Position.x, (m_MouseToPositionDelta + aMousePos).y));
                            m_Draged = true;
                            break;
                        }
                    case PressType.PressedH:
                        {
                            SetPosition(new Vector2((m_MouseToPositionDelta + aMousePos).x, Position.y));
                            m_Draged = true;
                            break;
                        }
                }
            }
            bool aIsClicked = false;
            if (GUI.Button(ButtonRect, "▧", ButtonStyle))
            {
                if (!m_Draged)
                {
                    aIsClicked = true;
                }
            }
            if(GUI.Button(ButtonRectV, "↑", ButtonStyleV))
            {

            }
            if (GUI.Button(ButtonRectH, "→", ButtonStyleH))
            {

            }
            return aIsClicked;
        }
    }
    public class DraggableButton
    {
        public GUIStyle ButtonStyle { get; set; } = null;
        public string Name { get; set; }
        public Rect mButtonRect = new Rect(10, 10, 100, 100);
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
            if (ButtonStyle != null)
            {
                if (GUI.Button(mButtonRect, Name, ButtonStyle))
                {
                    if (!mDraged)
                    {
                        aIsClicked = true;
                    }
                }
            }
            else
            {
                if (GUI.Button(mButtonRect, Name))
                {
                    if (!mDraged)
                    {
                        aIsClicked = true;
                    }
                }
            }

            return aIsClicked;
        }
    }
    public static class UCL_GUI {
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
        /// <summary>
        /// draw a Handle
        /// </summary>
        /// <param name="iPosition"></param>
        /// <returns></returns>
        public static Vector2 PositionHandle(Vector2 iPosition, UCLI_ObjectDictionary iDic)
        {
            const int Size = 20;
            const string Key = "PositionHandle";
            if (!iDic.ContainsKey(Key))
            {
                PositionHandle2D aHandle = new PositionHandle2D();
                aHandle.SetSize(Size);
                iDic.SetData(Key, aHandle);
            }
            var aDragHandle = iDic.GetData<PositionHandle2D>(Key);
            aDragHandle.SetPosition(iPosition);
            
            aDragHandle.OnGUI();
            var aResult = aDragHandle.Position;
            if ((aResult - iPosition).magnitude < 0.001f)
            {
                return iPosition;
            }
            return aResult;
        }
    }
}