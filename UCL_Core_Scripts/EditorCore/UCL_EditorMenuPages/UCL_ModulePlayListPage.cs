
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 09/30 2024

using Cysharp.Threading.Tasks;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UCL.Core.EditorLib.Page;
using UCL.Core.LocalizeLib;
using UCL.Core.UI;
using UnityEngine;
using UnityEngine.UI;

namespace UCL.Core.Page
{
    public class UCL_ModulePlayListPage : UCL_CommonEditorPage
    {
        public enum EditMode
        {
            Select,
            Edit,
        }
        public override string WindowName => $"{this.GetType().Name}({m_EditMode})";//UCL_LocalizeManager.Get("UCL_ModuleEditPage");

        private string SavePath => UCL_ModulePlaylist.RuntimeSavePath;
        static public UCL_ModulePlayListPage Create()
        {
            var aPage = UCL_EditorPage.Create<UCL_ModulePlayListPage>();
            aPage.Init();
            return aPage;
        }


        private UCL_ObjectDictionary m_DataDic = new UCL_ObjectDictionary();
        private EditMode m_EditMode = EditMode.Select;
        //private UCL_Module m_CurEditModule;
        //private UCL_ModulePath.PersistantPath.ModuleEntry ModuleConfig => m_CurEditModule.ModuleEntry;
        private UCL_ModulePlaylist m_ModulePlaylist = null;
        private string m_NewPlaylistID = "Playlist Name";
        private int m_SelectedIndex = 0;
        public void Init()
        {
            //m_CurEditModule = iModule;
            //m_ID = iModule.ID;
            Directory.CreateDirectory(SavePath);
        }

        public override void OnClose()
        {
            //UCL_ModuleService.Ins.ClearCurrentEditModule();
            UCL_ModuleService.Ins.SetState(UCL_ModuleService.State.Main);//回到主頁
            base.OnClose();
        }
        protected override void TopBarButtons()
        {
            base.TopBarButtons();
            //            GUILayout.Label($"[{m_CurEditModule.ID}]", UCL_GUIStyle.LabelStyle);
            //#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            if (GUILayout.Button(UCL_LocalizeManager.Get("OpenFolder"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                var path = SavePath;
                Directory.CreateDirectory(path);
                UCL.Core.FileLib.WindowsLib.OpenExplorer(path);
            }
            //            if (GUILayout.Button(UCL_LocalizeManager.Get("OpenModuleInstallFolder"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            //            {
            //                UCL.Core.FileLib.WindowsLib.OpenExplorer(ModuleConfig.InstallFolder);
            //            }

