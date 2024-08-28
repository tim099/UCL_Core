
using System.Collections;
using System.Collections.Generic;
using UnityEngine;


namespace UCL.Core
{
    [UCL.Core.ATTR.UCL_GroupIDAttribute(UCL_AssetGroup.Config)]
    public class UCL_ModulePlaylist : UCL_Asset<UCL_ModulePlaylist>
    {
        public static UCL_ModulePlaylist DefaultPlaylist
        {
            get
            {
                if(s_DefaultPlaylist == null)
                {
                    s_DefaultPlaylist = new UCL_ModulePlaylist(UCL_ModuleEntry.CoreModuleID);
                }
                return s_DefaultPlaylist;
            }
        }
        private static UCL_ModulePlaylist s_DefaultPlaylist;

        public UCL_ModulePlaylist() { }
        public UCL_ModulePlaylist(string iModuleID)
        {
            m_Playlist.Add(new UCL_ModuleEntry(iModuleID));
        }
        public Dictionary<string, UCL_Module> LoadPlaylist()
        {
            //if (!UCL_ModuleService.Initialized)
            //{
            //    Debug.LogError("UCL_ModulePlaylist.LoadPlaylist(), !UCL_ModuleService.Initialized");
            //    return null;
            //}

            if (m_Playlist.Count == 0)
            {
                m_Playlist.Add(new UCL_ModuleEntry(UCL_ModuleEntry.CoreModuleID));
            }
            return UCL_ModuleService.Ins.LoadModulePlaylist(this);
        }

        public List<UCL_ModuleEntry> m_Playlist = new List<UCL_ModuleEntry>();
    }
}