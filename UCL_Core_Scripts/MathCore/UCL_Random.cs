using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace UCL.Core.MathLib {
    public static partial class Extensions {
        public static RandomState GetState(this System.Random random) {
            var binaryFormatter = new BinaryFormatter();
            using(var temp = new MemoryStream()) {
                binaryFormatter.Serialize(temp, random);
                return new RandomState(temp.ToArray());
            }
        }
    }

    [System.Serializable]
    public struct RandomState {
        public byte[] m_State;
        public RandomState(byte[] state) {
            m_State = state;
        }
        public System.Random CreateRandom() {   
            using(var temp = new MemoryStream(m_State)) {
                var binaryFormatter = new BinaryFormatter();
                return (System.Random)binaryFormatter.Deserialize(temp);
            }
        }
    }
    public class UCL_Random {
        protected static UCL_Random _Instance = null; 
        public static UCL_Random Instance {
            get {
                if(_Instance == null) {
                    _Instance = new UCL_Random();
                }
                return _Instance;
            }
            set {
                _Instance = value;
            }
        }
        public int m_Seed;
        protected System.Random m_Rnd;

        public UCL_Random(int _Seed = 0) {
            m_Seed = _Seed;
            m_Rnd = new System.Random(m_Seed);
        }
        public UCL_Random() {
            m_Seed = MathLib.Crc32.Sum(System.DateTime.Now);
            m_Rnd = new System.Random(m_Seed);
        }

        #region Color

        public Color ColorHSV(float hueMin, float hueMax,
            float saturationMin, float saturationMax,
            float valueMin, float valueMax,
            float alphaMin, float alphaMax) {
            float hue = Range(hueMin, hueMax);
            float saturation = Range(saturationMin, saturationMax);
            float value = Range(valueMin, valueMax);
            float alpha = Range(alphaMin, alphaMax);
            Color col = Color.HSVToRGB(hue, saturation, value);
            col.a = alpha;
            return col;
        }
        public Color ColorHSV(float hueMin, float hueMax, float saturationMin, float saturationMax,float valueMin, float valueMax) {
            float hue = Range(hueMin, hueMax);
            float saturation = Range(saturationMin, saturationMax);
            float value = Range(valueMin, valueMax);
            return Color.HSVToRGB(hue, saturation, value);
        }
        public Color ColorHSV(float hueMin, float hueMax, float saturationMin, float saturationMax) {
            float hue = Range(hueMin, hueMax);
            float saturation = Range(saturationMin, saturationMax);
            return Color.HSVToRGB(hue, saturation, 1f);
        }
        public Color ColorHSV(float hueMin, float hueMax) {
            float hue = Range(hueMin, hueMax);
            return Color.HSVToRGB(hue, 1f, 1f);
        }
        public Color ColorHSV() {
            float hue = Range(0, 1f);
            return Color.HSVToRGB(hue, 1f, 1f);
        }
        #endregion

        #region Vector

        public Vector2 InRect(float width, float height) {
            return new Vector2(Range(0f, width), Range(0f, height));
        }

        public Vector2 OnRect(float width, float height) {
            float dd = Range(0f, 2*(width+height));
            dd -= width;
            if(dd <= 0) {
                return new Vector2(Range(0f, width), 0);
            }
            dd -= width;
            if(dd <= 0) {
                return new Vector2(Range(0f, width), height);
            }
            dd -= height;
            if(dd <= 0) {
                return new Vector2(0 , Range(0f, height));
            }
            return new Vector2(width, Range(0f, height));
        }

        /// <summary>
        /// return a random point x,y,z range in 0 ~ 1
        /// </summary>
        /// <returns></returns>
        public Vector3 InUnitCube() {
            return new Vector3(Range(0f, 1f), Range(0f, 1f), Range(0f, 1f));
        }
        public Vector3 OnUnitCube() {
            float pos = Range(0f, 1f);
            float pos2 = Range(0f, 1f);
            switch(Next(6)) {
                case 0: return new Vector3(pos, pos2, 0);
                case 1: return new Vector3(pos, pos2, 1);
                case 2: return new Vector3(pos, 0, pos2);
                case 3: return new Vector3(pos, 1, pos2);
                case 4: return new Vector3(0, pos, pos2);
            }
            return new Vector3(1, pos, pos2);
        }
        /// <summary>
        /// return a random point x,y range in 0 ~ 1
        /// </summary>
        /// <returns></returns>
        public Vector2 InUnitSquare() {
            return new Vector2(Range(0f, 1f), Range(0f, 1f));
        }
        public Vector2 OnUnitSquare() {
            float pos = Range(0f, 1f);
            switch(Next(4)) {
                case 0:return new Vector2(pos, 0);
                case 1: return new Vector2(pos, 1);
                case 2: return new Vector2(0, pos);
            }
            return new Vector2(1, pos);
        }
        public Vector2 OnUnitCircle() {
            float angle = Range(0, Mathf.PI * 2f);
            return new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
        }
        /// <summary>
        /// return random point on circle between min_radius and max_radius
        /// </summary>
        /// <param name="min_radius">min radius of point</param>
        /// <param name="max_radius">max angle of point</param>
        /// <returns></returns>
        public Vector2 OnUnitCircle(float min_radius, float max_radius) {
            float angle = Range(min_radius, max_radius);//*Mathf.Deg2Rad
            return new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
        }
        public Vector2 InUnitCircle() {
            float r = NextFloat();
            return Mathf.Sqrt(r) * OnUnitCircle();
        }

        public Vector3 OnUnitSphere() {
            var xy = OnUnitCircle();
            var z = Range(-1f, 1f);
            float r = Mathf.Sqrt(1 - z * z);//MathLib.Lib.Cbrt

            return new Vector3(r*xy.x, r*xy.y, z);
        }
        public Vector3 InUnitSphere() {
            var xy = InUnitCircle();
            var z = Range(-1f, 1f);
            bool neg = false;
            if(z < 0) {
                neg = true;
                z = -z;
            }
            z = 1 - Mathf.Sqrt(z);
            float r = Mathf.Sqrt(1 - z * z);//MathLib.Lib.Cbrt
            if(neg) z = -z;
            return new Vector3(r * xy.x, r * xy.y, z);
        }
        #endregion
        #region Range
        /// <summary>
        /// Return a random float number between min [inclusive] and max [inclusive]
        /// </summary>
        /// <param name="min"></param>
        /// <param name="max"></param>
        /// <returns></returns>
        public float Range(float min, float max) {
            return MathLib.Lib.Lerp(min, max, NextFloat());
        }
        /// <summary>
        /// Return a random float number between min [inclusive] and max [exclusive]
        /// </summary>
        /// <param name="min"></param>
        /// <param name="max"></param>
        /// <returns></returns>
        public int Range(int min, int max) {
            return m_Rnd.Next(min, max);
        }
        /// <summary>
        /// Return a random Vector3 number between min [inclusive] and max [inclusive]
        /// </summary>
        /// <param name="min"></param>
        /// <param name="max"></param>
        /// <returns></returns>
        public Vector3 Range(Vector3 min, Vector3 max) {
            return MathLib.Lib.Lerp(min, max, NextFloat());
        }

        /// <summary>
        /// Return a random Vector2 number between min [inclusive] and max [inclusive]
        /// </summary>
        /// <param name="min"></param>
        /// <param name="max"></param>
        /// <returns></returns>
        public Vector2 Range(Vector2 min, Vector2 max) {
            return MathLib.Lib.Lerp(min, max, NextFloat());
        }
        #endregion


        #region Next
        /// <summary>
        /// Return a random float number between 0 [inclusive] and max [inclusive]
        /// </summary>
        /// <param name="max">Max value of random number</param>
        /// <returns></returns>
        public float NextFloat(float max) {
            return max * (m_Rnd.Next() / (float)(int.MaxValue - 1));
        }

        /// <summary>
        /// Return a random float number between 0 [inclusive] and 1 [inclusive]
        /// </summary>
        /// <returns></returns>
        public float NextFloat() {
            return (m_Rnd.Next() / (float)(int.MaxValue - 1));
        }
        /// <summary>
        /// Return a random double number between 0 [inclusive] and max [inclusive]
        /// </summary>
        /// <param name="max"></param>
        /// <returns></returns>
        public double NextDouble(double max) {
            int val = m_Rnd.Next();
            return max * (val / (double)(int.MaxValue - 1));
        }
        #endregion

        /// <summary>
        /// Return a random float number between 0 [inclusive] and (int.MaxValue - 1) [inclusive]
        /// </summary>
        /// <returns></returns>
        public int Next() {
            return m_Rnd.Next();
        }
        public int Next(int max) {
            return m_Rnd.Next(max);
        }
        /// <summary>
        /// Get current random state
        /// </summary>
        /// <returns></returns>
        public RandomState GetState() {
            return m_Rnd.GetState();
        }

        /// <summary>
        /// Restore random state
        /// </summary>
        /// <param name="state"></param>
        public void SetState(RandomState state) {
            m_Rnd = state.CreateRandom();
        }
    }
}
