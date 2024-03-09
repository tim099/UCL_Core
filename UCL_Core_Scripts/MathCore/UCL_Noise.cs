
// ATS_AutoHeader
// to change the auto header please go to ATS_AutoHeader.cs
// Create time : 03/09 2024 12:41
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.MathLib
{
    public static class UCL_Noise
    {
        
        //public static float PerlinNoise3D(float x, float y, float z) {
        //    float noise = 0;
        //    noise += Mathf.PerlinNoise(x, y);
        //    noise -= Mathf.PerlinNoise(x, z);
        //    noise -= Mathf.PerlinNoise(y, z);

        //    noise -= Mathf.PerlinNoise(y, x);
        //    noise += Mathf.PerlinNoise(z, x);
        //    noise += Mathf.PerlinNoise(z, y);

        //    return noise;// / 6.0f;
        //}
        //public static float PerlinNoise3D(Vector3 pos) {
        //    return PerlinNoise3D(pos.x, pos.y, pos.z);
        //}

        public static float PerlinNoise3D(Vector3 iPos)
        {
            return PerlinNoise3D(iPos.x, iPos.y, iPos.z);
        }
        public static float PerlinNoise3D(float x, float y, float z)
        {
            return 0.5f * PerlinNoise(x, y, z) + 0.5f;
        }

        public static float PerlinNoise(float x, float y, float z)
        {
            var X = Mathf.FloorToInt(x) & 0xff;
            var Y = Mathf.FloorToInt(y) & 0xff;
            var Z = Mathf.FloorToInt(z) & 0xff;
            x -= Mathf.Floor(x);
            y -= Mathf.Floor(y);
            z -= Mathf.Floor(z);
            var u = Fade(x);
            var v = Fade(y);
            var w = Fade(z);
            var A = (s_Perm[X] + Y) & 0xff;
            var B = (s_Perm[X + 1] + Y) & 0xff;
            var AA = (s_Perm[A] + Z) & 0xff;
            var BA = (s_Perm[B] + Z) & 0xff;
            var AB = (s_Perm[A + 1] + Z) & 0xff;
            var BB = (s_Perm[B + 1] + Z) & 0xff;
            var val = Lerp(w, Lerp(v, Lerp(u, Grad(s_Perm[AA], x, y, z), Grad(s_Perm[BA], x - 1, y, z)),
                                   Lerp(u, Grad(s_Perm[AB], x, y - 1, z), Grad(s_Perm[BB], x - 1, y - 1, z))),
                           Lerp(v, Lerp(u, Grad(s_Perm[AA + 1], x, y, z - 1), Grad(s_Perm[BA + 1], x - 1, y, z - 1)),
                                   Lerp(u, Grad(s_Perm[AB + 1], x, y - 1, z - 1), Grad(s_Perm[BB + 1], x - 1, y - 1, z - 1))));
            if (val > 1.0f) val = 1.0f;
            if (val < -1.0f) val = -1.0f;
            return val;
        }

        public static float PerlinNoise(Vector3 pos)
        {
            return PerlinNoise(pos.x, pos.y, pos.z);
        }

        public static float PerlinNoise(float x)
        {
            var X = Mathf.FloorToInt(x) & 0xff;
            x -= Mathf.Floor(x);
            var u = Fade(x);
            return Lerp(u, Grad(s_Perm[X], x), Grad(s_Perm[X + 1], x - 1)) * 2;
        }
        /// <summary>
        /// range between 0~1
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <returns></returns>
        public static float PerlinNoiseUnsigned(float x, float y)
        {
            return 0.5f * PerlinNoise(x, y) + 0.5f;
        }
        /// <summary>
        /// PerlinNoise (range between -1~1)
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <returns></returns>
        public static float PerlinNoise(float x, float y)
        {
            var X = Mathf.FloorToInt(x) & 0xff;
            var Y = Mathf.FloorToInt(y) & 0xff;
            x -= Mathf.Floor(x);
            y -= Mathf.Floor(y);
            var u = Fade(x);
            var v = Fade(y);
            var A = (s_Perm[X] + Y) & 0xff;
            var B = (s_Perm[X + 1] + Y) & 0xff;
            var val = Lerp(v, Lerp(u, Grad(s_Perm[A], x, y), Grad(s_Perm[B], x - 1, y)),
                           Lerp(u, Grad(s_Perm[A + 1], x, y - 1), Grad(s_Perm[B + 1], x - 1, y - 1)));
            if (val > 1.0f) val = 1.0f;
            else if (val < -1.0f) val = -1.0f;

            return val;//Mathf.Clamp(val, -1, 1)
        }

        public static float PerlinNoise(Vector2 pos)
        {
            return PerlinNoise(pos.x, pos.y);
        }

        public static float Fbm(float x, int octave)
        {
            var f = 0.0f;
            var w = 0.5f;
            for (var i = 0; i < octave; i++)
            {
                f += w * PerlinNoise(x);
                x *= 2.0f;
                w *= 0.5f;
            }
            return f;
        }

        public static float Fbm(float x, float y, int octave)
        {
            var f = 0.0f;
            var w = 0.5f;
            for (var i = 0; i < octave; i++)
            {
                f += w * PerlinNoise(x, y);
                x *= 2.0f;
                y *= 2.0f;
                w *= 0.5f;
            }
            return f;
        }

        public static float Fbm(Vector2 pos, int octave)
        {
            return Fbm(pos.x, pos.y, octave);
        }

        public static float Fbm(float x, float y, float z, int octave)
        {
            var f = 0.0f;
            var w = 0.5f;
            for (var i = 0; i < octave; i++)
            {
                f += w * PerlinNoise(x, y);
                x *= 2.0f;
                y *= 2.0f;
                z *= 2.0f;
                w *= 0.5f;
            }
            return f;
        }

        public static float Fbm(Vector3 pos, int octave)
        {
            return Fbm(pos.x, pos.y, pos.z, octave);
        }
        #region Private
        static float Fade(float t)
        {
            return t * t * t * (t * (t * 6 - 15) + 10);
        }

        static float Lerp(float t, float a, float b)
        {
            return a + t * (b - a);
        }

        static float Grad(int hash, float x)
        {
            return (hash & 1) == 0 ? x : -x;
        }

        static float Grad(int hash, float x, float y)
        {
            return ((hash & 1) == 0 ? x : -x) + ((hash & 2) == 0 ? y : -y);
        }

        static float Grad(int hash, float x, float y, float z)
        {
            var h = hash & 15;
            var u = h < 8 ? x : y;
            var v = h < 4 ? y : (h == 12 || h == 14 ? x : z);
            return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
        }
        /// <summary>
        /// Generate new Perm for Noise
        /// https://en.wikipedia.org/wiki/Rijndael_S-box
        /// </summary>
        static public void GeneratePerm()
        {
            //UCL.Core.MathLib.UCL_Random.Instance.Shuffle(s_Perm);

            //TODO 
        }
        /// <summary>
        /// Lenth = 257 and values between 0~255(inclusive)
        /// </summary>
        static int[] s_Perm = {
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
            138,236,205,93,222,114,67,29,24,72,243,141,128,195,78,66,215,61,156,180,151
        };
        #endregion
    }
}