﻿using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace UCL.Core.LocalizeLib
{
    public class LocalizeData
    {
        protected Dictionary<string, string> m_Dic = new Dictionary<string, string>();
        public LocalizeData(string data) {
            ParseData(data);
        }
        virtual public Dictionary<string, string> GetDic() {
            return m_Dic;
        }
        virtual public void ParseData(string data) {
            m_Dic.Clear();
            using(StringReader reader = new StringReader(data)) {
                var hex = new char[4];
                bool parsing = true;
                StringBuilder aStringBuilder = new StringBuilder();
                string key = null;
                int phase = 0;
                while(parsing) {
                    if(reader.Peek() == -1) {
                        parsing = false;
                        break;
                    }
                    char c = System.Convert.ToChar(reader.Read());
                    //Debug.LogWarning("c:" + c);
                    switch(c) {
                        case '"': {
                                phase++;
                                switch(phase) {
                                    case 1:
                                        {//Key Start
                                            aStringBuilder.Clear();
                                            break;
                                        }
                                    case 2: {//Key Enter
                                            key = aStringBuilder.ToString();
                                            break;
                                        }
                                    case 3: {//Value Start
                                            aStringBuilder.Clear();
                                            break;
                                        }
                                    case 4: {//Value End
                                            if(m_Dic.ContainsKey(key)) {
                                                Debug.LogError("ParseData key:" + key + " already exist!!,Value:" + aStringBuilder.ToString());
                                            } else {
                                                //Debug.LogWarning("Add" + key + "," + s.ToString());
                                                m_Dic.Add(key, aStringBuilder.ToString());
                                            }
                                            aStringBuilder.Clear();
                                            phase = 0;
                                            break;
                                        }
                                }
                                break;
                            }

                        case '\\': {
                                if(reader.Peek() == -1) {
                                    parsing = false;
                                    break;
                                }

                                c = System.Convert.ToChar(reader.Read());
                                switch(c) {
                                    case '"':
                                    case '\\':
                                    case '/':
                                        aStringBuilder.Append(c);
                                        break;
                                    case 'b':
                                        aStringBuilder.Append('\b');
                                        break;
                                    case 'f':
                                        aStringBuilder.Append('\f');
                                        break;
                                    case 'n':
                                        aStringBuilder.Append('\n');
                                        break;
                                    case 'r':
                                        aStringBuilder.Append('\r');
                                        break;
                                    case 't':
                                        aStringBuilder.Append('\t');
                                        break;
                                    case 'u':
                                        for(int i = 0; i < 4; i++) {
                                            hex[i] = System.Convert.ToChar(reader.Read());
                                        }
                                        aStringBuilder.Append((char)System.Convert.ToInt32(new string(hex), 16));
                                        break;
                                }
                                break;
                            }
                        default:
                            aStringBuilder.Append(c);
                            break;
                    }
                }
            }
        }
        virtual public string GetLocalize(string key) {
            if(!m_Dic.ContainsKey(key)) {
                //Debug.Log("LocalizeData not contain key:" + key);
                return key;
            }
            return m_Dic[key];
        }
    }
}