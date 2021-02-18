using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.IO;

namespace UCL.Core.DebugLib {
#if UNITY_EDITOR
    [ATTR.EnableUCLEditor]
#endif
    [AddComponentMenu("UCL/UCL_DebugLog")]
    [DisallowMultipleComponent]
    public class UCL_DebugLog : UCL_Singleton<UCL_DebugLog> {
        //[Flags]
        public enum LogLevel {
            None = 0,//     LogType used for Errors.
            Error = 1,//     LogType used for Asserts. (These could also indicate an error inside Unity itself.)
            Assert = 1<<1,//     LogType used for Warnings.
            Warning = 1<<2,//     LogType used for regular log messages.
            Log = 1<<3,//     LogType used for Exceptions.
            Exception = 1<< 4//,All = Error|Assert|Warning|Log|Exception
        }
        public class LogData {
            public LogData(string _Message, string _StackTrace, LogType _Type) {
                m_Message = _Message;
                m_StackTrace = _StackTrace;
                m_Type = _Type;
                m_LogTime = DateTime.Now;
                var lines = m_Message.Split(new[] { "\r\n", "\r", "\n" },StringSplitOptions.None);//Environment.NewLine, 
                if(lines.Length > 0) {
                    m_Title = m_LogTime.ToString("[HH:mm:ss] ")+lines[0];
                } else {
                    m_Title = m_Message;
                }
                switch(_Type) {
                    case LogType.Error: {
                            m_Level = LogLevel.Error;
                            break;
                        }
                    case LogType.Assert: {
                            m_Level = LogLevel.Assert;
                            break;
                        }
                    case LogType.Warning: {
                            m_Level = LogLevel.Warning;
                            break;
                        }
                    case LogType.Log: {
                            m_Level = LogLevel.Log;
                            break;
                        }
                    case LogType.Exception: {
                            m_Level = LogLevel.Exception;
                            break;
                        }
                    default: {
                            m_Level = LogLevel.Log;
                            break;
                        }
                }
            }
            public string GetTitle() { return m_Title; }
            public DateTime m_LogTime;
            public string m_Title;
            public string m_Message;
            public string m_StackTrace;
            public LogType m_Type = LogType.Log;
            public LogLevel m_Level;
        }
        static readonly Dictionary<LogType, Color> m_LogColors = new Dictionary<LogType, Color>()
        {
                { LogType.Assert, Color.white },
                { LogType.Error, Color.red },
                { LogType.Exception, Color.red },
                { LogType.Log, Color.white },
                { LogType.Warning, Color.yellow },
        };

#if UNITY_EDITOR
        [ATTR.UCL_FunctionButton]
        public void Editor_OpenLogFile() {
            string folder = GetDebugLogPath().Replace("/", "\\");
            FileLib.Lib.CreateDirectory(folder);

            FileLib.EditorLib.ExploreFile("Open LogFile", folder);
        }
#endif


        const int m_Margin = 20;

        Rect m_WindowRect = new Rect(m_Margin, m_Margin, Screen.width - (m_Margin * 2), Screen.height - (m_Margin * 2));
        //Rect m_TitleBarRect = new Rect(0, 0, 10000, 20);
        protected Vector2 m_VertScrollPosition;
        protected Vector2 m_HoriScrollPosition;
        protected GUIStyle m_LogStyle = new GUIStyle();
        protected GUIStyle m_LogButtonStyle = null;
        protected GUIStyle m_LogToggleStyle = null;
        [PA.UCL_ReadOnly][SerializeField]protected float m_Scale;

        [PA.UCL_EnumMask] public LogLevel m_LogLevel = (LogLevel.Error | LogLevel.Assert | LogLevel.Warning | LogLevel.Log | LogLevel.Exception);
        [SerializeField] protected bool f_Show = true;

        /// <summary>
        /// Draw the show button when f_Show == false
        /// </summary>
        public bool f_DrawShowDebugLogButton = false;
        public bool f_LogToFile = false;
        public bool f_AutoStackTrace = true;
        public int m_MaxLogCount = 100;

        /// <summary>
        /// Show debugLog while this key pressed!!
        /// </summary>
        public KeyCode m_ShowKeyCode = KeyCode.S;

