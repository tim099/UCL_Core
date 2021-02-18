using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using UnityEngine;

namespace UCL.Core.FileLib {
    public enum LibName {
        UCL_CoreLib = 0,
        UCL_TweenLib,
        UCL_GameLib,
        UCL_BuildLib,
    }
#if UNITY_EDITOR
    static public class EditorLib {
        public static string GetLibFolderPath(LibName libname) {
            return GetLibFolderPath(libname.ToString());
        }
        public static string GetLibFolderPath(string LibName) {
            var res = Resources.Load(LibName);// + ".txt"
            string path = UnityEditor.AssetDatabase.GetAssetPath(res);

            return FileLib.Lib.RemoveFolderPath(path, 2);
        }
        public static string GetCoreFolderPath() {
            return UCL_CoreSetting.GetFolderPath();
        }
        public static void ExploreFile(string iTitle, string iFolder, string iExtension = "") {
            string path = UnityEditor.EditorUtility.OpenFilePanel(iTitle, iFolder, iExtension);

            if(!string.IsNullOrEmpty(path) && path != iFolder) {
                Application.OpenURL(path);
            }
        }
        public static void ExploreFolder(string folder) {
            string path = UnityEditor.EditorUtility.OpenFolderPanel("Open Folder", folder, string.Empty);

            if(!string.IsNullOrEmpty(path) && path != folder) {
                Application.OpenURL(path);
            }
        }
        /// <summary>
        /// Open file explorer under assets folder
        /// 在Assets資料夾中開啟檔案瀏覽器
        /// </summary>
        /// <param name="iFilePath">file path</param>
        /// <returns></returns>
        public static string OpenAssetsFileExplorer(string iFilePath) {
            string asset_root = Application.dataPath.Replace("Assets", string.Empty);
            string assets_path = asset_root + iFilePath;
            string path = UnityEditor.EditorUtility.OpenFilePanel("Open Folder", assets_path, string.Empty);
            if(string.IsNullOrEmpty(path)) return iFilePath;

            return path.Replace(asset_root, "");
        }

        /// <summary>
        /// Open folder explorer under assets folder
        /// 在Assets資料夾中開啟資料夾瀏覽器
        /// </summary>
        /// <param name="folder"></param>
        /// <returns></returns>
        public static string OpenAssetsFolderExplorer(string folder) {
            string asset_root = Application.dataPath.Replace("Assets", string.Empty);
            string assets_path = asset_root + folder;
            string path = UnityEditor.EditorUtility.OpenFolderPanel("Open Folder", assets_path, string.Empty);
            if(string.IsNullOrEmpty(path)) return folder;

            return path.Replace(asset_root, "");
        }
        /// <summary>
        /// Open folder explorer
        /// 開啟資料夾瀏覽器
        /// </summary>
        /// <param name="folder"></param>
        /// <returns></returns>
        public static string OpenFolderExplorer(string folder) {
            string path = UnityEditor.EditorUtility.OpenFolderPanel("Open Folder", folder, string.Empty);
            if(string.IsNullOrEmpty(path)) return folder;

            return path;
        }
        public static string SelectScript(MonoBehaviour be) {
            var path = GetScriptPath(be);
            UnityEditor.Selection.activeObject = UnityEditor.AssetDatabase.LoadMainAssetAtPath(path);
            return path;
        }
        public static string SelectScript(ScriptableObject so) {
            var path = GetScriptPath(so);
            UnityEditor.Selection.activeObject = UnityEditor.AssetDatabase.LoadMainAssetAtPath(path);
            return path;
        }
        public static string GetScriptPath(MonoBehaviour be) {
            UnityEditor.MonoScript monoScript = UnityEditor.MonoScript.FromMonoBehaviour(be);
            //Debug.LogWarning("path:" + UnityEditor.AssetDatabase.GetAssetPath(monoScript));
            return UnityEditor.AssetDatabase.GetAssetPath(monoScript);
        }
        public static string GetScriptPath(ScriptableObject so) {
            UnityEditor.MonoScript monoScript = UnityEditor.MonoScript.FromScriptableObject(so);
            //Debug.LogWarning("path:" + UnityEditor.AssetDatabase.GetAssetPath(monoScript));
            return UnityEditor.AssetDatabase.GetAssetPath(monoScript);
        }
        /// <summary>
        /// this funtion is for memo(not useful)
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public static Object LoadAssets(string path) {
           return  UnityEditor.AssetDatabase.LoadMainAssetAtPath(Path.Combine("Assets", path));
        }
    }
#endif
    static public class Lib{

