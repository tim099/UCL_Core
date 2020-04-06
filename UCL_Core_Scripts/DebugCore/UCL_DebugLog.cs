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
        public bool f_Show = true;
        public int m_MaxLogCount = 100;
        protected List<LogData> m_LogDataList = new List<LogData>();
        const int m_Margin = 20;

        Rect windowRect = new Rect(m_Margin, m_Margin, Screen.width - (m_Margin * 2), Screen.height - (m_Margin * 2));
        protected Vector2 m_ScrollPosition;
        protected GUIStyle m_LogStyle = new GUIStyle();

        private void Awake() {
            Instance = this;
            Application.logMessageReceived += Log;
        }
        override protected void OnDestroy() {
            base.OnDestroy();
            Application.logMessageReceived -= Log;
        }
        void OnGUI() {
            if(!f_Show) {
                return;
            }

            windowRect = GUILayout.Window(122125, windowRect, DebugConsole, "Debug Console");
        }
        void DebugConsole(int windowID) {
            m_ScrollPosition = GUILayout.BeginScrollView(m_ScrollPosition);
            for(int i = m_LogDataList.Count - 1; i >= 0; i--) {
                var log = m_LogDataList[i];

                m_LogStyle.fontSize = 25;
                m_LogStyle.normal.textColor = LogColors[log.m_Type];
                //GUI.contentColor = logTypeColors[log.type];
                GUILayout.Label(log.m_Message, m_LogStyle);
            }

            GUILayout.EndScrollView();

            //GUI.contentColor = Color.white;

        }


        virtual public void LogWarning(string message, string stack_trace = "") {
            Log(message, stack_trace, LogType.Warning);
        }
        virtual public void LogError(string message, string stack_trace = "") {
            Log(message, stack_trace, LogType.Error);
        }
        virtual public void Log(string message, string stack_trace = "", LogType type = LogType.Log) {
            m_LogDataList.Add(new LogData(message, stack_trace, type));
        }
    }
}