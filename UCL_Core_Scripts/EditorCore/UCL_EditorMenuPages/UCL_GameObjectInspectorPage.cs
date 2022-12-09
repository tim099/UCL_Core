using System.Collections;
using System.Collections.Generic;
using UnityEngine;


namespace UCL.Core.EditorLib.Page
{
    public class UCL_GameObjectInspectorPage : UCL_EditorPage
    {
        public static UCL_GameObjectInspectorPage Create(GameObject iTarget)
        {
            var aPage = UCL_EditorPage.Create<UCL_GameObjectInspectorPage>();
            aPage.Init(iTarget);
            return aPage;
        }
        public GameObject m_Target = null;
        public Transform m_Root = null;
        UCL.Core.UCL_ObjectDictionary m_Dic = new UCL.Core.UCL_ObjectDictionary();
        public override bool IsWindow => true;

        public void Init(GameObject iTarget)
        {
            m_Target = iTarget;
            m_Root = m_Target.transform;
        }

        protected override void TopBarButtons()
        {
            base.TopBarButtons();
        }

        protected override void ContentOnGUI()
        {
            using (new GUILayout.HorizontalScope("box"))
            {
                using (new GUILayout.VerticalScope("box", GUILayout.MinWidth(200)))
                {
                    var aActiveScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                    if (aActiveScene != null)
                    {

                        if(m_Root != null)
                        {
                            DrawHierarchy(m_Root, m_Dic.GetSubDic("Hierarchy"), true);
                        }
                        else
                        {
                            GUILayout.Label("Scene:" + aActiveScene.name);
                            var aObjs = aActiveScene.GetRootGameObjects();
                            int aAt = 0;
                            foreach (var aObj in aObjs)
                            {
                                DrawHierarchy(aObj.transform, m_Dic.GetSubDic("Hierarchy", aAt++), true);
                            }
                        }

                        //GUILayout.Label(m_Target.name);
                    }
                }
                if (m_Target != null)
                {
                    using (new GUILayout.VerticalScope("box"))
                    {
                        using (new GUILayout.VerticalScope("box"))
                        {
                            using (new GUILayout.HorizontalScope())
                            {
                                m_Target.SetActive(UCL.Core.UI.UCL_GUILayout.BoolField(m_Target.activeSelf, GUILayout.Width(21), GUILayout.Height(21), GUILayout.ExpandWidth(false)));
                                m_Target.name = GUILayout.TextField(m_Target.name);
                            }
                            using (new GUILayout.HorizontalScope())
                            {
                                GUILayout.Label("Tag", GUILayout.ExpandWidth(false));
                                m_Target.tag = GUILayout.TextField(m_Target.tag);
                                //var aTags = UnityEditorInternal.InternalEditorUtility.tags;
                                //m_Target.tag = aTags[UCL.Core.UI.UCL_GUILayout.PopupAuto(aTags.GetArrayIndex(m_Target.tag), aTags, m_Dic.GetSubDic("Tag"), "Tag", 8)];
                            }
                        }


                        var aComponents = m_Target.GetComponents<Component>();
                        int aAt = 0;
                        foreach (var aComponent in aComponents)
                        {
                            DrawComponent(aComponent, m_Dic.GetSubDic("Components", aAt++));
                        }
                    }
                }
            }
        }
        protected virtual void DrawHierarchy(Transform iTransform, UCL.Core.UCL_ObjectDictionary iDataDic,bool iParentActive)
        {
            bool aIsActive = (iTransform.gameObject.activeSelf && iParentActive);
            var aNameStyle = UI.UCL_GUIStyle.GetButtonStyle(aIsActive ? Color.white: Color.gray);
            if(m_Target == iTransform.gameObject)
            {
                aNameStyle = UI.UCL_GUIStyle.GetButtonStyle(aIsActive ? Color.yellow : new Color(0.7f, 0.7f, 0f));
            }
            if (iTransform.childCount > 0)
            {
                GUILayout.BeginHorizontal();
                bool aIsShowChild = UCL.Core.UI.UCL_GUILayout.Toggle(iDataDic, "ShowChild");

                GUILayout.BeginVertical();
                if (GUILayout.Button(iTransform.name, aNameStyle))
                {
                    m_Target = iTransform.gameObject;
                }
                if (aIsShowChild)
                {

                    int aAt = 0;
                    foreach (Transform aChild in iTransform)
                    {
                        DrawHierarchy(aChild, iDataDic.GetSubDic("Child", aAt++), aIsActive);
                    }
                }
                GUILayout.EndVertical();

                GUILayout.EndHorizontal();
            }
            else
            {
                GUILayout.BeginHorizontal();
                GUILayout.Button("⊠", GUILayout.Width(21), GUILayout.Height(21));
                if (GUILayout.Button(iTransform.name, aNameStyle))
                {
                    m_Target = iTransform.gameObject;
                }
                GUILayout.EndHorizontal();
            }
            

        }
        protected virtual void DrawComponent(Component iComponent, UCL.Core.UCL_ObjectDictionary iDataDic)
        {
            UCL.Core.UI.UCL_GUILayout.DrawObjectData(iComponent, iDataDic, iComponent.GetType().Name,
                false, UCL.Core.UCL_StaticFunctions.FieldNameUnityVer);
        }
    }
}