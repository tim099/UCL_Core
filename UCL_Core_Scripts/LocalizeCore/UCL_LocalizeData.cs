using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace UCL.Core.LocalizeLib
{
    public class LocalizeData
    {
        Dictionary<string, string> m_Dic = new Dictionary<string, string>();
        public LocalizeData(string data) {
            ParseData(data);
        }
        virtual public void ParseData(string data) {
            m_Dic.Clear();
            using(StringReader reader = new StringReader(data)) {
                var hex = new char[4];
                bool parsing = true;
                StringBuilder s = new StringBuilder();
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
                                    case 1: {
                                            s.Clear();
                                            break;
                                        }
                                    case 2: {
                                            key = s.ToString();
                                            break;
                                        }
                                    case 3: {
                                            s.Clear();
                                            break;
                                        }
                                    case 4: {
                                            if(m_Dic.ContainsKey(key)) {
                                                Debug.LogError("ParseData key:" + key + " already exist!!");
                                            } else {
                                                Debug.LogWarning("Add" + key + "," + s.ToString());
                                                m_Dic.Add(key, s.ToString());
                                            }
                                            s.Clear();
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
                                        s.Append(c);
                                        break;
                                    case 'b':
                                        s.Append('\b');
                                        break;
                                    case 'f':
                                        s.Append('\f');
                                        break;
                                    case 'n':
                                        s.Append('\n');
                                        break;
                                    case 'r':
                                        s.Append('\r');
                                        break;
                                    case 't':
                                        s.Append('\t');
                                        break;
                                    case 'u':
                                        for(int i = 0; i < 4; i++) {
                                            hex[i] = System.Convert.ToChar(reader.Read());
                                        }
                                        s.Append((char)System.Convert.ToInt32(new string(hex), 16));
                                        break;
                                }
                                break;
                            }
                        default:
                            s.Append(c);
                            break;
                    }
                }
            }
        }
        virtual public string GetLocalize(string key) {
            if(!m_Dic.ContainsKey(key)) {
                Debug.LogWarning("LocalizeData not contain key:" + key);
                return key;
            }
            return m_Dic[key];
        }
    }
}