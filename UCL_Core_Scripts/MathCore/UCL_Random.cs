using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using UnityEngine;

namespace UCL.Core.MathLib
{
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
        public string HexStringState
        {
            get
            {
                return UCL.Core.MarshalLib.Lib.ByteArrayToHexString(m_State);
            }
        }
        public RandomState(string iHexStringState)
        {
            m_State = UCL.Core.MarshalLib.Lib.HexStringToByteArray(iHexStringState);
        }
        public RandomState(byte[] iState) {
            m_State = iState;
        }
        /// <summary>
        /// Create a System.Random by random state
        /// </summary>
        /// <returns></returns>
        public System.Random CreateRandom() {   
            using(var aMemoryStream = new MemoryStream(m_State)) {
                var aBinaryFormatter = new BinaryFormatter();
                return (System.Random)aBinaryFormatter.Deserialize(aMemoryStream);
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
        protected System.Random m_Rnd = null;

        public UCL_Random(int iSeed = 0) {
            m_Seed = iSeed;
            m_Rnd = new System.Random(m_Seed);
        }
        public UCL_Random() {
            m_Seed = MathLib.Crc32.Sum(System.DateTime.Now);
            m_Rnd = new System.Random(m_Seed);
        }
        #region Array & List
        /// <summary>
        /// Using Fisher–Yates shuffle from https://gaohaoyang.github.io/2016/10/16/shuffle-algorithm/
        /// Shuffle the element in the input Array
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="iArray"></param>
        /// <returns></returns>
        public T[] Shuffle<T>(T[] iArray) {
            for(var i = iArray.Length - 1; i >= 0; i--) {
                var randomIndex = Next(i + 1);
                var itemAtIndex = iArray[randomIndex];

                iArray[randomIndex] = iArray[i];
                iArray[i] = itemAtIndex;
            }
            return iArray;
        }
        /// <summary>
        /// Shuffle the element in the input list
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="iList">input list</param>
        /// <returns></returns>
        public List<T> Shuffle<T>(List<T> iList) {
            for(var i = iList.Count - 1; i >= 0; i--) {
                var randomIndex = Next(i + 1);
                var itemAtIndex = iList[randomIndex];

                iList[randomIndex] = iList[i];
                iList[i] = itemAtIndex;
            }
            return iList;
        }
        /// <summary>
        /// Random pick a element in the input IList
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="iList"></param>
        /// <returns></returns>
        public T RandomPick<T>(IList<T> iList)
        {
            if(iList == null || iList.Count == 0)
            {
                return default;
            }
            return iList[Next(iList.Count)];
        }
        /// <summary>
        /// Random pick a element in the input IList
        /// Weight is the HitRate of item
        /// etc. A,B,C in iList, and A weight is 3, B is 2, C is 1
        /// then the HitRate of A is 1/2, B is 1/3 and c is 1/6
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="iList"></param>
        /// <param name="iGetWeightFunc"></param>
        /// <returns></returns>
        public T RandomPick<T>(IList<T> iList, System.Func<T, int> iGetWeightFunc)
        {
            if (iList == null || iList.Count == 0)
            {
                return default;
            }
            int aTotalWeight = 0;
            int[] aWeights = new int[iList.Count];
            for (int i = 0; i < iList.Count; i++)
            {
                int aWeight = iGetWeightFunc(iList[i]);
                aTotalWeight += aWeight;
                aWeights[i] = aWeight;
            }

            int aPickWeight = Next(aTotalWeight);

            for (int aPickAt = 0; aPickAt < iList.Count; aPickAt++)
            {
                int aWeight = aWeights[aPickAt];
                aPickWeight -= aWeight;
                if (aPickWeight < 0)
                {
                    return iList[aPickAt];
                }
            }
            return iList.LastElement();
        }
        /// <summary>
        /// Random pick n elements from the input list(n is iPickCount)
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="iList"></param>
        /// <param name="iPickCount"></param>
        /// <returns></returns>
        public List<T> RandomPick<T>(IList<T> iList, int iPickCount)
        {
            if (iList == null || iList.Count == 0)
            {
                return default;
            }
            List<T> aResult = new List<T>();
            if (iPickCount >= iList.Count)
            {
                for(int i = 0; i < iList.Count; i++)
                {
                    aResult.Add(iList[i]);
                }
                return aResult;
            }
            List<T> aPool = new List<T>();
            for (int i = 0; i < iList.Count; i++)
            {
                aPool.Add(iList[i]);
            }

            for (int i = 0; i < iPickCount; i++)
            {
                int aPickAt = Next(aPool.Count);
                aResult.Add(aPool[aPickAt]);
                aPool.RemoveAt(aPickAt);
            }
            return aResult;
        }

        /// <summary>
        /// Random pick n elements from the input list(n is iPickCount)
        /// Weight is the HitRate of item
        /// etc. A,B,C in iList, and A weight is 3, B is 2, C is 1
        /// then the HitRate of A is 1/2, B is 1/3 and c is 1/6
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="iList"></param>
        /// <param name="iPickCount"></param>
        /// <param name="iGetWeightFunc">input T and return the weight of T</param>
        /// <returns></returns>
        public List<T> RandomPick<T>(IList<T> iList, int iPickCount, System.Func<T,int> iGetWeightFunc)
        {
            if (iList == null || iList.Count == 0)
            {
                return default;
            }
            List<T> aResult = new List<T>();
            if (iPickCount >= iList.Count)
            {
                for (int i = 0; i < iList.Count; i++)
                {
                    aResult.Add(iList[i]);
                }
                return aResult;
            }
            List<T> aPool = new List<T>();
            List <int> aWeights = new List<int>();
            int aTotalWeight = 0;
            for (int i = 0; i < iList.Count; i++)
            {
                var aItem = iList[i];
                aPool.Add(aItem);
                int aWeight = iGetWeightFunc(aItem);
                aWeights.Add(aWeight);
                aTotalWeight += aWeight;
            }

            for (int i = 0; i < iPickCount; i++)
            {
                int aPickWeight = Next(aTotalWeight);
                
                for (int aPickAt = 0; aPickAt < aPool.Count; aPickAt++)
                {
                    int aWeight = aWeights[aPickAt];
                    aPickWeight -= aWeight;
                    if (aPickWeight < 0)
                    {
                        aTotalWeight -= aWeight;
                        aResult.Add(aPool[aPickAt]);
                        aPool.RemoveAt(aPickAt);
                        aWeights.RemoveAt(aPickAt);
                        break;
                    }
                    else if(aPickAt == aPool.Count - 1)
                    {
                        Debug.LogError("RandomPick Pick Fail!! aPickWeight:" + aPickWeight);
                    }
                }
            }
            return aResult;
        }
        /// <summary>
        /// Random pick a element in the input array
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="iList"></param>
        /// <returns></returns>
        public T RandomPick<T>(T[] iList)
        {
            if (iList == null || iList.Length == 0)
            {
                return default;
            }
            return iList[Next(iList.Length)];
        }
        #endregion

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
        /// <summary>
        /// return a random point on UnitSphere
        /// </summary>
        /// <returns></returns>
        public Vector3 OnUnitSphere() {
            var xy = OnUnitCircle();
            var z = Range(-1f, 1f);
            float r = Mathf.Sqrt(1 - z * z);//MathLib.Lib.Cbrt

            return new Vector3(r*xy.x, r*xy.y, z);
        }
        /// <summary>
        /// return a random point inside UnitSphere
        /// </summary>
        /// <returns></returns>
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
        /// <param name="iMin"></param>
        /// <param name="iMax"></param>
        /// <returns></returns>
        public Vector2 Range(Vector2 iMin, Vector2 iMax) {
            return MathLib.Lib.Lerp(iMin, iMax, NextFloat());
        }
        #endregion


        #region Next
        /// <summary>
        /// Return a random float number between 0 [inclusive] and max [inclusive]
        /// </summary>
        /// <param name="iMax">Max value of random number</param>
        /// <returns></returns>
        public float NextFloat(float iMax) {
            return iMax * (Next() / (float)(int.MaxValue - 1));
        }

        /// <summary>
        /// Return a random float number between 0 [inclusive] and 1 [inclusive]
        /// </summary>
        /// <returns></returns>
        public float NextFloat() {
            return (Next() / (float)(int.MaxValue - 1));
        }
        /// <summary>
        /// Return a random double number between 0 [inclusive] and max [inclusive]
        /// </summary>
        /// <param name="iMax"></param>
        /// <returns></returns>
        public double NextDouble(double iMax) {
            int val = Next();
            return iMax * (val / (double)(int.MaxValue - 1));
        }
        #endregion

        /// <summary>
        /// Return a random int between 0 [inclusive] and (int.MaxValue - 1) [inclusive]
        /// </summary>
        /// <returns></returns>
        public int Next() {
            return m_Rnd.Next();
        }
        /// <summary>
        /// Return a random int between 0 [inclusive] and iMax [exclusive]
        /// </summary>
        /// <param name="iMax"></param>
        /// <returns></returns>
        public int Next(int iMax) {
            return m_Rnd.Next(iMax);
        }
        /// <summary>
        /// Return a random float number between iMin [inclusive] and iMax [exclusive]
        /// </summary>
        /// <param name="iMin"></param>
        /// <param name="iMax"></param>
        /// <returns></returns>
        public int Range(int iMin, int iMax)
        {
            if (iMin > iMax) return m_Rnd.Next(iMax, iMin);

            return m_Rnd.Next(iMin, iMax);
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
        /// <param name="iState"></param>
        public void SetState(RandomState iState) {
            m_Rnd = iState.CreateRandom();
        }
    }
}
