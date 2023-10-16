using System.Collections;
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
        virtual public void ParseData(string iData) {
            //Debug.LogError($"LocalizeData.ParseData iData:{iData}");
            m_Dic.Clear();
            using(StringReader aReader = new StringReader(iData)) {
                var hex = new char[4];
                bool parsing = true;
                StringBuilder aStringBuilder = new StringBuilder();
                string iKey = null;
                int phase = 0;
                while(parsing) {
                    if(aReader.Peek() == -1) {
                        parsing = false;
                        break;
                    }
                    char c = System.Convert.ToChar(aReader.Read());
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
                                            iKey = aStringBuilder.ToString();
                                            break;
                                        }
                                    case 3: {//Value Start
                                            aStringBuilder.Clear();
                                            break;
                                        }
                                    case 4: {//Value End
                                            if(m_Dic.ContainsKey(iKey)) {
                                                Debug.LogError("ParseData key:\"" + iKey + "\" already exist!!,New Value:\"" + aStringBuilder.ToString()
                                                    + "\",OldValue:\"" + m_Dic[iKey]+ "\"");
                                            } else {
                                                //Debug.LogWarning("Add" + key + "," + s.ToString());
                                                m_Dic.Add(iKey, aStringBuilder.ToString());
                                            }
                                            aStringBuilder.Clear();
                                            phase = 0;
                                            break;
                                        }
                                }
                                break;
                            }

                        case '\\': {
                                if(aReader.Peek() == -1) {
                                    parsing = false;
                                    break;
                                }

                                c = System.Convert.ToChar(aReader.Read());
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
                                            hex[i] = System.Convert.ToChar(aReader.Read());
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
        /// <summary>
        /// Get localized of the key
        /// if iKey not exist localize, then return iKey
        /// </summary>
        /// <param name="iKey"></param>
        /// <returns></returns>
        virtual public string GetLocalize(string iKey) {
            if(!m_Dic.ContainsKey(iKey)) {
                //Debug.LogWarning("LocalizeData not contain key:" + iKey);
                return iKey;
            }
            return m_Dic[iKey];
        }
        /// <summary>
        /// Set localized by key
        /// </summary>
        /// <param name="iKey"></param>
        /// <param name="iValue">Localized string</param>
        virtual public void SetLocalize(string iKey, string iValue)
        {
            m_Dic[iKey] = iValue;
        }
        /// <summary>
        /// Check if localize of iKey exist
        /// </summary>
        /// <param name="iKey"></param>
        /// <returns></returns>
        virtual public bool ContainsKey(string iKey)
        {
            return m_Dic.ContainsKey(iKey);
        }
    }
}