using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UCL.Core.PA
{
    [CustomPropertyDrawer(typeof(ConditionalAttribute))]
    public class UCL_ConditionalAttributeDrawer : PropertyDrawer
    {
        private ConditionalAttribute Conditional => _conditional ?? (_conditional = attribute as ConditionalAttribute);
        private ConditionalAttribute _conditional;

        private bool m_CustomDrawersCached;
        private static IEnumerable<Type> s_AllPropertyDrawerAttributeTypes;
        private bool IsMultipleAttributes;
        private bool m_SpecialType;
        private PropertyAttribute aGenericAttr;
        private PropertyDrawer IsGenericAttributeDrawerInstance;
        private Type m_GenericAttributeDrawerType;
        private Type m_GenericType;
        private PropertyDrawer m_GenericTypeDrawerInstance;
        private Type m_GenericTypeDrawerType;

        private bool m_Show = true;


        private void Initialize(SerializedProperty property)
        {
            if (m_CustomDrawersCached) return;
            if (s_AllPropertyDrawerAttributeTypes == null)
            {
                s_AllPropertyDrawerAttributeTypes = AppDomain.CurrentDomain.GetAssemblies().SelectMany(x => x.GetTypes())
                    .Where(x => typeof(PropertyDrawer).IsAssignableFrom(x) && !x.IsInterface && !x.IsAbstract);
            }

            if (HaveMultipleAttributes())
            {
                IsMultipleAttributes = true;
                GetPropertyDrawerType(property);
            }
            else if (fieldInfo != null && !fieldInfo.FieldType.Module.ScopeName.Equals(typeof(int).Module.ScopeName))
            {
                m_SpecialType = true;
                GetTypeDrawerType(property);
            }

            m_CustomDrawersCached = true;
        }

        private bool HaveMultipleAttributes()
        {
            if (fieldInfo == null) return false;
            var genericAttributeType = typeof(PropertyAttribute);
            var attributes = fieldInfo.GetCustomAttributes(genericAttributeType, false);
            if (attributes.IsNullOrEmpty()) return false;
            return attributes.Length > 1;
        }
        public override float GetPropertyHeight(SerializedProperty iProperty, GUIContent label)
        {
            if (!m_Show) return 0;
            return base.GetPropertyHeight(iProperty, label);
        }
        public override void OnGUI(Rect position, SerializedProperty iProperty, GUIContent label)
        {
            Initialize(iProperty);

            var aParent = iProperty.GetParent();
            //Debug.LogError("aParent:" + aParent.GetType().Name);
            m_Show = Conditional.IsShow(aParent);
            if (!m_Show) return;

            //bool aIsVisible = PropertyIsVisible(iProperty, Conditional.m_CompareValues);
            //m_Show = Conditional.m_Inverse ^ aIsVisible;


            if (IsMultipleAttributes && IsGenericAttributeDrawerInstance != null)
            {
                try
                {
                    IsGenericAttributeDrawerInstance.OnGUI(position, iProperty, label);
                }
                catch (Exception e)
                {
                    EditorGUI.PropertyField(position, iProperty, label);
                    LogWarning("Unable to instantiate " + aGenericAttr.GetType() + " : " + e, iProperty);
                }
            }
            else if (m_SpecialType && m_GenericTypeDrawerInstance != null)
            {
                try
                {
                    m_GenericTypeDrawerInstance.OnGUI(position, iProperty, label);
                }
                catch (Exception e)
                {
                    EditorGUI.PropertyField(position, iProperty, label);
                    LogWarning("Unable to instantiate " + m_GenericType + " : " + e, iProperty);
                    Debug.LogException(e);
                }
            }
            else
            {
                EditorGUI.PropertyField(position, iProperty, label, true);
            }
        }

        private void LogWarning(string log, SerializedProperty property)
        {
            var warning = "Property <color=brown>" + fieldInfo.Name + "</color>";
            if (fieldInfo != null && fieldInfo.DeclaringType != null)
                warning += " on behaviour <color=brown>" + fieldInfo.DeclaringType.Name + "</color>";
            warning += " caused: " + log;

            Debug.LogWarning(warning, property.serializedObject.targetObject);
        }


        #region Get Custom Property/Type drawers

        private void GetPropertyDrawerType(SerializedProperty property)
        {
            if (IsGenericAttributeDrawerInstance != null) return;

            //Get the second attribute flag
            try
            {
                aGenericAttr = (PropertyAttribute)fieldInfo.GetCustomAttributes(typeof(PropertyAttribute), false)
                    .FirstOrDefault(a => !(a is ConditionalAttribute));

                if (aGenericAttr is ContextMenuItemAttribute)
                {
                    LogWarning("[ConditionalField] does not work with " + aGenericAttr.GetType(), property);
                    return;
                }

                if (aGenericAttr is TooltipAttribute) return;
            }
            catch (Exception e)
            {
                LogWarning("Can't find stacked propertyAttribute after ConditionalProperty: " + e, property);
                Debug.LogException(e);
                return;
            }

            //Get the associated attribute drawer
            try
            {

                m_GenericAttributeDrawerType = s_AllPropertyDrawerAttributeTypes.First(iType =>
                        {
                            var aCustomAttrs = CustomAttributeData.GetCustomAttributes(iType);
                            if(aCustomAttrs.IsNullOrEmpty())
                            {
                                //Debug.LogError("iType:"+iType.FullName+ ",aCustomAttrs.Count == 0");
                                return false;
                            }
                            var aConstructorArguments = aCustomAttrs.First().ConstructorArguments.First();
                            return (Type)aConstructorArguments.Value == aGenericAttr.GetType();
                        });
            }
            catch (Exception e)
            {
                LogWarning("Can't find property drawer from CustomPropertyAttribute of " + aGenericAttr.GetType() + " : " + e, property);
                Debug.LogException(e);
                return;
            }

            //Create instances of each (including the arguments)
            try
            {
                IsGenericAttributeDrawerInstance = (PropertyDrawer)Activator.CreateInstance(m_GenericAttributeDrawerType);
                //Get arguments
                IList<CustomAttributeTypedArgument> attributeParams = fieldInfo.GetCustomAttributesData()
                .First(a => a.AttributeType == aGenericAttr.GetType()).ConstructorArguments;
                IList<CustomAttributeTypedArgument> unpackedParams = new List<CustomAttributeTypedArgument>();
                //Unpack any params object[] args
                foreach (CustomAttributeTypedArgument singleParam in attributeParams)
                {
                    if (singleParam.Value.GetType() == typeof(System.Collections.ObjectModel.ReadOnlyCollection<CustomAttributeTypedArgument>))
                    {
                        foreach (CustomAttributeTypedArgument unpackedSingleParam in
                            (System.Collections.ObjectModel.ReadOnlyCollection<CustomAttributeTypedArgument>)singleParam.Value)
                        {
                            unpackedParams.Add(unpackedSingleParam);
                        }
                    }
                    else
                    {
                        unpackedParams.Add(singleParam);
                    }
                }

                object[] attributeParamsObj = unpackedParams.Select(x => x.Value).ToArray();

                if (attributeParamsObj.Any())
                {
                    aGenericAttr = (PropertyAttribute)Activator.CreateInstance(aGenericAttr.GetType(), attributeParamsObj);
                }
                else
                {
                    aGenericAttr = (PropertyAttribute)Activator.CreateInstance(aGenericAttr.GetType());
                }
            }
            catch (Exception e)
            {
                LogWarning("No constructor available in " + aGenericAttr.GetType() + " : " + e, property);
                Debug.LogException(e);
                return;
            }

            //Reassign the attribute field in the drawer so it can access the argument values
            try
            {
                var genericDrawerAttributeField = m_GenericAttributeDrawerType.GetField("m_Attribute", BindingFlags.Instance | BindingFlags.NonPublic);
                genericDrawerAttributeField.SetValue(IsGenericAttributeDrawerInstance, aGenericAttr);
            }
            catch (Exception e)
            {
                LogWarning("Unable to assign attribute to " + IsGenericAttributeDrawerInstance.GetType() + " : " + e, property);
                Debug.LogException(e);
            }
        }

        private void GetTypeDrawerType(SerializedProperty property)
        {
            if (m_GenericTypeDrawerInstance != null) return;

            //Get the associated attribute drawer
            try
            {
                // Of all property drawers in the assembly we need to find one that affects target type
                // or one of the base types of target type
                foreach (Type propertyDrawerType in s_AllPropertyDrawerAttributeTypes)
                {
                    m_GenericType = fieldInfo.FieldType;
                    var affectedType = (Type)CustomAttributeData.GetCustomAttributes(propertyDrawerType).First().ConstructorArguments.First().Value;
                    while (m_GenericType != null)
                    {
                        if (m_GenericTypeDrawerType != null) break;
                        if (affectedType == m_GenericType) m_GenericTypeDrawerType = propertyDrawerType;
                        else m_GenericType = m_GenericType.BaseType;
                    }
                    if (m_GenericTypeDrawerType != null) break;
                }
            }
            catch (Exception)
            {
                // Commented out because of multiple false warnings on Behaviour types
                //LogWarning("[ConditionalField] does not work with "+_genericType+". Unable to find property drawer from the Type", property);
                return;
            }
            if (m_GenericTypeDrawerType == null) return;

            //Create instances of each (including the arguments)
            try
            {
                m_GenericTypeDrawerInstance = (PropertyDrawer)Activator.CreateInstance(m_GenericTypeDrawerType);
            }
            catch (Exception e)
            {
                LogWarning("no constructor available in " + m_GenericType + " : " + e, property);
                Debug.LogException(e);
                return;
            }

            //Reassign the attribute field in the drawer so it can access the argument values
            try
            {
                m_GenericTypeDrawerType.GetField("m_Attribute", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(m_GenericTypeDrawerInstance, fieldInfo);
            }
            catch (Exception)
            {
                //LogWarning("Unable to assign attribute to " + _genericTypeDrawerInstance.GetType() + " : " + e, property);
            }
        }

        #endregion

        #region Property Is Visible

        public bool PropertyIsVisible(SerializedProperty iProperty, object[] iCompareAgainst)
        {
            if (iProperty == null) return true;
            var aParent = iProperty.GetParent();
            var aAttr = attribute as ConditionalAttribute;
            var aMember = aParent.GetMember(aAttr.m_FieldName);
            if (aMember == null)
            {
                Debug.LogError("member == null attr.m_FieldName:" + aAttr.m_FieldName + " ,parent:" + aParent.ToString());
                return false;
            }
            //return aAttr.IsShow(aParent);
            if (!iCompareAgainst.IsNullOrEmpty())
            {
                return CompareAgainstValues(aMember, iCompareAgainst);
            }

            return true;
        }

        /// <summary>
        /// True if the property value matches any of the values in '_compareValues'
        /// </summary>
        private bool CompareAgainstValues(object propertyValue, object[] compareAgainst)
        {
            if (propertyValue == null)
            {
                Debug.LogError("propertyValue == null");
                return false;
            }
            for (var i = 0; i < compareAgainst.Length; i++)
            {
                if (compareAgainst[i] != null)
                {
                    if (compareAgainst[i].Equals(propertyValue))
                    {
                        return true;
                    }
                }
            }
            return false;
        }
        #endregion
    }
}