        public static string GetProjectPath() {
            return Application.dataPath.Replace("Assets", string.Empty);
        }

        /// <summary>
        /// Remove file name from path and return folder path
        /// </summary>
        /// <param name="file_path"></param>
        /// <returns></returns>
        public static string GetFolderPath(string file_path) {
            for(int i = file_path.Length - 1; i >= 0; i--) {
                var c = file_path[i];
                if(c == '/' || c == '\\') {
                    file_path = file_path.Substring(0, i);
                    break;
                }
            }
            return file_path;
        }
        /// <summary>
        /// Remove folder and file from path
        /// Example path is "root/folder/c.txt" and remove_count is 2, then return "root" 
        /// 根據設定數量移除路徑
        /// </summary>
        /// <param name="path"></param>
        /// <param name="remove_count"></param>
        /// <returns></returns>
        public static string RemoveFolderPath(string path, int remove_count = 1) {
            if(remove_count <= 0) return path;

            int i = path.Length - 1;
            for(; i >= 0; i--) {
                var c = path[i];
                if(c == '/' || c == '\\') {
                    if(--remove_count <= 0) break;
                }
            }
            if(i <= 0) return string.Empty;
            return path.Substring(0, i);
        }
        /// <summary>
        /// return filename from input path
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public static string GetFileName(string path) {
            int i = path.Length - 1;
            for(; i >= 0; i--) {
                var c = path[i];
                if(c == '/' || c == '\\') {
                    return path.Substring(i + 1, path.Length - i - 1);
                }
            }
            return path;
        }
        /// <summary>
        /// Split a path into two part
        /// Example path is "root/folder/c.txt" and split_at is 2, then return "root","folder/c.txt" 
        /// 根據指定的位置切分路徑
        /// </summary>
        /// <param name="path">input path</param>
        /// <param name="split_at">split position</param>
        /// <returns></returns>
        public static System.Tuple<string,string> SplitPath(string path, int split_at = 1) {
            if(split_at <= 0) return new System.Tuple<string, string>(path,"");

            int i = path.Length - 1;
            for(; i >= 0; i--) {
                var c = path[i];
                if(c == '/' || c == '\\') {

                    //path = path.Substring(0, i);
                    if(--split_at <= 0) break;
                }
            }
            if(i <= 0) return new System.Tuple<string, string>("", path);
            return new System.Tuple<string, string>(path.Substring(0, i), path.Substring(i + 1, path.Length - i - 1));
        }
        public static void WriteBinaryToFile<T>(string path, T target, FileMode fileMode = FileMode.Create) {
            using(Stream stream = File.Open(path, fileMode)) {
                var formatter = new System.Runtime.Serialization.Formatters.Binary.BinaryFormatter();
                formatter.Serialize(stream, target);
            }
        }
        public static string RemoveFileExtension(string path, char separator = '.') {
            if(path.Length <= 1) return path;//No FileExtension
            for(int i = path.Length - 2; i >= 0; i--) {
                if(path[i] == separator) return path.Substring(0, i);
            }
            return path;//No FileExtension
            
        }
        public static T ReadBinaryFromFile<T>(string path) {
            if(!File.Exists(path)) return default;

            using(Stream stream = File.Open(path, FileMode.Open)) {
                var formatter = new System.Runtime.Serialization.Formatters.Binary.BinaryFormatter();
                return (T)formatter.Deserialize(stream);
            }
        }
        public static void SerializeToFile<T>(string path, T obj) {
            var data = MarshalLib.Lib.ToByteArray(obj);//SerializeObject(obj);
            File.WriteAllBytes(path, data);
        }
        public static T DeserializeFromFile<T>(string path) {
            if(!File.Exists(path)) return default;
            var data = File.ReadAllBytes(path);
            return MarshalLib.Lib.ToStructure<T>(data); //DeSerializeObject(data);
        }

