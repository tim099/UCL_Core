using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.MathLib {
    public class RangeChecker<T> where T: IComparable {
        public void Init(T min,T max) {
            Min = min;
            Max = max;
            Inited = true;
        }
        public void AddValue(T val) {
            if(!Inited) {
                Init(val,val);
                return;
            }
            if(val.CompareTo(Max) > 0) {
                Max = val;
            }else if(val.CompareTo(Min) < 0) {
                Min = val;
            }
        }
        bool Inited = false;
        public T Min;
        public T Max;
    }
    public static class Noise {
        /*
        public static float PerlinNoise3D(float x, float y, float z) {
            float noise = 0;
            noise += Mathf.PerlinNoise(x, y);
            noise -= Mathf.PerlinNoise(x, z);
            noise -= Mathf.PerlinNoise(y, z);

            noise -= Mathf.PerlinNoise(y, x);
            noise += Mathf.PerlinNoise(z, x);
            noise += Mathf.PerlinNoise(z, y);

            return noise;// / 6.0f;
        }
        public static float PerlinNoise3D(Vector3 pos) {
            return PerlinNoise3D(pos.x, pos.y, pos.z);
        }
        */

        public static float PerlinNoiseUnsigned(float x, float y, float z) {
            return  0.5f*PerlinNoise(x, y, z) + 0.5f;
        }

        public static float PerlinNoise(float x, float y, float z) {
            var X = Mathf.FloorToInt(x) & 0xff;
            var Y = Mathf.FloorToInt(y) & 0xff;
            var Z = Mathf.FloorToInt(z) & 0xff;
            x -= Mathf.Floor(x);
            y -= Mathf.Floor(y);
            z -= Mathf.Floor(z);
            var u = Fade(x);
            var v = Fade(y);
            var w = Fade(z);
            var A = (perm[X] + Y) & 0xff;
            var B = (perm[X + 1] + Y) & 0xff;
            var AA = (perm[A] + Z) & 0xff;
            var BA = (perm[B] + Z) & 0xff;
            var AB = (perm[A + 1] + Z) & 0xff;
            var BB = (perm[B + 1] + Z) & 0xff;
            var val = Lerp(w, Lerp(v, Lerp(u, Grad(perm[AA], x, y, z), Grad(perm[BA], x - 1, y, z)),
                                   Lerp(u, Grad(perm[AB], x, y - 1, z), Grad(perm[BB], x - 1, y - 1, z))),
                           Lerp(v, Lerp(u, Grad(perm[AA + 1], x, y, z - 1), Grad(perm[BA + 1], x - 1, y, z - 1)),
                                   Lerp(u, Grad(perm[AB + 1], x, y - 1, z - 1), Grad(perm[BB + 1], x - 1, y - 1, z - 1))));
            if(val > 1.0f) val = 1.0f;
            if(val < -1.0f) val = -1.0f;
            return val;
        }

        public static float PerlinNoise(Vector3 pos) {
            return PerlinNoise(pos.x, pos.y, pos.z);
        }
        //*/
        public static float PerlinNoise(float x) {
            var X = Mathf.FloorToInt(x) & 0xff;
            x -= Mathf.Floor(x);
            var u = Fade(x);
            return Lerp(u, Grad(perm[X], x), Grad(perm[X + 1], x - 1)) * 2;
        }
        public static float PerlinNoiseUnsigned(float x, float y) {
            return 0.5f * PerlinNoise(x, y) + 0.5f;
        }
        
        public static float PerlinNoise(float x, float y) {
            var X = Mathf.FloorToInt(x) & 0xff;
            var Y = Mathf.FloorToInt(y) & 0xff;
            x -= Mathf.Floor(x);
            y -= Mathf.Floor(y);
            var u = Fade(x);
            var v = Fade(y);
            var A = (perm[X] + Y) & 0xff;
            var B = (perm[X + 1] + Y) & 0xff;
            var val = Lerp(v, Lerp(u, Grad(perm[A], x, y), Grad(perm[B], x - 1, y)),
                           Lerp(u, Grad(perm[A + 1], x, y - 1), Grad(perm[B + 1], x - 1, y - 1)));
            if(val > 1.0f) val = 1.0f;
            if(val < -1.0f) val = -1.0f;
            return val;
        }

        public static float PerlinNoise(Vector2 pos) {
            return PerlinNoise(pos.x, pos.y);
        }

        public static float Fbm(float x, int octave) {
            var f = 0.0f;
            var w = 0.5f;
            for(var i = 0; i < octave; i++) {
                f += w * PerlinNoise(x);
                x *= 2.0f;
                w *= 0.5f;
            }
            return f;
        }

        public static float Fbm(float x, float y, int octave) {
            var f = 0.0f;
            var w = 0.5f;
            for(var i = 0; i < octave; i++) {
                f += w * PerlinNoise(x,y);
                x *= 2.0f;
                y *= 2.0f;
                w *= 0.5f;
            }
            return f;
        }

        public static float Fbm(Vector2 pos, int octave) {
            return Fbm(pos.x, pos.y, octave);
        }

        public static float Fbm(float x, float y, float z, int octave) {
            var f = 0.0f;
            var w = 0.5f;
            for(var i = 0; i < octave; i++) {
                f += w * PerlinNoise(x,y);
                x *= 2.0f;
                y *= 2.0f;
                z *= 2.0f;
                w *= 0.5f;
            }
            return f;
        }

        public static float Fbm(Vector3 pos, int octave) {
            return Fbm(pos.x, pos.y, pos.z, octave);
        }
        #region Private
        static float Fade(float t) {
            return t * t * t * (t * (t * 6 - 15) + 10);
        }

        static float Lerp(float t, float a, float b) {
            return a + t * (b - a);
        }

        static float Grad(int hash, float x) {
            return (hash & 1) == 0 ? x : -x;
        }

        static float Grad(int hash, float x, float y) {
            return ((hash & 1) == 0 ? x : -x) + ((hash & 2) == 0 ? y : -y);
        }

        static float Grad(int hash, float x, float y, float z) {
            var h = hash & 15;
            var u = h < 8 ? x : y;
            var v = h < 4 ? y : (h == 12 || h == 14 ? x : z);
            return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
        }

        static int[] perm = {
            151,160,137,91,90,15,
            131,13,201,95,96,53,194,233,7,225,140,36,103,30,69,142,8,99,37,240,21,10,23,
            190, 6,148,247,120,234,75,0,26,197,62,94,252,219,203,117,35,11,32,57,177,33,
            88,237,149,56,87,174,20,125,136,171,168, 68,175,74,165,71,134,139,48,27,166,
            77,146,158,231,83,111,229,122,60,211,133,230,220,105,92,41,55,46,245,40,244,
            102,143,54, 65,25,63,161, 1,216,80,73,209,76,132,187,208, 89,18,169,200,196,
            135,130,116,188,159,86,164,100,109,198,173,186, 3,64,52,217,226,250,124,123,
            5,202,38,147,118,126,255,82,85,212,207,206,59,227,47,16,58,17,182,189,28,42,
            223,183,170,213,119,248,152, 2,44,154,163, 70,221,153,101,155,167, 43,172,9,
            129,22,39,253, 19,98,108,110,79,113,224,232,178,185, 112,104,218,246,97,228,
            251,34,242,193,238,210,144,12,191,179,162,241, 81,51,145,235,249,14,239,107,
            49,192,214, 31,181,199,106,157,184, 84,204,176,115,121,50,45,127, 4,150,254,
            138,236,205,93,222,114,67,29,24,72,243,141,128,195,78,66,215,61,156,180,
            151
        };
        #endregion
    }
    public static class Lib {
        #region Lerp
        public static Vector3 Lerp(Vector3 a,Vector3 b,float val) {
            var del = b - a;
            return a + del * val;
        }
        public static Quaternion Lerp(Quaternion a, Quaternion b, float val) {
            //return Slerp(a, b, val);
            Quaternion r;
            float _val = 1 - val;
            r.x = _val * a.x + val * b.x;
            r.y = _val * a.y + val * b.y;
            r.z = _val * a.z + val * b.z;
            r.w = _val * a.w + val * b.w;
            r.Normalize();
            return r;
        }
        public static Quaternion Slerp(Quaternion a, Quaternion b, float val) {
            Quaternion r;
            float _val = 1 - val;
            float Wa, Wb;
            float theta = Mathf.Acos(a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w);
            float sn = Mathf.Sin(theta);
            Wa = Mathf.Sin(_val * theta) / sn;
            Wb = Mathf.Sin(val * theta) / sn;
            r.x = Wa * a.x + Wb * b.x;
            r.y = Wa * a.y + Wb * b.y;
            r.z = Wa * a.z + Wb * b.z;
            r.w = Wa * a.w + Wb * b.w;
            r.Normalize();
            return r;
        }
        #endregion
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
    public static class LinearMapping {
        /// <summary>
        /// Convert function into line
        /// </summary>
        /// <param name="func"></param>
        /// <param name="seg_count"></param>
        /// <returns></returns>
        static public List<float> ConvertFunction(System.Func<float,float> func,int seg_count) {
            var datas = new List<float>();
            float del = 1f / seg_count;
            for(int i = 0; i < seg_count + 1; i++) {
                var x = del * i;
                datas.Add(func(x));
            }
            return datas;
        }


        /// <summary>
        /// GetValue will return data at val position using lerp
        /// </summary>
        /// <param name="datas"></param> 
        /// <param name="x"></param> val should between 0f ~ 1f
        /// <returns></returns>
        static public float GetY(List<float> datas, float x) {
            if(datas == null || datas.Count == 0) return x;
            if(datas.Count == 1) return datas[0];
            if(x > 1) x = 1;
            if(x < 0) x = 0;
            if(datas.Count == 2) return Mathf.Lerp(datas[0], datas[1], x);
            float pos = x * (datas.Count - 1);
            int cur = Mathf.FloorToInt(pos);
            if(cur >= datas.Count - 1) cur = datas.Count - 2;
            float lerp_pos = pos - cur;
            float a = datas[cur];
            float b = datas[cur + 1];
            float magnitude = b - a;
            float seg_val = lerp_pos / magnitude;
            //Debug.LogWarning("a:" + a + ",b:" + b + ",val:" + val+ ",lerp_pos:"+ lerp_pos);
            return Mathf.Lerp(a, b, lerp_pos);
        }
        static public float GetX(List<float> datas, float y) {
            if(datas == null || datas.Count <= 1) return y;
            float prev = datas[0];
            float cur = prev;
            int i = 1;
            for(; i < datas.Count; i++) {
                cur = datas[i];
                if(prev <= y && y <= cur) {
                    break;
                }
                prev = cur;
            }
            float del = cur - prev;
            float seg_pos = 1;
            if(del > 0) {
                seg_pos = (y - prev) / del;
            }
            //Debug.LogWarning("GetPosition seg_pos:" + seg_pos+ ",i:"+ i);
            float pos = ((i - 1 + seg_pos) / (float)(datas.Count - 1));
            if(pos > 1) pos = 1;
            //pos +=  / datas.Count;
            return pos;
        }
    }
}

