
using System.Collections;
using System.Collections.Generic;
using UnityEngine;


namespace UCL.Core
{
    public class UCL_ModulePlaylist : UCL_Asset<UCL_ModulePlaylist>
    {
        public UCL_ModulePlaylist() { }
        public UCL_ModulePlaylist(string iModuleID)
        {
            m_Playlist.Add(new UCL_ModuleEntry(iModuleID));
        }
        public Dictionary<string, UCL_Module> LoadPlaylist()
        {
            if (!UCL_ModuleService.Initialized)
            {
                Debug.LogError("UCL_ModulePlaylist.LoadPlaylist(), !UCL_ModuleService.Initialized");
                return null;
            }
            return UCL_ModuleService.Ins.LoadModulePlaylist(this);
        }

        public List<UCL_ModuleEntry> m_Playlist = new List<UCL_ModuleEntry>();
    }
}