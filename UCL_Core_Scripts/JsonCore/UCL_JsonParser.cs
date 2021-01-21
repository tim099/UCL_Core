using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UCL.Core.JsonLib {
    internal class JsonParser : IDisposable {
        enum Token {
            None,
            Brace_L,
            Brace_R,
            Bracket_L,
            Bracket_R,
            Col,
            Comma,
            String,
            Num,
            True,
            False,
            Null
        };
        StringReader m_Reader;

        public JsonParser(string str) {
            m_Reader = new StringReader(str);
            //GC.SuppressFinalize(this);
        }
        ~JsonParser() {
            Dispose();
        }
        public void Dispose() {
            if(m_Reader == null) return;
            m_Reader.Dispose();
            m_Reader = null;
        }
        Dictionary<string, object> ParseObject() {
            Dictionary<string, object> table = new Dictionary<string, object>();
            m_Reader.Read();
            while(true) {
                switch(GetNextToken()) {
                    case Token.None:
                        return null;
                    case Token.Comma:
                        continue;
                    case Token.Brace_R:
                        return table;
                    default:
                        string name = ParseString();
                        if(name == null) return null;
                        if(GetNextToken() != Token.Col) return null;
                        
                        m_Reader.Read();
                        table[name] = Parse();
                        break;
                }
            }
        }

        List<object> ParseArray() {
            List<object> arr = new List<object>();
            m_Reader.Read();
            var parsing = true;
            while(parsing) {
                Token nextToken = GetNextToken();

                switch(nextToken) {
                    case Token.None:
                        return null;
                    case Token.Comma:
                        continue;
                    case Token.Bracket_R:
                        parsing = false;
                        break;
                    default:
                        object value = ParseByToken(nextToken);
                        arr.Add(value);
                        break;
                }
            }
            return arr;
        }

        internal object Parse() {
            return ParseByToken(GetNextToken());
        }

        object ParseByToken(Token token) {
            switch(token) {
                case Token.String: return ParseString();
                case Token.Num: return ParseNumber();
                case Token.Brace_L: return ParseObject();
                case Token.Bracket_L: return ParseArray();
                case Token.True: return true;
                case Token.False: return false;
                case Token.Null: return null;                    
            }
            return null;
        }

        string ParseString() {
            StringBuilder aStringBuilder = new StringBuilder();
            char c;
            m_Reader.Read();

            bool parsing = true;
            while(parsing) {
                if(m_Reader.Peek() == -1) {
                    parsing = false;
                    break;
                }
                c = Convert.ToChar(m_Reader.Read());
                switch(c) {
                    case '"':
                        parsing = false;
                        break;
                    case '\\':
                        if(m_Reader.Peek() == -1) {
                            parsing = false;
                            break;
                        }

                        c = Convert.ToChar(m_Reader.Read());
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
                                var hex = new char[4];
                                for(int i = 0; i < 4; i++) {
                                    hex[i] = Convert.ToChar(m_Reader.Read());
                                }
                                aStringBuilder.Append((char)Convert.ToInt32(new string(hex), 16));
                                break;
                        }
                        break;
                    default:
                        aStringBuilder.Append(c);
                        break;
                }
            }

            return aStringBuilder.ToString();
        }

        object ParseNumber() {
            string number = GetNextWord();

            if(number.IndexOf('.') == -1) {
                int parsedInt32;
                if(Int32.TryParse(number, out parsedInt32))
                    return parsedInt32;

                long parsedInt64;
                Int64.TryParse(number, out parsedInt64);
                return parsedInt64;
            }

            double parsedDouble;
            Double.TryParse(number, out parsedDouble);
            return parsedDouble;
        }

        void SkipWhiteSpace() {
            int peekchar = m_Reader.Peek();
            while(peekchar != -1 && char.IsWhiteSpace(Convert.ToChar(peekchar))) {
                m_Reader.Read();//Read WhiteSpace
                peekchar = m_Reader.Peek();
            }
        }

        static HashSet<char> BreakSet = new HashSet<char>() {'{','}','[',']',',',':','"'};
        public static bool IsWordBreak(char c) {
            return char.IsWhiteSpace(c) || BreakSet.Contains(c);
        }

        string GetNextWord() {
            int peek_val = m_Reader.Peek();
            StringBuilder word = new StringBuilder();

            while(peek_val != -1 && !IsWordBreak(Convert.ToChar(peek_val))) {
                word.Append(Convert.ToChar(m_Reader.Read()));
                peek_val = m_Reader.Peek();
            }

            return word.ToString();
        }

        Token GetNextToken() {
            SkipWhiteSpace();
            int peek_val = m_Reader.Peek();
            if(peek_val == -1) {
                return Token.None;
            }
            char peek = Convert.ToChar(peek_val);
            switch(peek) {
                case '{': {
                        return Token.Brace_L;
                    }
                case '}': {
                        m_Reader.Read();
                        return Token.Brace_R;
                    }
                case '[':
                    return Token.Bracket_L;
                case ']':
                    m_Reader.Read();
                    return Token.Bracket_R;
                case ',':
                    m_Reader.Read();
                    return Token.Comma;
                case '"':
                    return Token.String;
                case ':':
                    return Token.Col;

                case '0':
                case '1':
                case '2':
                case '3':
                case '4':
                case '5':
                case '6':
                case '7':
                case '8':
                case '9':
                case '-':
                    return Token.Num;
            }

            switch(GetNextWord()) {
                case "false":
                    return Token.False;
                case "true":
                    return Token.True;
                case "null":
                    return Token.Null;
            }

            return Token.None;
        }
    }
}