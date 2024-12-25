
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UCL.Core.JsonLib;
using UCL.Core.LocalizeLib;
using UCL.Core.UI;
using UnityEngine;


namespace UCL.Core
{
    [UCL.Core.ATTR.UCL_GroupIDAttribute(UCL_AssetGroup.Config)]
    [UCL.Core.ATTR.UCL_Sort((int)UCL_AssetGroup.EditConfigType.UCL_ModulePlaylist)]
    public class UCL_ModulePlaylist : UCL_Asset<UCL_ModulePlaylist>
    {
        #region static
        public const string DefaultID = "Default";
        public const string FileFormat = ".json";
        public static UCL_ModulePlaylist DefaultPlaylist
        {
            get
            {
                if(s_DefaultPlaylist == null)
                {
                    s_DefaultPlaylist = new UCL_ModulePlaylist(UCL_ModuleEntry.CoreModuleID);
                    s_DefaultPlaylist.ID = DefaultID;
                }
                return s_DefaultPlaylist;
            }
        }
        private const string CurPlaylistKey = "CurPlaylistID";
        public static string CurPlaylistID
        {
            get => PlayerPrefs.GetString(CurPlaylistKey, DefaultID);
            set => PlayerPrefs.SetString(CurPlaylistKey, value);
        }
        public static UCL_ModulePlaylist CurPlaylist => LoadRuntimePlaylist(CurPlaylistID);

        private static UCL_ModulePlaylist s_DefaultPlaylist;

        public static string RuntimeSavePath => Path.Combine(Application.persistentDataPath, "ModulePlaylist");
        public static string GetRuntimePlaylistPath(string id) => Path.Combine(RuntimeSavePath, $"{id}.json");
        public static void SaveRuntimePlaylist(UCL_ModulePlaylist playlist)
        {
            string id = playlist.ID;
            if (string.IsNullOrEmpty(id)) id = DefaultID;
            var root = RuntimeSavePath;
            var json = playlist.SerializeToJson();
            Directory.CreateDirectory(root);
            var path = GetRuntimePlaylistPath(id);
            File.WriteAllText(path, json.ToJsonBeautify());
        }
        public static UCL_ModulePlaylist LoadRuntimePlaylist(string id)
        {
            var root = RuntimeSavePath;
            var path = GetRuntimePlaylistPath(id);
            UCL_ModulePlaylist playList = null;
            if (!File.Exists(path))
            {
                playList = DefaultPlaylist.CloneObject();
            }
            else
            {
                playList = new UCL_ModulePlaylist();
                string json = File.ReadAllText(path);
                JsonData jsonData = JsonData.ParseJson(json);
                playList.DeserializeFromJson(jsonData);
            }
            playList.ID = id;
            return playList;
        }
        #endregion


        public override void Preview(UCL_ObjectDictionary iDataDic, bool iIsShowEditButton = false)
        {
            using (var aScope = new GUILayout.VerticalScope("box", GUILayout.ExpandWidth(false)))//, GUILayout.MinWidth(130)
            {
                if (iIsShowEditButton)
                {
                    ShowEditButtonOnGUI();
                }
                GUILayout.Label($"{UCL_LocalizeManager.Get("Preview")}({ID})", UCL.Core.UI.UCL_GUIStyle.LabelStyle);

                foreach (var module in m_Playlist)
                {
                    if (module.IsEnable)
                    {
                        GUILayout.Label($"{module}", UCL.Core.UI.UCL_GUIStyle.LabelStyle);
                    }
                }
                //GUILayout.Label($"{this.UCL_ToString()}", UCL.Core.UI.UCL_GUIStyle.LabelStyle);
                //UCL_GUILayout.Preview.OnGUI(this, iDataDic.GetSubDic("DrawPreview"));
                //UCL_GUILayout.DrawObjectData(this, iDataDic.GetSubDic("Preview Data"), string.Empty, true);

            }
        }

        public List<ModuleSetting> m_Playlist = new List<ModuleSetting>();

        public UCL_ModulePlaylist() { }
        public UCL_ModulePlaylist(string iModuleID)
        {
            m_Playlist.Add(new ModuleSetting(iModuleID));
        }

