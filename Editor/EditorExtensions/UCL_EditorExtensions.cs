using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor.Animations;
using UnityEngine;

namespace UCL.Core
{
    public static class UCL_EditorExtensions
    {

    }
    public static partial class UCL_EditorAnimatorExtensions
    {
        static Dictionary<Animator, Dictionary<int, string>> s_StatesNameHash = new();
        public static HashSet<string> EditorGetAllStatesName(this Animator animator)
        {

            HashSet<string> names = new HashSet<string>();
            if (animator == null)
            {
                Debug.LogError($"{nameof(EditorGetAllStatesName)}, animator == null");
                return names;
            }

            // Get AnimatorController
            AnimatorController controller = animator.runtimeAnimatorController as AnimatorController;
            if (controller == null)
            {
                Debug.LogError($"{nameof(EditorGetAllStatesName)}, controller == null");
                return names;
            }

            foreach (AnimatorControllerLayer layer in controller.layers)
            {
                foreach (ChildAnimatorState state in layer.stateMachine.states)
                {
                    names.Add(state.state.name);
                }
            }
            return names;
        }
        public static HashSet<AnimatorState> EditorGetAllStates(this Animator animator)
        {

            HashSet<AnimatorState> states = new();
            if (animator == null)
            {
                Debug.LogError($"{nameof(EditorGetAllStates)}, animator == null");
                return states;
            }

            // Get AnimatorController
            AnimatorController controller = animator.runtimeAnimatorController as AnimatorController;
            if (controller == null)
            {
                Debug.LogError($"{nameof(EditorGetAllStates)}, controller == null");
                return states;
            }

            foreach (AnimatorControllerLayer layer in controller.layers)
            {
                ExtractStates(layer.stateMachine, states);
            }
            return states;
        }
        public static void ExtractStates(AnimatorStateMachine stateMachine, HashSet<AnimatorState> states)
        {
            foreach (ChildAnimatorState state in stateMachine.states)
            {
                states.Add(state.state);
            }

            foreach (var subStateMachine in stateMachine.stateMachines)
            {
                ExtractStates(subStateMachine.stateMachine, states);
            }
        }
        public static string EditorGetStateName(this Animator animator, AnimatorStateInfo curState)
        {
            if (!s_StatesNameHash.ContainsKey(animator))
            {
                Dictionary<int, string> dic = new Dictionary<int, string>();
                AnimatorController controller = animator.runtimeAnimatorController as AnimatorController;
                var states = animator.EditorGetAllStates();
                foreach (var state in states)
                {
                    string stateName = state.name;
                    int hash = Animator.StringToHash(stateName);
                    dic[hash] = stateName;
                    //Debug.LogError($"stateName:{stateName},hash:{hash}");
                }
                s_StatesNameHash[animator] = dic;
            }

            {
                var hashDic = s_StatesNameHash[animator];
                int hash = curState.shortNameHash;
                if (hashDic.TryGetValue(hash, out var stateName))
                {
                    return stateName;
                }
                return hash.ToString();
            }
        }
    }
}

