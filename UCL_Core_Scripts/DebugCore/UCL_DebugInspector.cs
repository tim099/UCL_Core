using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace UCL.Core {
    public class UCL_DebugInspector : MonoBehaviour {
        public static UCL_DebugInspector ins = null;
        protected Rect m_WindowRect = Rect.zero;
        protected Vector2 m_ScrollPos = Vector2.zero;
        protected Vector2 m_RootScrollPos = Vector2.zero;
        protected Vector2 m_InspectorScrollPos = Vector2.zero;
        protected HashSet<Transform> m_ShowTrans = new HashSet<Transform>();
        protected HashSet<Component> m_ShowComponents = new HashSet<Component>();
        protected Transform m_CurRoot = null;
        protected Transform m_Selected = null;
        public static void Show() {
            if(ins == null) {
                ins = Core.GameObjectLib.Create<UCL_DebugInspector>("UCL_DebugInspector", null);
            }
            ins.ShowInspector();
        }
        public static void Hide() {
            if(ins == null) return;
            ins.HideInspector();
        }
        virtual public void ShowInspector() {
            gameObject.SetActive(true);
        }
        virtual public void HideInspector() {
            gameObject.SetActive(false);
        }
        virtual public void Init() {

        }
        private void OnGUI() {
            const int edge = 5;//5 pixel
            m_WindowRect = new Rect(edge, edge, Screen.width - 2 * edge, Screen.height - 2 * edge);
            m_WindowRect = GUILayout.Window(132125, m_WindowRect, ConsoleWindow, "UCL DebugInspector");
        }
        virtual protected void ConsoleWindow(int id) {
            m_ScrollPos = GUILayout.BeginScrollView(m_ScrollPos);
            GUILayout.BeginVertical();
            using(var scope = new GUILayout.HorizontalScope("box")) {
                if(UCL.Core.UI.UCL_GUILayout.DrawButtonAutoSize("Close",22)) {
                    HideInspector();
                }
                //if(GUILayout.Button("Save All To Json")) {}
            }
            GUILayout.BeginHorizontal();
            using(var scope = new GUILayout.HorizontalScope("box",GUILayout.Width(m_WindowRect.width/2f))) {
                DrawInspector();
            }
            using(var scope = new GUILayout.HorizontalScope("box")) {
                DrawRoot();
            }

            GUILayout.EndHorizontal();

            GUILayout.EndVertical();
            GUILayout.EndScrollView();
        }
        public void SetSelectedObject(GameObject obj) {
            if(obj == null) {
                m_Selected = null;
            } else {
                m_Selected = obj.transform;
            }
            m_ShowComponents.Clear();
        }
        virtual protected void DrawRoot() {
            m_RootScrollPos = GUILayout.BeginScrollView(m_RootScrollPos);
            if(m_CurRoot == null) {
                var active_scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                GUILayout.Label(active_scene.name);
                var objs = active_scene.GetRootGameObjects();
                foreach(var obj in objs) {
                    DrawNode(obj.transform);
                }
            } else {
                if(UCL.Core.UI.UCL_GUILayout.DrawButtonAutoSize("Back", 22)) {
                    m_CurRoot = m_CurRoot.parent;
                }
                DrawNode(m_CurRoot);
            }
            GUILayout.EndScrollView();
        }
        virtual protected void DrawNode(Transform node) {
            if(node == null) return;
            GUILayout.BeginHorizontal();
            if(node.childCount > 0) {
                if(m_ShowTrans.Contains(node)) {
                    if(UCL.Core.UI.UCL_GUILayout.DrawButtonAutoSize("v", 22)) {
                        m_ShowTrans.Remove(node);
                    }
                } else {
                    if(UCL.Core.UI.UCL_GUILayout.DrawButtonAutoSize(">", 22)) {
                        m_ShowTrans.Add(node);
                    }
                }
                
            } else {
                UCL.Core.UI.UCL_GUILayout.DrawButtonAutoSize("-", 22);
            }
            GUILayout.BeginVertical();
            if(m_Selected == node) {
                if(UCL.Core.UI.UCL_GUILayout.DrawButtonAutoSize(node.name, 22, Color.green)) {
                    m_CurRoot = m_Selected;
                }
            } else {
                if(UCL.Core.UI.UCL_GUILayout.DrawButtonAutoSize(node.name, 22)) {
                    SetSelectedObject(node.gameObject);
                }
            }

            if(m_ShowTrans.Contains(node)) {
                foreach(Transform trans in node) {
                    DrawNode(trans);
                }
            }
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
        }
        virtual protected void DrawInspector() {
            GUILayout.BeginVertical();
            m_InspectorScrollPos = GUILayout.BeginScrollView(m_InspectorScrollPos);
            GUILayout.Label("Inspector");
            if(m_Selected != null) {
                DrawSelectedInfo(m_Selected.gameObject);
            }
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
        }
        virtual protected void DrawSelectedInfo(GameObject obj) {
            GUILayout.BeginVertical();
            GUILayout.BeginHorizontal();
            if(GUILayout.Button("Deselect", GUILayout.Width(80))) {
                SetSelectedObject(null);
            }
            obj.name = GUILayout.TextField(obj.name, 128);
            GUILayout.EndHorizontal();

            var coms = obj.GetComponents<Component>();
            foreach(var com in coms) {
                using(var scope = new GUILayout.HorizontalScope("box")) {
                    DrawComponent(com);
                }
            }
            GUILayout.EndVertical();
        }
        virtual protected void DrawComponent(Component component) {
            GUILayout.BeginHorizontal();
            var type = component.GetType();
            bool draw_detail = false;
            if(m_ShowComponents.Contains(component)) {
                draw_detail = true;
                if(GUILayout.Button("v", GUILayout.Width(21))) {
                    m_ShowComponents.Remove(component);
                }
            } else {
                if(GUILayout.Button(">", GUILayout.Width(21))) {
                    m_ShowComponents.Add(component);
                }
            }
            GUILayout.BeginVertical();
            GUILayout.Box(type.Name);
            if(draw_detail) {
                DrawComponentDetail(component);
            }
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();

        }
        public void DrawComponentDetail(Component obj) {
            var type = obj.GetType();
            FieldInfo[] fieldInfos = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach(var info in fieldInfos) {
                DrawFieldInfo(info, obj);
            }
        }
        public void DrawObject(FieldInfo info, object obj, object parent_obj) {
            var type = obj.GetType();
            string title = "(" + type.Name + ")" + info.Name;
            if(type.IsGenericType && type != typeof(string)) {
                /*
                var GenericType = info_type.GetGenericTypeDefinition();
                var TypeInfo = info_type.GetTypeInfo();
                var GenericTypeArguments = TypeInfo.GenericTypeArguments;
                var ContentType = GenericTypeArguments[0];

                if(value is IEnumerable) {
                    var container = (IEnumerable)value;
                    if(GenericType == typeof(Dictionary<,>)) {
                        ContentType = GenericTypeArguments[1];//[0] is Key type [1] is Value type!!
                    }
                    if(value is IList) {
                        IList list = value as IList;
                        for(int i = 0; i < list.Count; i++) {
                            int at = i;
                            DrawType(list[at], debug_data, delegate (object val) {
                                list[at] = val;
                            });
                        }
                    } else if(value is IDictionary) {
                        IDictionary dic = value as IDictionary;
                        foreach(var key in dic.Keys) {
                            var cur_key = key;
                            DrawType(dic[cur_key], debug_data, delegate (object val) {
                                dic[cur_key] = val;
                            });
                        }
                    } else if(GenericType == typeof(Stack<>)) {
                        var stack = value as IEnumerable;
                        foreach(var t_val in container) {
                            if(m_SelectedObj == t_val) {
                                DrawType(t_val, debug_data, delegate (object val) {
                                    m_SelectedObj = null;
                                });
                                DrawTypeRecursive(t_val, debug_data);
                            } else {
                                DrawType(t_val, debug_data, delegate (object val) {
                                    m_SelectedObj = t_val;
                                    dic[cur_key] = val;
                                });
                            }


                        }
                    } else {//Can't assign value....
                        IEnumerator enumerator = container.GetEnumerator();
                        while(enumerator.MoveNext()) {
                            var type_val = enumerator.Current;
                            DrawType(type_val, debug_data, delegate (object val) {
                            });
                        }
                    }

                } else {
                    var debug_data2 = DebugCommon.DebugOnGUI.Instance.CreateData("Unknown GenericType_" + nameof(value))
                        .SetSize(m_ButtonSize).SetButAct(delegate () { });
                    var debug_data3 = DebugCommon.DebugOnGUI.Instance.CreateData(value.ToString()).SetSize(m_ButtonSize).SetButAct(delegate () { });
                    debug_data.AddSubData(debug_data2);
                    debug_data2.AddSubData(debug_data3);
                }
                */
            }
            else if(type == typeof(int)) {
                info.SetValue(parent_obj, UCL.Core.UI.UCL_GUILayout.IntField(title, (int)obj));
                //UnityEditor.EditorGUILayout.IntField(field_name, (int) obj);
            } 
            else {//Unknown Type!!
                GUILayout.Label(title + " : " + obj.ToString());
            }

        }
        public void DrawFieldInfo(FieldInfo info, object parent_obj) {
            var value = info.GetValue(parent_obj);

            System.Type info_type = info.FieldType;
            if(value != null) {
                DrawObject(info, value, parent_obj);
                //GUILayout.Label("(" + info.FieldType.Name + ")" + info.Name + " : " + value.ToString());
            } else {
                GUILayout.Label("(" + info.FieldType.Name + ")" + info.Name + " : null");
            }
        }
    }
}