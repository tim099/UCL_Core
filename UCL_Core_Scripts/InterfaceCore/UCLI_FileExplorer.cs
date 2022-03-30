using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace UCL.Core
{
    public interface UCLI_FileExplorer
    {
        bool DirectoryExists(string iPath);
        /// <summary>
        /// Returns the names of the subdirectories (including their paths) 
        /// that match the specified search pattern in the specified directory, and optionally searches subdirectories.
        /// </summary>
        /// <param name="iPath">The relative or absolute path to the directory to search. This string is not case-sensitive.</param>
        /// <param name="iSearchPattern">The search string to match against the names of subdirectories in path.
        /// This parameter can contain a combination of valid literal and wildcard characters, but it doesn't support regular expressions.</param>
        /// <param name="iSearchOption">One of the enumeration values that specifies whether the search operation 
        /// should include all subdirectories or only the current directory.</param>
        /// <returns></returns>
        string[] GetDirectories(string iPath, string iSearchPattern = "*", SearchOption iSearchOption = SearchOption.AllDirectories, bool iRemoveRootPath = false);

        /// <summary>
        /// Returns the names of files (including their paths) that match the specified search pattern in the specified directory.
        /// </summary>
        /// <param name="iPath">The relative or absolute path to the directory to search. This string is not case-sensitive.</param>
        /// <param name="iSearchPattern">The search string to match against the names of files in path. This parameter can contain a combination of valid literal path
        /// and wildcard (* and ?) characters, but it doesn't support regular expressions.</param>
        /// <returns></returns>
        string[] GetFiles(string iPath, string iSearchPattern = "*", SearchOption iSearchOption = SearchOption.TopDirectoryOnly, bool iRemoveRootPath = false);
    }
    public class UCL_FileExplorer : UCLI_FileExplorer
    {
        public static UCL_FileExplorer Ins {
            get
            {
                if (_Ins == null) _Ins = new UCL_FileExplorer();
                return _Ins;
            }
        }
        private static UCL_FileExplorer _Ins = null;
        virtual public bool DirectoryExists(string iPath)
        {
            return Directory.Exists(iPath);
        }
        /// <summary>
        /// Returns the names of the subdirectories (including their paths) 
        /// that match the specified search pattern in the specified directory, and optionally searches subdirectories.
        /// </summary>
        /// <param name="iPath">The relative or absolute path to the directory to search. This string is not case-sensitive.</param>
        /// <param name="iSearchPattern">The search string to match against the names of subdirectories in path.
        /// This parameter can contain a combination of valid literal and wildcard characters, but it doesn't support regular expressions.</param>
        /// <param name="iSearchOption">One of the enumeration values that specifies whether the search operation 
        /// should include all subdirectories or only the current directory.</param>
        /// <returns></returns>
        virtual public string[] GetDirectories(string iPath, string iSearchPattern = "*",
                SearchOption iSearchOption = SearchOption.AllDirectories, bool iRemoveRootPath = false) {
            if (!DirectoryExists(iPath)) return new string[0];
            var aDirs = Directory.GetDirectories(iPath, iSearchPattern, iSearchOption);
            if (iRemoveRootPath)
            {
                for (int i = 0; i < aDirs.Length; i++)
                {
                    aDirs[i] = FileLib.Lib.GetFolderName(aDirs[i]);
                }
            }
            return aDirs;
        }
        /// <summary>
        /// Returns the names of files (including their paths) that match the specified search pattern in the specified directory.
        /// </summary>
        /// <param name="iPath">The relative or absolute path to the directory to search. This string is not case-sensitive.</param>
        /// <param name="iSearchPattern">The search string to match against the names of files in path. This parameter can contain a combination of valid literal path
        /// and wildcard (* and ?) characters, but it doesn't support regular expressions.</param>
        /// <returns></returns>
        virtual public string[] GetFiles(string iPath, string iSearchPattern = "*", SearchOption iSearchOption = SearchOption.TopDirectoryOnly,
            bool iRemoveRootPath = false)
        {
            if (!DirectoryExists(iPath))
            {
                return new string[0];
            }
            var aFilePaths = Directory.GetFiles(iPath, iSearchPattern, iSearchOption);
            if (iRemoveRootPath)
            {
                for (int i = 0; i < aFilePaths.Length; i++)
                {
                    aFilePaths[i] = FileLib.Lib.GetFileName(aFilePaths[i]);
                }
            }
            return aFilePaths;
        }
    }
}