using System;
using System.Collections;
using System.Collections.Generic;
using UCL.Core.UI;
using UnityEngine;


namespace UCL.Core.ServiceLib
{
    public static class UCL_DebugLogService
    {
        public class LogData
        {
            public LogData(string _Message, string _StackTrace, LogType iType)
            {
                m_Message = _Message;
                m_StackTrace = _StackTrace;
                m_Type = iType;
                m_LogTime = DateTime.Now;
                var lines = m_Message.SplitByLine();
                if (lines.Length > 0)
                {
                    m_Title = lines[0];
                }
                else
                {
                    m_Title = m_Message;
                }
                m_Title = $"[{m_LogTime.ToString("HH:mm:ss")}] {m_Title.CutToMaxLength(80)}";
            }
            public void OnGUI(UCL.Core.UCL_ObjectDictionary iDataDic)
            {
                if (!s_Inited) return ;
                //using(var aScope = new GUILayout.VerticalScope("box"))
                {
                    bool aIsShow = false;
                    using (var aScope2 = new GUILayout.HorizontalScope())
                    {
                        aIsShow = UCL_GUILayout.Toggle(iDataDic, UCL_GUILayout.IsShowFieldKey);
                        using (var aScope3 = new GUILayout.VerticalScope())
                        {
                            Color aTextColor = Color.white;
                            switch (m_Type)
                            {
                                case LogType.Warning: aTextColor = Color.yellow; break;
                                case LogType.Error: aTextColor = Color.red; break;
                                case LogType.Exception: aTextColor = Color.red; break;
                            }
                            GUILayout.BeginHorizontal();
                            GUILayout.Label($"[{m_Type}]", UCL_GUIStyle.GetLabelStyle(aTextColor), GUILayout.ExpandWidth(false));
                            GUILayout.Label(m_Title, UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                            GUILayout.EndHorizontal();
                            if (aIsShow)
                            {
                                UCL_GUILayout.DrawObjectData(this, iDataDic, "Log", true);
                                //GUILayout.Label($"{m_Message}");
                            }
                        }

                    }


                    
                }
            }
            [UCL.Core.ATTR.UCL_HideOnGUI]
            public DateTime m_LogTime;

            [UCL.Core.ATTR.UCL_HideOnGUI]
            public string m_Title;

            public string m_Message;
            public string m_StackTrace;

            [UCL.Core.ATTR.UCL_HideOnGUI]
            public LogType m_Type = LogType.Log;
        }

        private static bool s_Inited = false;
        private static List<LogData> s_Logs = null;
        private static Dictionary<LogType, bool> s_LogTypeFilter = null;
        public static void Init()
        {
            if(s_Inited) return;
            s_Inited = true;
            s_Logs = new();
            s_LogTypeFilter = new();
            foreach (LogType aLogType in Enum.GetValues(typeof(LogType)))
            {
                s_LogTypeFilter[aLogType] = true;
            }
            Application.logMessageReceivedThreaded += ThreadedLog;
        }
        public static void TopBarButtons(UCL.Core.UCL_ObjectDictionary iDataDic)
        {
            if (!s_Inited) return;

            foreach(LogType aLogType in Enum.GetValues(typeof(LogType)))
            {
                if(aLogType != LogType.Assert)
                {
                    s_LogTypeFilter[aLogType] = UCL_GUILayout.CheckBox(s_LogTypeFilter[aLogType]);
                    GUILayout.Label($"{aLogType}", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    GUILayout.Space(UCL_GUIStyle.GetScaledSize(20));
                }
            }
        }
        public static void OnGUI(UCL.Core.UCL_ObjectDictionary iDataDic)
        {
            if (!s_Inited) return;
            using (var aScope = new GUILayout.HorizontalScope("box"))
            {
                if (GUILayout.Button("Clear", UCL_GUIStyle.ButtonStyle))
                {
                    s_Logs.Clear();
                }
            }
            if (s_Logs.IsNullOrEmpty())
            {
                return;
            }
            for (int i = 0; i < s_Logs.Count; i++)
            {
                var aLog = s_Logs[i];
                if (s_LogTypeFilter[aLog.m_Type])
                {
                    aLog.OnGUI(iDataDic.GetSubDic("Logs", i));
                }
            }
        }
        public static void ThreadedLog(string message, string stack_trace = "", LogType type = LogType.Log)
        {
            if (string.IsNullOrEmpty(stack_trace))
            {//type != LogType.Log
                stack_trace = new System.Diagnostics.StackTrace(true).ToString();
            }
            lock (s_Logs)
            {
                s_Logs.Add(new LogData(message, stack_trace, type));
            }
        }
    }
}