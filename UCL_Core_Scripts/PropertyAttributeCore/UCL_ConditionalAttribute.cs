using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using System.Reflection;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.EditorTools;
#endif

namespace UCL.Core.PA {
    /// <summary>
    /// reference https://github.com/Deadcows/MyBox/blob/master/Attributes/ConditionalAttribute.cs
    /// this is an alter version of ConditionalAttribute
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public class ConditionalAttribute : PropertyAttribute {
        public readonly string m_FieldName;
        public readonly object[] m_CompareValues;
        public readonly bool m_Inverse;

        /// <param name="field_name">String name of field to check value</param>
        /// <param name="inverse">Inverse check result</param>
        /// <param name="compareValues">On which values field will be shown in inspector</param>
        public ConditionalAttribute(string field_name, bool inverse = false, params object[] compareValues) {
            m_FieldName = field_name;
            m_Inverse = inverse;
            m_CompareValues = compareValues;
        }
    }

#if UNITY_EDITOR
    [CustomPropertyDrawer(typeof(ConditionalAttribute))]
    public class ConditionalAttributeDrawer : PropertyDrawer {
        private ConditionalAttribute Conditional => _conditional ?? (_conditional = attribute as ConditionalAttribute);
        private ConditionalAttribute _conditional;

        private bool _customDrawersCached;
        private static IEnumerable<Type> _allPropertyDrawerAttributeTypes;
        private bool _multipleAttributes;
        private bool _specialType;
        private PropertyAttribute _genericAttribute;
        private PropertyDrawer _genericAttributeDrawerInstance;
        private Type _genericAttributeDrawerType;
        private Type _genericType;
        private PropertyDrawer _genericTypeDrawerInstance;
        private Type _genericTypeDrawerType;

        private bool m_Show = true;


        private void Initialize(SerializedProperty property) {
            if(_customDrawersCached) return;
            if(_allPropertyDrawerAttributeTypes == null) {
                _allPropertyDrawerAttributeTypes = AppDomain.CurrentDomain.GetAssemblies().SelectMany(x => x.GetTypes())
                    .Where(x => typeof(PropertyDrawer).IsAssignableFrom(x) && !x.IsInterface && !x.IsAbstract);
            }

            if(HaveMultipleAttributes()) {
                _multipleAttributes = true;
                GetPropertyDrawerType(property);
            } else if(fieldInfo != null && !fieldInfo.FieldType.Module.ScopeName.Equals(typeof(int).Module.ScopeName)) {
                _specialType = true;
                GetTypeDrawerType(property);
            }

            _customDrawersCached = true;
        }

        private bool HaveMultipleAttributes() {
            if(fieldInfo == null) return false;
            var genericAttributeType = typeof(PropertyAttribute);
            var attributes = fieldInfo.GetCustomAttributes(genericAttributeType, false);
            if(attributes.IsNullOrEmpty()) return false;
            return attributes.Length > 1;
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label) {
            if(!m_Show) return 0;
            return base.GetPropertyHeight(property, label);
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label) {
            Initialize(property);
            bool visible = PropertyIsVisible(property, Conditional.m_CompareValues);
            m_Show = Conditional.m_Inverse ^ visible;
            if(!m_Show) return;

            if(_multipleAttributes && _genericAttributeDrawerInstance != null) {
                try {
                    _genericAttributeDrawerInstance.OnGUI(position, property, label);
                } catch(Exception e) {
                    EditorGUI.PropertyField(position, property, label);
                    LogWarning("Unable to instantiate " + _genericAttribute.GetType() + " : " + e, property);
                }
            } else if(_specialType && _genericTypeDrawerInstance != null) {
                try {
                    _genericTypeDrawerInstance.OnGUI(position, property, label);
                } catch(Exception e) {
                    EditorGUI.PropertyField(position, property, label);
                    LogWarning("Unable to instantiate " + _genericType + " : " + e, property);
                }
            } else {
                EditorGUI.PropertyField(position, property, label, true);
            }
        }

        private void LogWarning(string log, SerializedProperty property) {
            var warning = "Property <color=brown>" + fieldInfo.Name + "</color>";
            if(fieldInfo != null && fieldInfo.DeclaringType != null)
                warning += " on behaviour <color=brown>" + fieldInfo.DeclaringType.Name + "</color>";
            warning += " caused: " + log;

            Debug.LogWarning(warning, property.serializedObject.targetObject);
        }


        #region Get Custom Property/Type drawers

