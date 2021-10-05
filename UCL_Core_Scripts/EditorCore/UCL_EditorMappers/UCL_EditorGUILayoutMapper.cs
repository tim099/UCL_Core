using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UCL.Core.EditorLib
{
    public static class EditorGUILayoutMapper
    {
        #region ObjectField
        private static System.Func<UnityEngine.Object, Type, bool, GUILayoutOption[], UnityEngine.Object> m_ObjectField = null;

        /// <summary>
        /// Make a field to receive any object type.
        /// </summary>
        /// <param name="obj">The object the field shows.</param>
        /// <param name="objType">The type of the objects that can be assigned.</param>
        /// <param name="allowSceneObjects">Allow assigning Scene objects. See Description for more info.</param>
        /// <param name="options">An optional list of layout options that specify extra layout properties. Any values passed in here will override settings defined by the style</param>
        /// <returns>The object that has been set by the user.</returns>
        public static UnityEngine.Object ObjectField(UnityEngine.Object obj, Type objType, bool allowSceneObjects, params GUILayoutOption[] options)
        {
            if (m_ObjectField == null) return obj;
            return m_ObjectField.Invoke(obj, objType, allowSceneObjects, options);
        }
        public static void InitObjectField(System.Func<UnityEngine.Object, Type, bool, GUILayoutOption[], UnityEngine.Object> iFunc)
        {
            m_ObjectField = iFunc;
        }
        #endregion
    }
}