            //#endif

        }
        protected override void ContentOnGUI()
        {
            switch (m_EditMode)
            {
                case EditMode.Select:
                    DrawSelectMode();
                    break;
                case EditMode.Edit:
                    DrawEditMode();
                    break;
            }
            using (var aScope = new GUILayout.HorizontalScope())
            {

                //if (GUILayout.Button("Zip Module", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                //{
                //    ModuleConfig.ZipModule();
                //}
            }
            //UCL_GUILayout.DrawObjectData(m_CurEditModule, m_DataDic.GetSubDic("CurEditModule"), "CurEditModule");

        }
        protected override void BackButtonClicked()
        {
            switch (m_EditMode)
            {
                case EditMode.Edit:
                    m_EditMode = EditMode.Select;
                    break;
                default:
                    {
                        base.BackButtonClicked();
                        break;
                    }
            }
            
        }
        protected void LoadCurModulePlaylist(string id)
        {
            m_ModulePlaylist = UCL_ModulePlaylist.LoadRuntimePlaylist(id);
        }
        protected void DrawSelectMode()
        {
            string format = $"*{UCL_ModulePlaylist.FileFormat}";
            var files = Directory.GetFiles(SavePath, format, SearchOption.TopDirectoryOnly);
            if (files.IsNullOrEmpty())//Add default!!
            {
                UCL_ModulePlaylist.SaveRuntimePlaylist(UCL_ModulePlaylist.DefaultPlaylist);
                files = Directory.GetFiles(SavePath, format, SearchOption.TopDirectoryOnly);
            }
            var fileNames = files.Select(x => Path.GetFileName(x).Replace(UCL_ModulePlaylist.FileFormat, string.Empty)).ToList();


            using (var scope = new GUILayout.HorizontalScope())
            {
                GUILayout.Label("Current Playlist", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                string id = UCL_ModulePlaylist.CurPlaylistID;
                string newID = UCL_GUILayout.PopupAuto(id, fileNames, m_DataDic, "fileNames");
                if(newID != id)//Update!!
                {
                    UCL_ModulePlaylist.CurPlaylistID = newID;
                    m_ModulePlaylist = null;
                }
            }
            if (m_ModulePlaylist == null)
            {
                LoadCurModulePlaylist(UCL_ModulePlaylist.CurPlaylistID);
            }
            using (var scope = new GUILayout.HorizontalScope())
            {
                if (GUILayout.Button($"Create New", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    var newPlaylist = new UCL_ModulePlaylist(UCL_ModuleEntry.CoreModuleID);
                    newPlaylist.ID = m_NewPlaylistID;
                    UCL_ModulePlaylist.SaveRuntimePlaylist(newPlaylist);

                    UCL_ModulePlaylist.CurPlaylistID = m_NewPlaylistID;

                    m_ModulePlaylist = newPlaylist;
                }
                m_NewPlaylistID = GUILayout.TextField(m_NewPlaylistID, UCL_GUIStyle.TextFieldStyle);
                
            }
            using (var scope = new GUILayout.HorizontalScope())
            {
                if (GUILayout.Button($"Save", UCL_GUIStyle.GetButtonStyle(Color.white, 24), GUILayout.ExpandWidth(false)))
                {
                    UCL_ModulePlaylist.SaveRuntimePlaylist(m_ModulePlaylist);
                }
                if (UCL_ModuleService.Ins.LoadingPlaylist)
                {
                    
                    var now = System.DateTime.Now.Second % 4;
                    string dynamicStr = new string('.', now);
                    GUILayout.Label($"Loading Playlist{dynamicStr}", UCL_GUIStyle.LabelStyle);
                }
                else
                {
                    if (GUILayout.Button($"Load current playlist", UCL_GUIStyle.GetButtonStyle(Color.white, 24), GUILayout.ExpandWidth(false)))
                    {
                        UCL_ModuleService.Ins.LoadModulePlaylistAsync(m_ModulePlaylist, default).Forget();

                        //LoadCurModulePlaylist(UCL_ModulePlaylist.CurPlaylistID);
                    }
                }

            }
            m_ModulePlaylist.OnGUI(m_DataDic.GetSubDic("m_ModulePlaylist"));
            //string edit = string.Empty;
            //foreach(var fileName in fileNames)
            //{
            //    //string fileName = Path.GetFileName(file).Replace(".json", string.Empty);
            //    GUILayout.BeginHorizontal();
            //    if(GUILayout.Button($"Edit", UCL_GUIStyle.GetButtonStyle(Color.white, 24), GUILayout.ExpandWidth(false)))
            //    {
            //        edit = fileName;
            //    }
            //    GUILayout.Label($"{fileName}", UCL_GUIStyle.GetLabelStyle(Color.white, 24));
            //    GUILayout.EndHorizontal();
            //}
            //if (!string.IsNullOrEmpty(edit))
            //{
            //    m_EditMode = EditMode.Edit;
            //    m_ModulePlaylist = UCL_ModulePlaylist.LoadRuntimePlaylist(edit);
            //}
        }
        protected void DrawEditMode()
        {
            if (m_ModulePlaylist == null)
            {
                m_ModulePlaylist = new UCL_ModulePlaylist(UCL_ModuleEntry.CoreModuleID);
            }
            UCL_GUILayout.DrawObjectData(m_ModulePlaylist, m_DataDic.GetSubDic("m_ModulePlaylist"), "ModulePlaylist", true);
        }
    }
}
