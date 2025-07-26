
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core
{
    public static partial class RandomExtensions
    {
        /// <summary>
        /// Random pick a element in the input list
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="list"></param>
        /// <returns></returns>
        public static T RandomPick<T>(this IList<T> list)
        {
            if (list == null || list.Count == 0)
            {
                return default;
            }
            return list[Random.Range(0, list.Count)];
        }
        /// <summary>
        /// Random pick a element in the input IList
        /// Weight is the HitRate of item
        /// etc. A,B,C in iList, and A weight is 3, B is 2, C is 1
        /// then the HitRate of A is 1/2, B is 1/3 and c is 1/6
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="list"></param>
        /// <param name="getWeightFunc"></param>
        /// <returns></returns>
        public static T RandomPickByWeight<T>(this IList<T> list, System.Func<T, float> getWeightFunc)
        {
            if (list == null || list.Count == 0)
            {
                return default;
            }
            float totalWeight = 0;
            float[] weights = new float[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                float weight = getWeightFunc(list[i]);
                totalWeight += weight;
                weights[i] = weight;
            }

            float pickWeight = Random.Range(0, totalWeight);

            for (int pickAt = 0; pickAt < list.Count; pickAt++)
            {
                float weight = weights[pickAt];
                pickWeight -= weight;
                if (pickWeight <= 0)
                {
                    return list[pickAt];
                }
            }
            return list.LastElement();
        }
        /// <summary>
        /// Random pick a element in the input IList
        /// Weight is the HitRate of item
        /// etc. A,B,C in iList, and A weight is 3, B is 2, C is 1
        /// then the HitRate of A is 1/2, B is 1/3 and c is 1/6
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="list"></param>
        /// <param name="getWeightFunc"></param>
        /// <returns></returns>
        public static T RandomPickByWeight<T>(this IList<T> list, System.Func<T, int> getWeightFunc)
        {
            if (list == null || list.Count == 0)
            {
                return default;
            }
            int totalWeight = 0;
            int[] weights = new int[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                int weight = getWeightFunc(list[i]);
                totalWeight += weight;
                weights[i] = weight;
            }

            int pickWeight = Random.Range(0, totalWeight);

            for (int pickAt = 0; pickAt < list.Count; pickAt++)
            {
                int weight = weights[pickAt];
                pickWeight -= weight;
                if (pickWeight <= 0)
                {
                    return list[pickAt];
                }
            }
            return list.LastElement();
        }
    }
}
