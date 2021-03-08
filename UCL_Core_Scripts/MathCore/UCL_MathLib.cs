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
    public static partial class Extensions {
        #region Vector2
        public static Vector2 ToTextureSpace(this Vector2 pos) {
            return new Vector2(0.5f + 0.5f * pos.x, 0.5f + 0.5f * pos.y);
        }
        public static Vector2 ToWorldSpace(this Vector2 pos) {
            return new Vector2(2f * pos.x - 1f, 2f * pos.y - 1f);
        }
        #endregion
    }
    public static class Lib {
        #region Geometry 2D
        /// <summary>
        /// reference https://stackoverflow.com/questions/2049582/how-to-determine-if-a-point-is-in-a-2d-triangle
        /// </summary>
        /// <param name="p1"></param>
        /// <param name="p2"></param>
        /// <param name="p3"></param>
        /// <returns></returns>
        public static float Sign(Vector2 p1, Vector2 p2, Vector2 p3) {
            return (p1.x - p3.x) * (p2.y - p3.y) - (p2.x - p3.x) * (p1.y - p3.y);
        }
        /// <summary>
        /// https://stackoverflow.com/questions/2049582/how-to-determine-if-a-point-is-in-a-2d-triangle
        /// </summary>
        /// <param name="pt"></param>
        /// <param name="v1"></param>
        /// <param name="v2"></param>
        /// <param name="v3"></param>
        /// <returns></returns>
        public static bool CheckWithinTriangle(Vector2 pt, Vector2 v1, Vector2 v2, Vector2 v3) {
            float d1 = Sign(pt, v1, v2);
            float d2 = Sign(pt, v2, v3);
            float d3 = Sign(pt, v3, v1);

            return !(((d1 < 0) || (d2 < 0) || (d3 < 0)) && ((d1 > 0) || (d2 > 0) || (d3 > 0)));
        }


        #endregion

        #region Lerp
        /// <summary>
        /// return the cubic
        /// </summary>
        /// <param name="val"></param>
        /// <returns></returns>
        public static float Cbrt(float val) {
            const float ot = 1f / 3f;
            return Mathf.Pow(val, ot);
        }
        public static float Abs(float val) {
            return val < 0 ? -val : val;
        }
        public static int Abs(int val) {
            if(val >= 0) return val;
            if(val == int.MinValue) {
                //return int.MaxValue;
                throw new OverflowException();//overflow!!
            }
            return -val;
        }
        public static int Lerp(int a, int b, float val)
        {
            return Mathf.RoundToInt(a * (1f - val) + b * val);
        }
        public static float Lerp(float a, float b, float val) {
            return a * (1f - val) + b * val;
        }
        public static Vector3 Lerp(Vector3 a,Vector3 b,float val) {
            return a * (1f - val) + b * val;
        }
        public static Vector2 Lerp(Vector2 a, Vector2 b, float val) {
            return a * (1f - val) + b * val;
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
        public static float BackFolding(float iVal, float iMidpoint)
        {
            if (iVal == iMidpoint)
            {
                return 1f;
            }
            if (iVal < iMidpoint)
            {
                return iVal / iMidpoint;
            }

            return (1f - iVal) / (1f - iMidpoint);
        }
        #endregion
        #region Round & Floor & Ceil
        public static long RoundToLong(float val) {
            long result = (long)val;
            float del = val - result;
            if(del >= 0.5f) ++result;
            else if(del < -0.5f) --result;
            return result;
        }
        public static long FloorToLong(float val) {
            long result = (long)val;
            float del = val - result;
            if(del < 0f) --result;
            return result;
        }
        public static long CeilToLong(float val) {
            long result = (long)val;
            float del = val - result;
            if(del > 0f) ++result;
            return result;
        }
        #endregion
        #region Diamond-Square


        public static void DiamondSquare(float[,] arr) {
            DiamondSquare(arr, arr.GetLength(0), arr.GetLength(0) - 1, MathLib.UCL_Random.Instance);
        }
        public static void DiamondSquare(float[,] arr, MathLib.UCL_Random rnd) {
            DiamondSquare(arr, arr.GetLength(0), arr.GetLength(0) - 1, rnd);
        }
        public static void DiamondSquare(float[,] arr, int step, MathLib.UCL_Random rnd) {
            DiamondSquare(arr, step, step - 1, rnd);
        }
        public static void DiamondSquare(float[,] arr, int sx, int sz, int range, MathLib.UCL_Random rnd) {
            DiamondSquare(arr, range, sx, sz, range - 1, rnd);
        }
        //Author Nick O'Brien
        //reference https://medium.com/@nickobrien/diamond-square-algorithm-explanation-and-c-implementation-5efa891e486f
        public static void DiamondSquare(float[,] arr, int range, int step, MathLib.UCL_Random rnd) {
            int half = step / 2;
            if(half < 1) return;

            //square steps
            for(int z = half; z < range; z += step)
                for(int x = half; x < range; x += step)
                    SquareStep(arr, range, x, z, half, rnd);
            
            // diamond steps
            int col = 0;
            for(int x = 0; x < range; x += half) {
                col++;
                //If this is an odd column.
                if(col % 2 == 1)
                    for(int z = half; z < range; z += step)
                        DiamondStep(arr, range, x, z, half, rnd);
                else
                    for(int z = 0; z < range; z += step)
                        DiamondStep(arr, range, x, z, half, rnd);
            }
            //return;
            DiamondSquare(arr, range, step / 2, rnd);
            return;
        }
        static void SquareStep(float[,] arr, int range, int x, int z, int reach, MathLib.UCL_Random rnd) {
            int count = 0;
            float avg = 0.0f;
            if(x - reach >= 0 && z - reach >= 0) {
                avg += arr[x - reach,z - reach];
                count++;
            }
            if(x - reach >= 0 && z + reach < range) {
                avg += arr[x - reach,z + reach];
                count++;
            }
            if(x + reach < range && z - reach >= 0) {
                avg += arr[x + reach,z - reach];
                count++;
            }
            if(x + reach < range && z + reach < range) {
                avg += arr[x + reach,z + reach];
                count++;
            }
            float val = reach / (float)range;
            avg += rnd.Range(-val, val);
            avg /= count;
            arr[x,z] = avg;
        }
        static void DiamondStep(float[,] arr, int range, int x, int z, int reach, MathLib.UCL_Random rnd) {
            //Debug.LogWarning("diamondStep x:" + x +",z:" + z);
            int count = 0;
            float avg = 0.0f;
            if(x - reach >= 0) {
                avg += arr[x - reach,z];
                count++;
            }
            if(x + reach < range) {
                avg += arr[x + reach,z];
                count++;
            }
            if(z - reach >= 0) {
                avg += arr[x,z - reach];
                count++;
            }
            if(z + reach < range) {
                avg += arr[x,z + reach];
                count++;
            }
            float val = reach/(float)range;
            avg += rnd.Range(-val, val);
            avg /= count;
            arr[x,z] = avg;
        }

        static void DiamondSquare(float[,] arr, int range,int sx,int sz, int size, MathLib.UCL_Random rnd) {
            int half = size / 2;
            if(half < 1) return;

            //square steps
            for(int z = half; z < range; z += size)
                for(int x = half; x < range; x += size)
                    SquareStep(arr, range, sx, sz, x, z, half, rnd);

            // diamond steps
            int col = 0;
            for(int x = 0; x < range; x += half) {
                col++;
                //If this is an odd column.
                if(col % 2 == 1)
                    for(int z = half; z < range; z += size)
                        DiamondStep(arr, range, sx, sz, x, z, half, rnd);
                else
                    for(int z = 0; z < range; z += size)
                        DiamondStep(arr, range, sx, sz, x, z, half, rnd);
            }
            //return;
            DiamondSquare(arr, range, sx, sz, size / 2, rnd);
            return;
        }
        static void SquareStep(float[,] arr, int range, int sx, int sz, int x, int z, int reach, MathLib.UCL_Random rnd) {
            //Debug.LogWarning("SquareStep sx:" + sx+ ",sz:" + sz + ",x:" + x + ",z:" + z);
            int count = 0;
            float avg = 0.0f;
            if(x - reach >= 0 && z - reach >= 0) {
                avg += arr[x - reach + sx, z - reach + sz];
                count++;
            }
            if(x - reach >= 0 && z + reach < range) {
                avg += arr[x - reach + sx, z + reach + sz];
                count++;
            }
            if(x + reach < range && z - reach >= 0) {
                avg += arr[x + reach + sx, z - reach + sz];
                count++;
            }
            if(x + reach < range && z + reach < range) {
                avg += arr[x + reach + sx, z + reach + sz];
                count++;
            }
            float val = reach / (float)range;
            avg += rnd.Range(-val, val);
            avg /= count;
            arr[x + sx, z + sz] = avg;
        }
        static void DiamondStep(float[,] arr, int range, int sx, int sz, int x, int z, int reach, MathLib.UCL_Random rnd) {
            //Debug.LogWarning("diamondStep x:" + x +",z:" + z);
            int count = 0;
            float avg = 0.0f;
            if(x - reach >= 0) {
                avg += arr[x - reach + sx, z + sz];
                count++;
            }
            if(x + reach < range) {
                avg += arr[x + reach + sx, z + sz];
                count++;
            }
            if(z - reach >= 0) {
                avg += arr[x + sx, z - reach + sz];
                count++;
            }
            if(z + reach < range) {
                avg += arr[x + sx, z + reach + sz];
                count++;
            }
            float val = reach / (float)range;
            avg += rnd.Range(-val, val);
            avg /= count;
            arr[x + sx, z + sz] = avg;
        }
        #endregion
        #region Misc
        public static int PowTwo(int power) { return 1 << power; }
        public static bool IsPowerOfTwo(long x) {
            return (x != 0) && (x & (x - 1)) == 0;
        }
        public static bool IsPowerOfTwo(int x) {
            //Debug.LogWarning("x:" + x + ",IsPowerOfTwo:" + ((x != 0) && (x & (x - 1)) == 0));
            return (x != 0) && (x & (x - 1)) == 0;
        }
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
        #endregion
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

        static public List<float> GetLength(List<Vector3> points) {
            List<float> point_dis = new List<float>();
            float total_len = 0;
            point_dis.Add(0);
            for(int i = 0, len = points.Count - 1; i < len; i++) {
                float seg_len = (points[i] - points[i + 1]).magnitude;
                total_len += seg_len;
                point_dis.Add(total_len);
            }
            for(int i = 0, len = point_dis.Count; i < len; i++) {
                point_dis[i] /= total_len;
            }
            return point_dis;
        }
        static public List<float> GetLength<T>(List<T> points, System.Func<T,T,float> magnitude_func) {
            List<float> point_dis = new List<float>();
            float total_len = 0;
            point_dis.Add(0);
            for(int i = 0, len = points.Count - 1; i < len; i++) {
                float seg_len = magnitude_func(points[i], points[i + 1]);
                total_len += seg_len;
                point_dis.Add(total_len);
            }
            for(int i = 0, len = point_dis.Count; i < len; i++) {
                point_dis[i] /= total_len;
            }
            return point_dis;
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
            //float magnitude = b - a;
            //float seg_val = lerp_pos / magnitude;
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

        static public Vector3 GetValue(List<Vector3> datas,float x) {
            return GetValue(datas, x, MathLib.Lib.Lerp);
        }
        static public Vector2 GetValue(List<Vector2> datas, float x) {
            return GetValue(datas, x, MathLib.Lib.Lerp);
        }
        /// <summary>
        /// GetValue will return data at val position using lerp
        /// </summary>
        /// <param name="datas"></param> 
        /// <param name="x"></param> val should between 0f ~ 1f
        /// <returns></returns>
        static public T GetValue<T>(List<T> datas, float x , System.Func<T,T,float, T> lerp_func) {
            if(datas == null || datas.Count == 0) return default;
            if(datas.Count == 1) return datas[0];
            if(x > 1) x = 1;
            if(x < 0) x = 0;
            if(datas.Count == 2) return lerp_func(datas[0], datas[1], x);
            float pos = x * (datas.Count - 1);
            int cur = Mathf.FloorToInt(pos);
            if(cur >= datas.Count - 1) cur = datas.Count - 2;
            float lerp_pos = pos - cur;
            //Debug.LogWarning("a:" + a + ",b:" + b + ",val:" + val+ ",lerp_pos:"+ lerp_pos);
            return lerp_func(datas[cur], datas[cur + 1], lerp_pos);
        }
    }
    public static class Num {
        public static bool IsNumber<T>() {
            if(typeof(T) == typeof(sbyte)
                || typeof(T) == typeof(byte)
                || typeof(T) == typeof(short)
                || typeof(T) == typeof(ushort)
                || typeof(T) == typeof(int)
                || typeof(T) == typeof(uint)
                || typeof(T) == typeof(long)
                || typeof(T) == typeof(ulong)
                || typeof(T) == typeof(float)
                || typeof(T) == typeof(double)
                || typeof(T) == typeof(decimal)) {
                return true;
            }
            return false;
        }
        /// <summary>
        /// Can parse number into specify type
        /// </summary>
        /// <typeparam name="T">result Type</typeparam>
        /// <param name="number">input number</param>
        /// <param name="result">result value</param>
        /// <returns>return true if success</returns>
        public static bool TryParse<T>(string number, out T result) where T : IConvertible {
            bool flag = false;
            if(typeof(T) == typeof(int)) {
                int res = 0;
                flag = int.TryParse(number, out res);
                result = (T)Convert.ChangeType(res, typeof(T));
            } else if(typeof(T) == typeof(uint)) {
                uint res = 0;
                flag = uint.TryParse(number, out res);
                result = (T)Convert.ChangeType(res, typeof(T));
            } else if(typeof(T) == typeof(long)) {
                long res = 0;
                flag = long.TryParse(number, out res);
                result = (T)Convert.ChangeType(res, typeof(T));
            } else if(typeof(T) == typeof(ulong)) {
                ulong res = 0;
                flag = ulong.TryParse(number, out res);
                result = (T)Convert.ChangeType(res, typeof(T));
            } else if(typeof(T) == typeof(short)) {
                short res = 0;
                flag = short.TryParse(number, out res);
                result = (T)Convert.ChangeType(res, typeof(T));
            } else if(typeof(T) == typeof(ushort)) {
                ushort res = 0;
                flag = ushort.TryParse(number, out res);
                result = (T)Convert.ChangeType(res, typeof(T));
            } else if(typeof(T) == typeof(sbyte)) {
                sbyte res = 0;
                flag = sbyte.TryParse(number, out res);
                result = (T)Convert.ChangeType(res, typeof(T));
            } else if(typeof(T) == typeof(byte)) {
                byte res = 0;
                flag = byte.TryParse(number, out res);
                result = (T)Convert.ChangeType(res, typeof(T));
            } else if(typeof(T) == typeof(float)) {
                float res = 0;
                flag = float.TryParse(number, out res);
                result = (T)Convert.ChangeType(res, typeof(T));
            } else if(typeof(T) == typeof(double)) {
                double res = 0;
                flag = double.TryParse(number, out res);
                result = (T)Convert.ChangeType(res, typeof(T));
            } else if(typeof(T) == typeof(decimal)) {
                decimal res = 0;
                flag = decimal.TryParse(number, out res);
                result = (T)Convert.ChangeType(res, typeof(T));
            } else {
                result = default;
            }

            return flag;
        }
        /// <summary>
        ///  Can parse number into specify type
        /// </summary>
        /// <param name="number">input number</param>
        /// <param name="type">type of result</param>
        /// <param name="result">result value</param>
        /// <returns>return true if success</returns>
        public static bool TryParse(string number, Type type, out object result){
            bool flag = false;
            if(type == typeof(int)) {
                int res = 0;
                flag = int.TryParse(number, out res);
                result = res;
            } else if(type == typeof(uint)) {
                uint res = 0;
                flag = uint.TryParse(number, out res);
                result = res;
            } else if(type == typeof(long)) {
                long res = 0;
                flag = long.TryParse(number, out res);
                result = res;
            } else if(type == typeof(ulong)) {
                ulong res = 0;
                flag = ulong.TryParse(number, out res);
                result = res;
            } else if(type == typeof(short)) {
                short res = 0;
                flag = short.TryParse(number, out res);
                result = res;
            } else if(type == typeof(ushort)) {
                ushort res = 0;
                flag = ushort.TryParse(number, out res);
                result = res;
            } else if(type == typeof(sbyte)) {
                sbyte res = 0;
                flag = sbyte.TryParse(number, out res);
                result = res;
            } else if(type == typeof(byte)) {
                byte res = 0;
                flag = byte.TryParse(number, out res);
                result = res;
            } else if(type == typeof(float)) {
                float res = 0;
                flag = float.TryParse(number, out res);
                result = res;
            } else if(type == typeof(double)) {
                double res = 0;
                flag = double.TryParse(number, out res);
                result = res;
            } else if(type == typeof(decimal)) {
                decimal res = 0;
                flag = decimal.TryParse(number, out res);
                result = res;
            } else {
                result = default;
            }

            return flag;
        }
    }
}

