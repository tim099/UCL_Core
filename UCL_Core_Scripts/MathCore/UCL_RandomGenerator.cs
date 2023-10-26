using System;
namespace UCL.Core.MathLib
{
    [System.Serializable]
    public struct RandomState
    {
        public byte[] m_State;
        public string HexStringState => UCL.Core.MarshalLib.Lib.ByteArrayToHexString(m_State);
        public RandomState(string iHexStringState)
        {
            //UnityEngine.Debug.LogError("iHexStringState.Len:" + iHexStringState.Length+ ",iHexStringState:"+ iHexStringState);
            m_State = UCL.Core.MarshalLib.Lib.HexStringToByteArray(iHexStringState);
        }
        public RandomState(byte[] iState)
        {
            m_State = iState;
        }
        /// <summary>
        /// Create a System.Random by random state
        /// </summary>
        /// <returns></returns>
        public UCL_RandomGenerator CreateRandomGenerator()
        {
            return new UCL_RandomGenerator(m_State);
        }
    }
    [Serializable]
    public class UCL_RandomGenerator
    {
        private const int SEED = 161803398;
        const int StateCount = 56;
        private int NextID {
            get => m_State[StateCount];
            set => m_State[StateCount] = value;
        }
        private int NextPID
        {
            get => m_State[StateCount + 1];
            set => m_State[StateCount + 1] = value;
        }
        /// <summary>
        /// current random state
        /// </summary>
        private int[] m_State = new int[StateCount + 2];


        public RandomState State => new RandomState(m_State.ToByteArray());
        public UCL_RandomGenerator() {
            InitState(MathLib.Crc32.Sum(System.DateTime.Now));
        }
        /// <summary>
        /// Init with iSeed
        /// </summary>
        /// <param name="iSeed">Random seed</param>
        public UCL_RandomGenerator(int iSeed)
        {
            InitState(iSeed);
        }
        public UCL_RandomGenerator(byte[] iState)
        {
            SetState(iState);
        }
        public void SetState(byte[] iState)
        {
            m_State = iState.ToArray<int>();
        }
        /// <summary>
        /// Init Random state with Seed
        /// </summary>
        /// <param name="iSeed"></param>
        public void InitState(int iSeed)
        {
            int aNextPID = 21;
            NextID = 0;
            NextPID = aNextPID;
            int aSubtraction = (iSeed == Int32.MinValue) ? Int32.MaxValue : Math.Abs(iSeed);
            int aJ = SEED - aSubtraction;
            int aK = 1;

            m_State[StateCount - 1] = aJ;
            for (int i = 1; i < (StateCount - 1); i++) {
                int aI = (aNextPID * i) % (StateCount - 1);
                m_State[aI] = aK;
                aK = aJ - aK;
                if (aK < 0) aK += Int32.MaxValue;
                aJ = m_State[aI];
            }
            for (int k = 1; k < 5; k++)
            {
                for (int i = 1; i < StateCount; i++)
                {
                    m_State[i] -= m_State[1 + (i + 30) % (StateCount - 1)];
                    if (m_State[i] < 0) m_State[i] += Int32.MaxValue;
                }
            }

        }
        protected virtual double Sample() => Next() * (1.0 / Int32.MaxValue);
        /// <summary>
        /// Return a random int between 0 [inclusive] and (int.MaxValue - 1) [inclusive]
        /// </summary>
        /// <returns></returns>
        public virtual int Next()
        {
            int aRetVal;
            int aLocINext = NextID;
            int aLocINextp = NextPID;

            if (++aLocINext >= 56) aLocINext = 1;
            if (++aLocINextp >= 56) aLocINextp = 1;

            aRetVal = m_State[aLocINext] - m_State[aLocINextp];

            if (aRetVal == Int32.MaxValue) aRetVal--;
            if (aRetVal < 0) aRetVal += Int32.MaxValue;

            m_State[aLocINext] = aRetVal;

            NextID = aLocINext;
            NextPID = aLocINextp;

            return aRetVal;
        }
        private double GetSampleForLargeRange()
        {
            int aResult = Next();
            if (Next() % 2 == 0)
            {
                aResult = -aResult;
            }
            double aVal = aResult;
            aVal += (Int32.MaxValue - 1);
            aVal /= 2 * (uint)Int32.MaxValue - 1;
            return aVal;
        }

        public virtual int Next(int iMin, int iMax)
        {
            if (iMin > iMax)
            {
                int aMin = iMax;
                iMax = iMin;
                iMin = aMin;
            }

            long aRange = (long)iMax - iMin;
            if (aRange <= (long)Int32.MaxValue)
            {
                return ((int)(Sample() * aRange) + iMin);
            }
            else
            {
                return (int)((long)(GetSampleForLargeRange() * aRange) + iMin);
            }
        }

        /// <summary>
        /// Return a Random value between 0 ~ iMax
        /// </summary>
        /// <param name="iMax"></param>
        /// <returns></returns>
        public virtual int Next(int iMax)
        {
            if (iMax < 0)
            {
                return 0;
            }
            return (int)(Sample() * iMax);
        }

        /// <summary>
        /// Return a double Value between 0~1
        /// </summary>
        /// <returns></returns>
        public virtual double NextDouble()
        {
            return Sample();
        }

        /// <summary>
        /// Fills the byte array with random bytes
        /// </summary>
        /// <param name="iBuffer"></param>
        public virtual void NextBytes(byte[] iBuffer)
        {
            if (iBuffer == null) return;
            for (int i = 0; i < iBuffer.Length; i++)
            {
                iBuffer[i] = (byte)(Next() % (Byte.MaxValue + 1));
            }
        }
    }
}