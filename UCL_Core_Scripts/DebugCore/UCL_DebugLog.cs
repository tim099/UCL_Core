using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core {
    public class UCL_DebugLog : UCL_Singleton<UCL_DebugLog> {
        public class LogData {
            public LogData(string _Message, string _StackTrace, LogType _Type) {
                m_Message = _Message;
                m_StackTrace = _StackTrace;
                m_Type = _Type;
            }
            public string m_Message;
            public string m_StackTrace;
            public LogType m_Type = LogType.Log;
        }
        static readonly Dictionary<LogType, Color> LogColors = new Dictionary<LogType, Color>()
        {
                { LogType.Assert, Color.white },
                { LogType.Error, Color.red },
                { LogType.Exception, Color.red },
                { LogType.Log, Color.white },
                { LogType.Warning, Color.yellow },
        };
        const int m_Margin = 20;

        Rect m_WindowRect = new Rect(m_Margin, m_Margin, Screen.width - (m_Margin * 2), Screen.height - (m_Margin * 2));
        //Rect m_TitleBarRect = new Rect(0, 0, 10000, 20);
        protected Vector2 m_VertScrollPosition;
        protected Vector2 m_HoriScrollPosition;
        protected GUIStyle m_LogStyle = new GUIStyle();
        protected GUIStyle m_LogButtonStyle = null;
        protected float m_Scale;

        public bool f_Show = true;
        public int m_MaxLogCount = 100;
        protected List<LogData> m_LogDataList = new List<LogData>();
        protected LogData m_SelectedLog = null;
        private void Awake() {
            if(!SetInstance(this)) {
                return;
            }
            Core.UI.UCL_CanvasBlocker.Get()?.SetBlockOnHotControl(true);
            //Application.logMessageReceived += Log;
            Application.logMessageReceivedThreaded += ThreadedLog;
        }
        override protected void OnDestroy() {
            base.OnDestroy();
            //Application.logMessageReceived -= Log;
            Application.logMessageReceivedThreaded -= ThreadedLog;
        }
        void OnGUI() {
            if(!f_Show) {
                return;
            }
            if(m_LogButtonStyle == null) {
                m_LogButtonStyle = new GUIStyle(GUI.skin.button);
            }
            var m_ScreenOrientation = Screen.orientation;
            if(m_ScreenOrientation == ScreenOrientation.Landscape ||
                m_ScreenOrientation == ScreenOrientation.LandscapeLeft ||
                m_ScreenOrientation == ScreenOrientation.LandscapeRight) {
                m_Scale = (Screen.width / 1280.0f);
            } else if(m_ScreenOrientation == ScreenOrientation.Portrait ||
                m_ScreenOrientation == ScreenOrientation.PortraitUpsideDown) {//Portrait!
                m_Scale = (Screen.width / 720.0f);
            } else {//unknown

            }
            int width = Screen.width - (m_Margin * 2);
            int height = Screen.height - (m_Margin * 2);
            m_WindowRect.size = new Vector2(width,height);
            m_WindowRect = GUILayout.Window(122125, m_WindowRect, DebugConsole, "Debug Console");
        }
        void DebugConsole(int windowID) {
            GUILayout.BeginHorizontal();

            m_LogButtonStyle.normal.textColor = Color.white;

            if(GUILayout.Button("Clear", style: m_LogButtonStyle)) {
                ClearLog();
            }
            if(GUILayout.Button("Log", style: m_LogButtonStyle)) {
                Debug.LogWarning("Log");
            }
            if(GUILayout.Button("Warning", style: m_LogButtonStyle)) {
                Debug.LogWarning("Warning");
            }
            if(GUILayout.Button("Error", style: m_LogButtonStyle)) {
                Debug.LogWarning("Error");
            }
            //GUI.backgroundColor
            if(GUILayout.Button("Close", style: m_LogButtonStyle)) {
                f_Show = false;
            }
            GUILayout.EndHorizontal();

            m_VertScrollPosition = GUILayout.BeginScrollView(m_VertScrollPosition, false, true);
            //, GUILayout.Width(m_WindowRect.size.x - 20) , GUILayout.Height(m_WindowRect.size.y * h)
            const int scroll_width = 14;
            ///*
            GUI.skin.verticalScrollbar.fixedWidth = scroll_width * m_Scale;
            GUI.skin.verticalScrollbarDownButton.fixedWidth = scroll_width * m_Scale;
            GUI.skin.verticalScrollbarThumb.fixedWidth = scroll_width * m_Scale;
            GUI.skin.verticalScrollbarUpButton.fixedWidth = scroll_width * m_Scale;

            
            GUI.skin.horizontalScrollbar.fixedHeight = scroll_width * m_Scale;
            GUI.skin.horizontalScrollbarLeftButton.fixedHeight = scroll_width * m_Scale;
            GUI.skin.horizontalScrollbarRightButton.fixedHeight = scroll_width * m_Scale;
            GUI.skin.horizontalScrollbarThumb.fixedHeight = scroll_width * m_Scale;
            //*/
            m_LogButtonStyle.fontSize = 22;

            for(int i = m_LogDataList.Count - 1; i >= 0; i--) {
                var log = m_LogDataList[i];

                //m_LogStyle.normal.textColor = LogColors[log.m_Type];
                bool flag = false;
                if(m_SelectedLog == log) {
                    flag = true;
                    UCL.Core.UI.UCL_GUI.PushBackGroundColor(Color.blue, true);
                    //m_LogButtonStyle.normal.textColor = Color.cyan;
                }
                m_LogButtonStyle.normal.textColor = LogColors[log.m_Type];
                m_LogButtonStyle.hover.textColor = m_LogButtonStyle.normal.textColor;
                //GUI.contentColor = logTypeColors[log.type];
                if(GUILayout.Button(log.m_Message, style: m_LogButtonStyle)) {
                    //Debug.LogWarning("Pressed:" + log.m_Message);
                    if(m_SelectedLog == log) {
                        m_SelectedLog = null;
                    } else {
                        m_SelectedLog = log;
                    }

                }

                if(flag) {
                    UCL.Core.UI.UCL_GUI.Undo();
                    //UCL.Core.UI.UCL_GUI.PopBackGroundColor();
                }
                //GUILayout.Label(log.m_Message, m_LogStyle);
            }
            GUILayout.EndScrollView();
            if(m_SelectedLog != null) {
                m_HoriScrollPosition = GUILayout.BeginScrollView(m_HoriScrollPosition,
                GUILayout.Width(m_WindowRect.size.x - 20)
                , GUILayout.Height(m_WindowRect.size.y * 0.35f));
                GUILayout.BeginHorizontal();

                m_LogStyle.fontSize = 16;
                m_LogStyle.normal.textColor = Color.white;
                GUILayout.Label(m_SelectedLog.m_StackTrace, m_LogStyle);

                GUILayout.EndHorizontal();
                GUILayout.EndScrollView();
            }
            GUI.DragWindow();//m_TitleBarRect
                             //GUI.contentColor = Color.white;
        }


        public void LogWarning(string message, string stack_trace = "") {
            Log(message, stack_trace, LogType.Warning);
        }
        public void LogError(string message, string stack_trace = "") {
            Log(message, stack_trace, LogType.Error);
        }
        public void ClearLog() {
            m_LogDataList.Clear();
            m_SelectedLog = null;
        }
        Queue<LogData> m_LogQue = new Queue<LogData>();
        public void ThreadedLog(string message, string stack_trace = "", LogType type = LogType.Log) {
            lock(m_LogQue) {
                m_LogQue.Enqueue(new LogData(message, stack_trace, type));
            }
        }
        public void Log(LogData data) {
            m_LogDataList.Add(data);
            if(m_LogDataList.Count > 0 && m_LogDataList.Count >= m_MaxLogCount) {
                if(m_SelectedLog == m_LogDataList[0]) {
                    m_SelectedLog = null;
                }
                m_LogDataList.RemoveAt(0);
            }
        }
        public void Log(string message, string stack_trace = "", LogType type = LogType.Log) {
            Log(new LogData(message, stack_trace, type));
        }
        private void Update() {
            lock(m_LogQue) {
                while(m_LogQue.Count > 0) {
                    Log(m_LogQue.Dequeue());
                }
            }
        }
    }
}