        public static object DeSerializeObject(byte[] data) {
            using(MemoryStream ms = new MemoryStream()) {
                BinaryFormatter binForm = new BinaryFormatter();
                ms.Write(data, 0, data.Length);
                ms.Seek(0, SeekOrigin.Begin);

                return binForm.Deserialize(ms);
            }
        }
        /// <summary>
        /// Serialize Object into byte array
        /// </summary>
        /// <param name="obj"></param>
        /// <returns></returns>
        public static byte[] SerializeObject(object obj) {
            using(MemoryStream ms = new MemoryStream()) {
                BinaryFormatter bf = new BinaryFormatter();
                bf.Serialize(ms, obj);
                return ms.ToArray();
            }
        }
        public static string GetFilesPath(bool create_if_not_exist = false) {
            string path = Path.Combine(Application.persistentDataPath, "files");
            if(create_if_not_exist) CreateDirectory(path);
            return path;
        }
        /// <summary>
        /// return the file extension
        /// Example path is "root/folder/c.txt", then return "txt" 
        /// 抓取副檔名並回傳
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public static string GetFileExtension(string path) {
            if(path.Length <= 1) return string.Empty;//No FileExtension
            int i = path.Length - 2;
            for(; i >= 0; i--) {
                if(path[i] == '.') return path.Substring(i + 1, path.Length - i - 1);
            }
            return string.Empty;//No FileExtension
        }
        /// <summary>
        /// Returns the names of the subdirectories (including their paths) 
        /// that match the specified search pattern in the specified directory, and optionally searches subdirectories.
        /// </summary>
        /// <param name="path">The relative or absolute path to the directory to search. This string is not case-sensitive.</param>
        /// <param name="searchPattern">The search string to match against the names of subdirectories in path.
        /// This parameter can contain a combination of valid literal and wildcard characters, but it doesn't support regular expressions.</param>
        /// <param name="searchOption">One of the enumeration values that specifies whether the search operation 
        /// should include all subdirectories or only the current directory.</param>
        /// <returns></returns>
        public static string[] GetDirectories(string path,
            string searchPattern = "*",
            SearchOption searchOption = SearchOption.AllDirectories) {

            return Directory.GetDirectories(path, searchPattern, searchOption);
        }
        /// <summary>
        /// Returns the names of files (including their paths) that match the specified search pattern in the specified directory.
        /// </summary>
        /// <param name="path">The relative or absolute path to the directory to search. This string is not case-sensitive.</param>
        /// <param name="searchPattern">The search string to match against the names of files in path. This parameter can contain a combination of valid literal path
        /// and wildcard (* and ?) characters, but it doesn't support regular expressions.</param>
        /// <returns></returns>
        public static string[] GetFiles(string path, string searchPattern = "*", 
            SearchOption searchOption = SearchOption.TopDirectoryOnly) {
            return Directory.GetFiles(path, searchPattern, searchOption);
        }
        public static void CreateDirectory(string path) {
            if(string.IsNullOrEmpty(path)) return;
            if(path[path.Length - 1] == '/') {
                path.Remove(path.Length - 1);
            }
            if(!Directory.Exists(path)) {
                Directory.CreateDirectory(path);
            }
        }
        public static void WriteToFile(string data, string path) {
            using(var writer = OpenWriteStream(path)) {
                writer.WriteLine(data);
                writer.Close();
            }
        }
        /// <summary>
        /// Copy files from iSourceDirectory to iDestinationDirectory
        /// </summary>
        /// <param name="iSourceDirectory">Source Directory</param>
        /// <param name="iDestinationDirectory">Destination Directory</param>
        /// <param name="iIgnoreFileExtensions">File with this extensions will be ignore</param>
        static public void CopyDirectory(string iSourceDirectory, string iDestinationDirectory, List<string> iIgnoreFileExtensions) {
            if(!Directory.Exists(iDestinationDirectory)) {
                Directory.CreateDirectory(iDestinationDirectory);
            }
            var aFiles = Directory.GetFiles(iSourceDirectory);
            for(int i = 0; i < aFiles.Length; i++) {
                var aFile = aFiles[i];
                if(iIgnoreFileExtensions != null && iIgnoreFileExtensions.Count > 0) {
                    var aFileExtension = GetFileExtension(aFile);
                    bool aIsIgnoreFile = false;
                    foreach(var aIgnoreFileExtension in iIgnoreFileExtensions) {
                        if(aFileExtension == aIgnoreFileExtension) {
                            aIsIgnoreFile = true;
                            break;
                        }
                    }
                    if(aIsIgnoreFile) {
                        continue;
                    }
                }
                File.Copy(aFile, aFile.Replace(iSourceDirectory, iDestinationDirectory), true);
            }
            var aSubDirs = Directory.GetDirectories(iSourceDirectory);
            for(int i = 0; i < aSubDirs.Length; i++) {
                var aSubDir = aSubDirs[i];
                CopyDirectory(aSubDir, aSubDir.Replace(iSourceDirectory, iDestinationDirectory), iIgnoreFileExtensions);
            }
        }
        public static void CopyDirectory(string source, string target) {
            if(source == target) {
                return;
            }
            if(!Directory.Exists(target)) {
                Directory.CreateDirectory(target);
            }
            DirectoryInfo source_info = new DirectoryInfo(source), target_info = new DirectoryInfo(target);
            foreach(FileInfo file_info in source_info.GetFiles()) {
                file_info.CopyTo(Path.Combine(target.ToString(), file_info.Name), true);
            }
            foreach(DirectoryInfo diSourceSubDir in source_info.GetDirectories()) {
                DirectoryInfo nextTargetSubDir = target_info.CreateSubdirectory(diSourceSubDir.Name);
                CopyDirectory(diSourceSubDir.FullName, nextTargetSubDir.FullName);
            }
        }
        public static StreamWriter OpenWriteStream(string path) {
            string[] strs = path.Split('/');
            string tmp_path = "";
            for(int i = 0; i < strs.Length - 1; i++) {
                tmp_path += strs[i];
                if(!Directory.Exists(tmp_path) && tmp_path != "") {
                    Debug.Log("Directory.CreateDirectory(tmp_path):" + tmp_path);
                    Directory.CreateDirectory(tmp_path);
                }
                tmp_path += "/";
            }
            StreamWriter file = new StreamWriter(path);
            return file;
        }
    }
    static public class StreamingAssetsLib {
        /// <summary>
        /// Conver the path into StreamingAssetsPath
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public static string GetStreamingAssetsPath(string path) {
            return Path.Combine(Application.streamingAssetsPath, path.TrimStart('/')).Replace('\\', '/');
        }
    }
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
    static public class WindowsLib {
        /// <summary>
        /// Open explorer to folder within Assets folder
        /// </summary>
        /// <param name="folder">target folder within Assets folder</param>
        static public void OpenAssetExplorer(string folder) {
            string asset_root = Application.dataPath.Replace("Assets", string.Empty);
            var path = asset_root + folder;
            //Debug.LogWarning("asset_root + folder:" + asset_root + folder);
            System.Diagnostics.Process.Start(asset_root + folder);//"explorer.exe", 
        }
        /// <summary>
        /// Open explorer of target folder
        /// </summary>
        /// <param name="folder"></param>
        static public void OpenExplorer(string folder) {
            //Debug.LogWarning("folder:" + folder);
            System.Diagnostics.Process.Start(folder);//"explorer.exe", 
        }
    }
#endif
}

