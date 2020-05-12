using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.MathLib {
    public static class Lib {
        public static int FindMaxAt<T>(T[] arr) where T : System.IComparable {
            if(arr.Length <= 1) return 0;
            T max = arr[0];
            int at = 0;
            for(int i = 1; i < arr.Length; i++) {
                if(max.CompareTo(arr[i]) < 0) {
                    max = arr[i];
                    at = i;
                }
            }
            return at;
        }
        public static int FindMinAt<T>(T[] arr) where T : System.IComparable {
            if(arr.Length <= 1) return 0;
            T max = arr[0];
            int at = 0;
            for(int i = 1; i < arr.Length; i++) {
                if(arr[i].CompareTo(max) < 0) {
                    max = arr[i];
                    at = i;
                }
            }
            return at;
        }
        public static int SqrtInt(int a) {
            if(a <= 0) return 0;
            if(a == 1) return 1;

            int res = Mathf.CeilToInt(Mathf.Sqrt(a));
            while(res * res < a) {
                Debug.LogWarning("SqrtInt Error!!res * res < a");
                res++;
            }
            if((res - 1) * (res - 1) >= a) {
                Debug.LogWarning("SqrtInt Error!!(res - 1) * (res - 1) >= a");
                res--;
            }
            return res;
        }
        public static int Atoi(string str) {
            int at = 0, result = 0;
            int str_len = str.Length;
            while(at < str_len && str[at] == ' ') at++;
            if(at >= str_len) return 0;
            char c = str[at];
            bool sign = false;
            if(c == '+' || c == '-' || (c >= '0' && c <= '9')) {
                if(c == '-') {
                    sign = true;
                    at++;
                } else if(c == '+') {
                    at++;
                }
            } else {
                return 0;//invalid string
            }
            for(; at < str_len; at++) {
                c = str[at];
                if(c >= '0' && c <= '9') {
                    int val = c - '0';
                    if(result > 214748365) {
                        return sign ? -2147483648 : 2147483647;
                    }
                    result *= 10;

                    if(sign) {
                        if(result > 2147483648 - val) return -2147483648;
                    } else {
                        if(result > 2147483647 - val) return 2147483647;
                    }
                    result += val;
                } else {
                    break;
                }
            }
            return sign ? -result : result;
        }

        public static string IntToString(int val, string format = "<sprite={0}>") {
            string out_str = "";
            string str = val.ToString();
            for(int i = 0; i < str.Length; i++) {
                char c = str[i];
                out_str += string.Format(format, c);
            }
            return out_str;
        }
        public static int Repeat(int index, int Loop) {
            if(Loop <= 0) return 0;
            index = index % Loop;
            while(index < 0) index += Loop;

            return index;
        }
        static void Swap<T>(ref T lhs, ref T rhs) {
            T temp;
            temp = lhs;
            lhs = rhs;
            rhs = temp;
        }
        public static bool CheckBetween(float index, float a, float b) {
            if(a > b) Swap(ref a, ref b);

            if(a <= index && index <= b) return true;

            return false;
        }
        public static Vector3 nearest_point_on_line(Vector3 line_point_a, Vector3 line_point_b, Vector3 point) {
            Vector3 lineDir = line_point_b - line_point_a;
            lineDir.Normalize();
            Vector3 v = point - line_point_a;
            float d = Vector3.Dot(v, lineDir);

            return line_point_a + lineDir * d;
        }
        public static float truncate(float value, int digits) {
            float mult = Mathf.Pow(10, digits);
            float result = (Mathf.RoundToInt(mult * value) / mult);
            return result;
        }
        public static float distance_to_line(Vector3 line_point_a, Vector3 line_point_b, Vector3 point) {
            Vector3 line_vec = line_point_b - line_point_a;
            Vector3 lineDir = line_vec.normalized;
            Vector3 v = point - line_point_a;
            float d = Vector3.Dot(v, lineDir);
            Vector3 p_c = line_point_a;
            if(d < 0) {
                //p_c = line_point_a;
            } else if(d > line_vec.magnitude) {
                p_c = line_point_b;
            } else {
                p_c = line_point_a + lineDir * d;
            }
            return (point - p_c).magnitude;
        }
    }
}