        public Dictionary<string, UCL_Module> LoadPlaylist(bool loadDependencies)
        {
            //if (!UCL_ModuleService.Initialized)
            //{
            //    Debug.LogError("UCL_ModulePlaylist.LoadPlaylist(), !UCL_ModuleService.Initialized");
            //    return null;
            //}

            if (m_Playlist.Count == 0)
            {
                m_Playlist.Add(new ModuleSetting(UCL_ModuleEntry.CoreModuleID));
            }
            return UCL_ModuleService.Ins.LoadModulePlaylist(this, loadDependencies);
        }

        public IEnumerable<ModuleSetting> EnablePlaylist => m_Playlist.Where(module => module.IsEnable);

        //public List<UCL_ModuleEntry> m_Playlist = new List<UCL_ModuleEntry>();
        public override void DeserializeFromJson(JsonData iJson)
        {
            base.DeserializeFromJson(iJson);
            HashSet<string> idSet = new HashSet<string>();
            var service = UCL_ModuleService.Ins;

            //if(service != null)//TODO check exist!!
            //{
            //    var allIds = new HashSet<string>(service.GetAllModuleIDs());
            //    //allIds.Add(UCL_ModuleEntry.CoreModuleID);//Core不能移除
            //    for (int i = m_Playlist.Count - 1; i >= 0; i--)
            //    {
            //        string id = m_Playlist[i].ID;

            //        if (!allIds.Contains(id))//not exist!!
            //        {
            //            Debug.LogError($"{GetType().Name}.DeserializeFromJson id:{id}, !allIds.Contains(id),allIds:{allIds.ConcatToString()}");
            //            m_Playlist.RemoveAt(i);
            //        }
            //    }
            //}

            //Remove repeated module
            for(int i = m_Playlist.Count - 1; i >= 0 ; i--)
            {
                string id = m_Playlist[i].ID;

                if (idSet.Contains(id))//repeated
                {
                    m_Playlist.RemoveAt(i);
                }
                else
                {
                    idSet.Add(id);
                }
            }
        }
    }

    public class ModuleSetting : UnityJsonSerializable, UCLI_IsEnable, UCLI_ShortName, UCLI_NameOnGUI
    {
        public string GetShortName() => this.ToString();

        public override string ToString()
        {
            return m_Module.ID;
            //return $"[{(m_IsEnable? "Enable" : "Disable")}]{m_Module.ID}(ModuleSetting)";
        }
        public bool IsEnable { get => m_IsEnable; set => m_IsEnable = value; }
        public string ID { get => m_Module.ID; set => m_Module.ID = value; }

        public bool m_IsEnable = true;
        [UCL.Core.ATTR.AlwaysExpendOnGUI] public UCL_ModuleEntry m_Module = new UCL_ModuleEntry();

        public ModuleSetting() { }
        public ModuleSetting(string id)
        {
            m_Module.ID = id;
        }

        #region Interface
        virtual public void NameOnGUI(UCL.Core.UCL_ObjectDictionary iDic, string iDisplayName)
        {
            var module = m_Module.Module;
            using (var scopeV = new GUILayout.VerticalScope())
            {
                using (var scope = new GUILayout.HorizontalScope())
                {
                    m_IsEnable = UCL_GUILayout.CheckBox(m_IsEnable);
                    var aAllIDs = m_Module.GetAllIDs();
                    var aAllNames = UCL_ModuleService.Ins.GetAllModuleNames();
                    var index = UCL_GUILayout.PopupAuto(aAllIDs.IndexOf(m_Module.ID), aAllNames, iDic, nameof(m_Module), 10
                        , GUILayout.Width(UCL_GUIStyle.GetScaledSize(280)));

                    m_Module.ID = aAllIDs[index];

                    //if (module != null)
                    //{
                    //    string id = module.m_Config.m_ID;
                    //    GUILayout.Label(id, UCL_GUIStyle.LabelStyle);
                    //}
                }
                if (module != null)
                {
                    var logo = module.Logo;
                    if (logo != null)
                    {
                        using (var scope = new GUILayout.HorizontalScope())
                        {
                            float size = UCL_GUIStyle.GetScaledSize(40);
                            GUILayout.Box(logo, UCL_GUIStyle.BoxStyle, GUILayout.Width(size), GUILayout.Height(size));
                            GUILayout.Label(module.m_Config.m_Description, UCL_GUIStyle.LabelStyle);
                        }
                    }
                }
            }

            //GUILayout.Label(string.Format("{0}({1})", iDisplayName, CardCount), UCL.Core.UI.UCL_GUIStyle.LabelStyle);//GetShortName()
        }
        #endregion
    }
}