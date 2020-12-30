using System;
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

        protected object m_EditData = null;
        protected Tuple<object, FieldInfo> m_EditTarget = null;

        #region Buffer
        public string m_ObjectNameBuffer;
        #endregion
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
                if(UCL.Core.UI.UCL_GUILayout.ButtonAutoSize("Close",22)) {
                    HideInspector();
                }
            }
            GUILayout.BeginHorizontal();
            DrawInspector();
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
                m_ObjectNameBuffer = obj.name;
            }
            m_EditTarget = null;
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
                if(UCL.Core.UI.UCL_GUILayout.ButtonAutoSize("Back", 22)) {
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
                    if(UCL.Core.UI.UCL_GUILayout.ButtonAutoSize("v", 22)) {
                        m_ShowTrans.Remove(node);
                    }
                } else {
                    if(UCL.Core.UI.UCL_GUILayout.ButtonAutoSize(">", 22)) {
                        m_ShowTrans.Add(node);
                    }
                }
            } else {
                UCL.Core.UI.UCL_GUILayout.ButtonAutoSize("-", 22);
            }
            GUILayout.BeginVertical();
            if(m_Selected == node) {
                if(UCL.Core.UI.UCL_GUILayout.ButtonAutoSize(node.name, 22, Color.green)) {
                    m_CurRoot = m_Selected;
                }
            } else {
                if(UCL.Core.UI.UCL_GUILayout.ButtonAutoSize(node.name, 22)) {
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
            if(m_Selected == null) return;
            using(var scope = new GUILayout.HorizontalScope("box", GUILayout.Width(m_WindowRect.width / 2f))) {
                GUILayout.BeginVertical();
                m_InspectorScrollPos = GUILayout.BeginScrollView(m_InspectorScrollPos);
                GUILayout.Label("Inspector");
                DrawSelectedInfo(m_Selected.gameObject);
                GUILayout.EndScrollView();
                GUILayout.EndVertical();
            }

        }
        virtual protected void DrawSelectedInfo(GameObject obj) {
            GUILayout.BeginVertical();
            GUILayout.BeginHorizontal();
            if(GUILayout.Button("Deselect", GUILayout.Width(80))) {
                SetSelectedObject(null);
            }
            m_ObjectNameBuffer = GUILayout.TextField(m_ObjectNameBuffer, 128);
            if(GUILayout.Button("Rename", GUILayout.Width(80))) {
                obj.name = m_ObjectNameBuffer;
            }
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
        public void DrawValue(string title, FieldInfo info, object parent_obj,System.Action<string> save_act) {
            GUILayout.BeginHorizontal();
            if(!(m_EditData is string)) {
                m_EditData = m_EditData.ToString();
            }
            m_EditData = UCL.Core.UI.UCL_GUILayout.TextField(title, (string)m_EditData, 80);
            GUILayout.FlexibleSpace();
            if(GUILayout.Button("Save")) {
                save_act.Invoke((string)m_EditData);
                m_EditTarget = null;
            }
            if(GUILayout.Button("Cancel")) {
                m_EditTarget = null;
            }
            GUILayout.EndHorizontal();
        }
        public void DrawVec3Value(string title, FieldInfo info, object parent_obj, System.Action<Vector3> save_act) {
            GUILayout.BeginHorizontal();
            if(!(m_EditData is Vector3)) {
                m_EditData = Vector3.zero;
            }
            m_EditData = UCL.Core.UI.UCL_GUILayout.Vector3Field(title, (Vector3)m_EditData);
            GUILayout.FlexibleSpace();
            if(GUILayout.Button("Save")) {
                save_act.Invoke((Vector3)m_EditData);
                m_EditTarget = null;
            }
            if(GUILayout.Button("Cancel")) {
                m_EditTarget = null;
            }
            GUILayout.EndHorizontal();
        }
        /// <summary>
        /// this is use to draw GenericType element, return true if need to delete element
        /// </summary>
        /// <param name="title"></param>
        /// <param name="obj"></param>
        /// <param name="set_value_act"></param>
        /// <returns></returns>
        public bool DrawElement(string title, object obj, System.Action<object> set_value_act) {
            bool delete = false;
            GUILayout.BeginHorizontal();
            
            if(obj is int) {
                int oval = (int)obj;
                int val = UCL.Core.UI.UCL_GUILayout.IntField(title, oval);
                if(val != oval) set_value_act(val);
            } else if(obj is float) {
                float oval = (float)obj;
                float val = UCL.Core.UI.UCL_GUILayout.FloatField(title, oval);
                if(val != oval) set_value_act(val);
            } else if(obj is string) {
                string oval = (string)obj;
                string val = UCL.Core.UI.UCL_GUILayout.TextField(title, oval);
                if(val != oval) set_value_act(val);
            } else {
                UCL.Core.UI.UCL_GUILayout.LabelAutoSize(title);
                UCL.Core.UI.UCL_GUILayout.LabelAutoSize(obj.ToString());
            }
            
            GUILayout.FlexibleSpace();
            if(GUILayout.Button("delete")) {
                delete = true;
            }
            GUILayout.EndHorizontal();
            return delete;
        }
        public void DrawObject(FieldInfo info, object obj, object parent_obj) {
            bool is_edit_target = false;
            bool log_info = false;
            bool editable = false;
            if(m_EditTarget!=null && m_EditTarget.Item1.Equals(obj) && m_EditTarget.Item2.Equals(info)) {
                is_edit_target = true;
            }
            var type = obj.GetType();
            string title = "(" + type.Name + ")" + info.Name + " : ";
            if(type.IsGenericType && !(obj is string)) {
                var GenericType = type.GetGenericTypeDefinition();
                var TypeInfo = type.GetTypeInfo();
                var GenericTypeArguments = TypeInfo.GenericTypeArguments;
                var ContentType = GenericTypeArguments[0];
                if(obj is IEnumerable) {
                    var container = (IEnumerable)obj;
                    if(GenericType == typeof(Dictionary<,>)) {
                        ContentType = GenericTypeArguments[1];//[0] is Key type [1] is Value type!!
                    }
                    GUILayout.BeginHorizontal();
                    if(is_edit_target) {
                        if(GUILayout.Button("v")) {
                            m_EditTarget = null;
                        }
                    } else {
                        if(GUILayout.Button(">")) {
                            m_EditTarget = new Tuple<object, FieldInfo>(obj, info);
                        }
                    }

                    GUILayout.BeginVertical();
                    UCL.Core.UI.UCL_GUILayout.LabelAutoSize(type.Name + "(" + ContentType.Name + ")" + info.Name);
                    if(is_edit_target) {
                        if(obj is IList) {
                            IList list = obj as IList;
                            int delete_at = -1;
                            for(int i = 0; i < list.Count; i++) {
                                int at = i;
                                if(DrawElement("Element " + at.ToString() + " : ", list[at], delegate (object val) {
                                    list[at] = val;
                                })) {
                                    delete_at = at;
                                }
                            }
                            if(delete_at >= 0) {
                                list.RemoveAt(delete_at);
                            }
                        } else if(obj is IDictionary) {
                            IDictionary dic = obj as IDictionary;
                            int i = 0;
                            object delete_at = null;
                            Tuple<object, object> alter_val = null;
                            foreach(var key in dic.Keys) {
                                var cur_key = key;
                                int at = i++;
                                if(DrawElement("Element " + at.ToString() + " : " + cur_key.ToString() + " : ", dic[cur_key],
                                    delegate (object val) {
                                        alter_val = new Tuple<object, object>(cur_key, val);
                                    })) {
                                    delete_at = cur_key;
                                }
                            }
                            if(delete_at != null) {
                                dic.Remove(delete_at);
                            }
                            if(alter_val != null) {
                                dic[alter_val.Item1] = alter_val.Item2;
                            }
                        } else {//Can't assign value....
                            int i = 0;
                            IEnumerator enumerator = container.GetEnumerator();
                            while(enumerator.MoveNext()) {
                                int at = i++;
                                var type_val = enumerator.Current;
                                DrawElement("Element " + at.ToString() + " : ", type_val, delegate (object val) {
                                });
                            }
                        }
                    }

                    GUILayout.EndVertical();
                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();
                } else {
                    log_info = true;
                    //var debug_data2 = DebugCommon.DebugOnGUI.Instance.CreateData("Unknown GenericType_" + nameof(obj))
                    //    .SetSize(m_ButtonSize).SetButAct(delegate () { });
                    //var debug_data3 = DebugCommon.DebugOnGUI.Instance.CreateData(obj.ToString())
                    //    .SetSize(m_ButtonSize).SetButAct(delegate () { });
                    //debug_data.AddSubData(debug_data2);
                    //debug_data2.AddSubData(debug_data3);
                }
            } 
            else if(obj is Array) {
                var ContentType = type.GetElementType();
                GUILayout.BeginHorizontal();
                if(is_edit_target) {
                    if(GUILayout.Button("v")) {
                        m_EditTarget = null;
                    }
                } else {
                    if(GUILayout.Button(">")) {
                        m_EditTarget = new Tuple<object, FieldInfo>(obj, info);
                    }
                }

                GUILayout.BeginVertical();
                UCL.Core.UI.UCL_GUILayout.LabelAutoSize("(" + type.Name + ")" + info.Name);
                if(is_edit_target) {
                    Array arr = obj as Array;
                    int delete_at = -1;
                    for(int i = 0; i < arr.Length; i++) {
                        int at = i;
                        if(DrawElement("Element "+at.ToString() + " : ", arr.GetValue(at), delegate (object val) {
                            arr.SetValue(val, at);
                        })) {
                            delete_at = at;
                        }
                    }
                    if(delete_at >= 0) {
                        Array new_arr = Array.CreateInstance(ContentType, arr.Length - 1);
                        for(int i = 0; i < arr.Length-1; i++) {
                            if(i < delete_at) {
                                new_arr.SetValue(arr.GetValue(i),i);
                            } else {
                                new_arr.SetValue(arr.GetValue(i+1), i);
                            }
                        }
                        info.SetValue(parent_obj, new_arr);
                        m_EditTarget = new Tuple<object, FieldInfo>(new_arr, info);
                    }
                }

                GUILayout.EndVertical();
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }
            #region numerical
            else if(obj is int) {
                editable = true;
                if(is_edit_target) {
                    DrawValue(title, info, parent_obj, (str) => {
                        int res_val = 0;
                        if(int.TryParse(str, out res_val)) {
                            info.SetValue(parent_obj, res_val);
                        }
                    });
                } else {
                    log_info = true;
                }
            } else if(obj is long) {
                editable = true;
                if(is_edit_target) {
                    DrawValue(title, info, parent_obj, (str) => {
                        long res_val = 0;
                        if(long.TryParse(str, out res_val)) {
                            info.SetValue(parent_obj, res_val);
                        }
                    });
                } else {
                    log_info = true;
                }
            } else if(obj is float) {
                editable = true;
                if(is_edit_target) {
                    DrawValue(title, info, parent_obj, (str) => {
                        float res_val = 0;
                        if(float.TryParse((string)m_EditData, out res_val)) {
                            info.SetValue(parent_obj, res_val);
                        }
                    });
                } else {
                    log_info = true;
                }
            } else if(obj is double) {
                editable = true;
                if(is_edit_target) {
                    DrawValue(title, info, parent_obj, (str) => {
                        double res_val = 0;
                        if(double.TryParse((string)m_EditData, out res_val)) {
                            info.SetValue(parent_obj, res_val);
                        }
                    });
                } else {
                    log_info = true;
                }
            }
            #endregion
            #region Vec
            else if(obj is Vector3) {
                editable = true;
                if(is_edit_target) {
                    DrawVec3Value(title, info, parent_obj, (vec) => {
                        info.SetValue(parent_obj, vec);
                    });
                } else {
                    log_info = true;
                }
            }
            #endregion
            else if(obj is string) {
                editable = true;
                if(is_edit_target) {
                    DrawValue(title, info, parent_obj, (str) => {
                        info.SetValue(parent_obj, str);
                    });
                } else {
                    log_info = true;
                }
            } else if(obj is Enum) {
                editable = true;
                List<string> strs = new List<string>();
                int at = 0;
                var enums = Enum.GetValues(type);
                for(int i = 0; i < enums.Length; i++) {
                    var item = enums.GetValue(i);
                    if(item.Equals(obj)) {
                        at = i;
                    }
                    strs.Add(item.ToString());
                }
                bool show = is_edit_target;
                GUILayout.BeginHorizontal();
                UCL.Core.UI.UCL_GUILayout.LabelAutoSize(title);
                var next = UCL.Core.UI.UCL_GUILayout.Popup(at, strs, ref show);
                if(!is_edit_target && show) {
                    m_EditTarget = new Tuple<object, FieldInfo>(obj, info);
                }
                GUILayout.EndHorizontal();
                if(next != at) {
                    info.SetValue(parent_obj, enums.GetValue(next));
                    m_EditTarget = null;
                }
            } else {//Unknown Type!!
                log_info = true;
            }
            if(log_info) {
                if(editable) {
                    GUILayout.BeginHorizontal();
                    UCL.Core.UI.UCL_GUILayout.LabelAutoSize(title + obj.ToString());
                    GUILayout.FlexibleSpace();
                    if(GUILayout.Button("Edit")) {
                        m_EditTarget = new Tuple<object, FieldInfo>(obj, info);
                        m_EditData = obj;
                    }
                    GUILayout.EndHorizontal();
                } else {
                    GUILayout.Label(title + obj.ToString());
                }
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