        protected StreamWriter m_StreamWriter = null;
        protected List<LogData> m_LogDataList = new List<LogData>();
        protected LogData m_SelectedLog = null;
        private void Awake() {
            if(!SetInstance(this)) {
                return;
            }
            Core.UI.UCL_CanvasBlocker.CreateInstance()?.SetBlockOnHotControl(true);
            //Application.logMessageReceived += Log;
            Application.logMessageReceivedThreaded += ThreadedLog;

            LoadSetting();
        }
        void LoadSetting() {
            if(PlayerPrefs.HasKey("UCL_DebugLog_LogLevel")) {
                m_LogLevel = (LogLevel)PlayerPrefs.GetInt("UCL_DebugLog_LogLevel");
            }
            if(PlayerPrefs.HasKey("UCL_DebugLog_LogToFile")) f_LogToFile = (PlayerPrefs.GetInt("UCL_DebugLog_LogToFile") == 1);
            if(PlayerPrefs.HasKey("UCL_DebugLog_AutoStackTrace")) f_AutoStackTrace = (PlayerPrefs.GetInt("UCL_DebugLog_AutoStackTrace") == 1);
        }
        void SaveSetting() {
            PlayerPrefs.SetInt("UCL_DebugLog_LogLevel", (int)m_LogLevel);
            PlayerPrefs.SetInt("UCL_DebugLog_LogToFile", f_LogToFile ? 1 : 0);
            PlayerPrefs.SetInt("UCL_DebugLog_AutoStackTrace", f_AutoStackTrace ? 1 : 0);
            PlayerPrefs.Save();
        }
        private void OnApplicationPause(bool pause) {
            if(pause) SaveSetting();
        }
        private void OnApplicationQuit() {
            SaveSetting();
        }
        override protected void OnDestroy() {
            base.OnDestroy();
            //Application.logMessageReceived -= Log;
            Application.logMessageReceivedThreaded -= ThreadedLog;
            SaveSetting();
        }
        public void SetShow(bool show) {
            f_Show = show;
        }
        public void Toggle() {
            SetShow(!f_Show);
        }
        void OnGUI() {
            if(m_LogButtonStyle == null) {
                m_LogButtonStyle = new GUIStyle(GUI.skin.button);
                m_LogButtonStyle.fontSize = 22;


                m_LogToggleStyle = new GUIStyle(GUI.skin.button);//GUI.skin.toggle
                m_LogToggleStyle.fontSize = 22;
                //m_LogToggleStyle.onFocused.textColor = Color.red;
                //m_LogToggleStyle.onActive.textColor = Color.red;
                //m_LogToggleStyle. = Color.red;
            }
            if(!f_Show) {
                if(f_DrawShowDebugLogButton) {
                    string key_str = "";
                    if(m_ShowKeyCode != KeyCode.None) {
                        key_str = "(" + m_ShowKeyCode.ToString() + ")";
                    }
                    if(GUILayout.Button("DebugLog"+ key_str, style: m_LogButtonStyle)) {
                        f_Show = true;
                    }
                }
                return;
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
            m_LogButtonStyle.hover.textColor = Color.white;
            if(GUILayout.Button("Clear", style: m_LogButtonStyle)) {
                ClearLog();
            }
            /*
            foreach(LogLevel level in Enum.GetValues(typeof(LogLevel))) {
                if(GUILayout.Toggle(((m_LogLevel & level) != LogLevel.None), level.ToString(), style: m_LogToggleStyle)) {
                    m_LogLevel |= level;
                } else {
                    m_LogLevel &= ~level;
                }
            }
            */
            {
                Core.UI.UCL_GUI.PushBackGroundColor(f_LogToFile ? Color.green : Color.red, true);
                f_LogToFile = GUILayout.Toggle(f_LogToFile, "Log to File", style: m_LogToggleStyle);
                Core.UI.UCL_GUI.Undo();
            }
            System.Action<LogLevel> log_act = delegate (LogLevel level) {
                bool is_on = ((m_LogLevel & level) != LogLevel.None);
                Core.UI.UCL_GUI.PushBackGroundColor(is_on ? Color.green : Color.red, true);
                if(GUILayout.Toggle(is_on , level.ToString(), style: m_LogToggleStyle)) {
                    m_LogLevel |= level;
                } else {
                    m_LogLevel &= ~level;
                }
                Core.UI.UCL_GUI.Undo();
            };
            {
                Core.UI.UCL_GUI.PushBackGroundColor(f_AutoStackTrace ? Color.green : Color.red, true);
                if(GUILayout.Toggle(f_AutoStackTrace, "StackTrace", style: m_LogToggleStyle)) {
                    f_AutoStackTrace = true;
                } else {
                    f_AutoStackTrace = false;
                }
                Core.UI.UCL_GUI.Undo();
            }
            log_act(LogLevel.Log);
            log_act(LogLevel.Warning);
            log_act(LogLevel.Error);

            //GUI.backgroundColor
            if(GUILayout.Button("Close", style: m_LogButtonStyle)) {
                f_Show = false;
            }
            GUILayout.EndHorizontal();

            m_VertScrollPosition = GUILayout.BeginScrollView(m_VertScrollPosition, false, true, GUILayout.Width(m_WindowRect.size.x - 5));
            //, GUILayout.Width(m_WindowRect.size.x - 20) , GUILayout.Height(m_WindowRect.size.y * h)
            const int scroll_width = 14;
            float sw = scroll_width * m_Scale;
            ///*
            GUI.skin.verticalScrollbar.fixedWidth = sw;
            GUI.skin.verticalScrollbarDownButton.fixedWidth = sw;
            GUI.skin.verticalScrollbarThumb.fixedWidth = sw;
            GUI.skin.verticalScrollbarUpButton.fixedWidth = sw;

            
            GUI.skin.horizontalScrollbar.fixedHeight = sw;
            GUI.skin.horizontalScrollbarLeftButton.fixedHeight = sw;
            GUI.skin.horizontalScrollbarRightButton.fixedHeight = sw;
            GUI.skin.horizontalScrollbarThumb.fixedHeight = sw;
            //*/
            m_LogButtonStyle.fontSize = 22;
            m_LogButtonStyle.alignment = TextAnchor.MiddleLeft;
            for(int i = m_LogDataList.Count - 1; i >= 0; i--) {
                var log = m_LogDataList[i];

                //m_LogStyle.normal.textColor = LogColors[log.m_Type];
                bool flag = false;
                if(m_SelectedLog == log) {
                    flag = true;
                    UCL.Core.UI.UCL_GUI.PushBackGroundColor(Color.blue, true);
                    //m_LogButtonStyle.normal.textColor = Color.cyan;
                }
                m_LogButtonStyle.normal.textColor = m_LogColors[log.m_Type];
                m_LogButtonStyle.hover.textColor = m_LogButtonStyle.normal.textColor;
                //GUI.contentColor = logTypeColors[log.type];
                if((log.m_Level & m_LogLevel) != LogLevel.None) {
                    if(GUILayout.Button(log.m_Title, style: m_LogButtonStyle, GUILayout.Width(m_WindowRect.size.x - 15 - sw))) {
                        //Debug.LogWarning("Pressed:" + log.m_Message);
                        if(m_SelectedLog == log) {
                            m_SelectedLog = null;
                        } else {
                            m_SelectedLog = log;
                        }
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
                GUILayout.Width(m_WindowRect.size.x - 5)
                , GUILayout.Height(m_WindowRect.size.y * 0.35f));
                GUILayout.BeginHorizontal();

                m_LogStyle.fontSize = 16;
                m_LogStyle.normal.textColor = Color.white;
                string str = m_SelectedLog.m_Message;
                str+= "\nStackTrace:" + m_SelectedLog.m_StackTrace;
                GUILayout.Label(str, m_LogStyle);

                GUILayout.EndHorizontal();
                GUILayout.EndScrollView();
            }
            GUI.DragWindow();//m_TitleBarRect
                             //GUI.contentColor = Color.white;

            //SaveSetting();
        }
        public void ClearLog() {
            m_LogDataList.Clear();
            m_SelectedLog = null;
        }
        Queue<LogData> m_LogQue = new Queue<LogData>();
        public void ThreadedLog(string message, string stack_trace = "", LogType type = LogType.Log) {
            if(f_AutoStackTrace && string.IsNullOrEmpty(stack_trace)) {//type != LogType.Log
                stack_trace = new System.Diagnostics.StackTrace(true).ToString();
            }
            lock(m_LogQue) {
                m_LogQue.Enqueue(new LogData(message, stack_trace, type));
            }
        }
        protected string GetDebugLogPath() {
            return Core.FileLib.Lib.GetFilesPath() + "/DebugLog/";
        }
        protected void LogToFile(LogData data) {
            if(m_StreamWriter == null) {
                string path = GetDebugLogPath()+ DateTime.Now.ToString("yyyy_MMdd_HHmm_") + "Log.txt";
                Debug.LogWarning("Save to:" + path);
                m_StreamWriter = Core.FileLib.Lib.OpenWriteStream(path);
                //m_StreamWriter.AutoFlush = true;
            }
            string str = "";
            str += DateTime.Now.ToString("HH:mm:ss ");
            str += data.m_Type.ToString() + ":";
            str += data.m_Message;
            
            m_StreamWriter.WriteLine(str);
            m_StreamWriter.Flush();
        }
        public void Log(LogData data) {
            if(f_LogToFile) {
                LogToFile(data);
            }
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
        /*
        public void LogWarning(string message) {
            LogStackTrace(message, LogType.Warning);
        }
        public void LogError(string message) {
            LogStackTrace(message, LogType.Error);
        }
        public void LogStackTrace(string message, LogType type = LogType.Log) {
            var st = new System.Diagnostics.StackTrace(true);
            string stack_trace = st.ToString();
            /*
            for(int i = 0,len = st.FrameCount; i < len; i++) {
                var sf = st.GetFrame(i);
                stack_trace += "" + sf.GetMethod();// +"\n";
                stack_trace += "(" + sf.GetFileName()+":"+ sf.GetFileLineNumber() + ")\n";

                //stack_trace += "\n";
            }
            Log(message, stack_trace, type);
        }
        */
        private void Update() {
            lock(m_LogQue) {
                while(m_LogQue.Count > 0) {
                    Log(m_LogQue.Dequeue());
                }
            }
            if(f_Show) {

            } else {
                if(m_ShowKeyCode != KeyCode.None) {
                    if(Input.GetKeyDown(m_ShowKeyCode)) {
                        SetShow(true);
                    }
                }

            }
        }
    }
}