        private void GetPropertyDrawerType(SerializedProperty property) {
            if(_genericAttributeDrawerInstance != null) return;

            //Get the second attribute flag
            try {
                _genericAttribute = (PropertyAttribute)fieldInfo.GetCustomAttributes(typeof(PropertyAttribute), false)
                    .FirstOrDefault(a => !(a is ConditionalAttribute));

                //TODO: wtf man
                if(_genericAttribute is ContextMenuItemAttribute) {
                    LogWarning("[ConditionalField] does not work with " + _genericAttribute.GetType(), property);
                    return;
                }

                if(_genericAttribute is TooltipAttribute) return;
            } catch(Exception e) {
                LogWarning("Can't find stacked propertyAttribute after ConditionalProperty: " + e, property);
                return;
            }

            //Get the associated attribute drawer
            try {
                _genericAttributeDrawerType = _allPropertyDrawerAttributeTypes.First(x =>
                    (Type)CustomAttributeData.GetCustomAttributes(x).First().ConstructorArguments.First().Value == _genericAttribute.GetType());
            } catch(Exception e) {
                LogWarning("Can't find property drawer from CustomPropertyAttribute of " + _genericAttribute.GetType() + " : " + e, property);
                return;
            }

            //Create instances of each (including the arguments)
            try {
                _genericAttributeDrawerInstance = (PropertyDrawer)Activator.CreateInstance(_genericAttributeDrawerType);
                //Get arguments
                IList<CustomAttributeTypedArgument> attributeParams = fieldInfo.GetCustomAttributesData()
                .First(a => a.AttributeType == _genericAttribute.GetType()).ConstructorArguments;
                IList<CustomAttributeTypedArgument> unpackedParams = new List<CustomAttributeTypedArgument>();
                //Unpack any params object[] args
                foreach(CustomAttributeTypedArgument singleParam in attributeParams) {
                    if(singleParam.Value.GetType() == typeof(System.Collections.ObjectModel.ReadOnlyCollection<CustomAttributeTypedArgument>)) {
                        foreach(CustomAttributeTypedArgument unpackedSingleParam in
                            (System.Collections.ObjectModel.ReadOnlyCollection<CustomAttributeTypedArgument>)singleParam.Value) {
                            unpackedParams.Add(unpackedSingleParam);
                        }
                    } else {
                        unpackedParams.Add(singleParam);
                    }
                }

                object[] attributeParamsObj = unpackedParams.Select(x => x.Value).ToArray();

                if(attributeParamsObj.Any()) {
                    _genericAttribute = (PropertyAttribute)Activator.CreateInstance(_genericAttribute.GetType(), attributeParamsObj);
                } else {
                    _genericAttribute = (PropertyAttribute)Activator.CreateInstance(_genericAttribute.GetType());
                }
            } catch(Exception e) {
                LogWarning("No constructor available in " + _genericAttribute.GetType() + " : " + e, property);
                return;
            }

            //Reassign the attribute field in the drawer so it can access the argument values
            try {
                var genericDrawerAttributeField = _genericAttributeDrawerType.GetField("m_Attribute", BindingFlags.Instance | BindingFlags.NonPublic);
                genericDrawerAttributeField.SetValue(_genericAttributeDrawerInstance, _genericAttribute);
            } catch(Exception e) {
                LogWarning("Unable to assign attribute to " + _genericAttributeDrawerInstance.GetType() + " : " + e, property);
            }
        }

        private void GetTypeDrawerType(SerializedProperty property) {
            if(_genericTypeDrawerInstance != null) return;

            //Get the associated attribute drawer
            try {
                // Of all property drawers in the assembly we need to find one that affects target type
                // or one of the base types of target type
                foreach(Type propertyDrawerType in _allPropertyDrawerAttributeTypes) {
                    _genericType = fieldInfo.FieldType;
                    var affectedType = (Type)CustomAttributeData.GetCustomAttributes(propertyDrawerType).First().ConstructorArguments.First().Value;
                    while(_genericType != null) {
                        if(_genericTypeDrawerType != null) break;
                        if(affectedType == _genericType) _genericTypeDrawerType = propertyDrawerType;
                        else _genericType = _genericType.BaseType;
                    }
                    if(_genericTypeDrawerType != null) break;
                }
            } catch(Exception) {
                // Commented out because of multiple false warnings on Behaviour types
                //LogWarning("[ConditionalField] does not work with "+_genericType+". Unable to find property drawer from the Type", property);
                return;
            }
            if(_genericTypeDrawerType == null) return;

            //Create instances of each (including the arguments)
            try {
                _genericTypeDrawerInstance = (PropertyDrawer)Activator.CreateInstance(_genericTypeDrawerType);
            } catch(Exception e) {
                LogWarning("no constructor available in " + _genericType + " : " + e, property);
                return;
            }

            //Reassign the attribute field in the drawer so it can access the argument values
            try {
                _genericTypeDrawerType.GetField("m_Attribute", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(_genericTypeDrawerInstance, fieldInfo);
            } catch(Exception) {
                //LogWarning("Unable to assign attribute to " + _genericTypeDrawerInstance.GetType() + " : " + e, property);
            }
        }

        #endregion

        #region Property Is Visible

        public bool PropertyIsVisible(SerializedProperty property, object[] compareAgainst) {
            if(property == null) return true;
            var parent = property.GetParent();
            var attr = attribute as ConditionalAttribute;
            var member = parent.GetMember(attr.m_FieldName);
            if(member == null) {
                Debug.LogError("member == null attr.m_FieldName:" + attr.m_FieldName + " ,parent:" + parent.ToString());
                return false;
            }
            if(!compareAgainst.IsNullOrEmpty()) {
                return CompareAgainstValues(member, compareAgainst);
            }

            return true;
        }

        /// <summary>
        /// True if the property value matches any of the values in '_compareValues'
        /// </summary>
        private bool CompareAgainstValues(object propertyValue, object[] compareAgainst) {
            if(propertyValue == null) {
                Debug.LogError("propertyValue == null");
                return false;
            }
            for(var i = 0; i < compareAgainst.Length; i++) {
                if(compareAgainst[i] != null) {
                    if(compareAgainst[i].Equals(propertyValue)) {
                        return true;
                    }
                }
            }
            return false;
        }
        #endregion
    }
#endif
}