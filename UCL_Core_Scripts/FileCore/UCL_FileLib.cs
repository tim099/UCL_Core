using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using UnityEngine;

namespace UCL.Core.FileLib {
#if UNITY_EDITOR
    static public class EditorLib {
        public static string GetLibFolderPath(string LibName) {
            var res = Resources.Load(LibName);// + ".txt"
            string path = UnityEditor.AssetDatabase.GetAssetPath(res);

            return FileLib.Lib.RemoveFolderPath(path, 2);
        }
        public static string GetCoreFolderPath() {
            return UCL_CoreSetting.GetFolderPath();
        }
        public static void ExploreFile(string folder) {
            string path = UnityEditor.EditorUtility.OpenFilePanel("Open LogFile", folder, "");

            if(!string.IsNullOrEmpty(path) && path != folder) {
                Application.OpenURL(path);
            }
        }
        public static void ExploreFolder(string folder) {
            string path = UnityEditor.EditorUtility.OpenFolderPanel("Open Folder", folder, "");

            if(!string.IsNullOrEmpty(path) && path != folder) {
                Application.OpenURL(path);
            }
        }
        public static string OpenAssetsFolderPanel(string folder) {
            string asset_root = Application.dataPath.Replace("Assets", "");
            string assets_path = asset_root + folder;
            string path = UnityEditor.EditorUtility.OpenFolderPanel("Open Folder", assets_path, "");
            if(string.IsNullOrEmpty(path)) return folder;

            return path.Replace(asset_root, "");
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
            return Application.dataPath.Replace("Assets", "");
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
        /// </summary>
        /// <param name="path"></param>
        /// <param name="remove_count"></param>
        /// <returns></returns>
        public static string RemoveFolderPath(string path,int remove_count = 1) {
            if(remove_count <= 0) return path;

            int i = path.Length - 1;
            for(; i >= 0; i--) {
                var c = path[i];
                if(c == '/' || c == '\\') {

                    //path = path.Substring(0, i);
                    if(--remove_count <= 0) break;
                }
            }
            if(i <= 0) return "";
            return path.Substring(0, i);
        }
        public static void WriteBinaryToFile<T>(string path, T target, FileMode fileMode = FileMode.Create) {
            using(Stream stream = File.Open(path, fileMode)) {
                var formatter = new System.Runtime.Serialization.Formatters.Binary.BinaryFormatter();
                formatter.Serialize(stream, target);
            }
        }
        public static string RemoveFileExtension(string path,char separator = '.') {
            if(path.Length <= 1) return path;//No FileExtension
            int i = path.Length - 1;
            for(; i >= 0; i--) {
                if(path[i] == separator) break;
            }
            if(i == 0) return path;//No FileExtension
            return path.Substring(0, i);
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
        public static void CreateDirectory(string path) {
            if(string.IsNullOrEmpty(path)) return;
            if(path[path.Length - 1] == '/') {
                path.Remove(path.Length - 1);
            }
            if(!Directory.Exists(path)) {
                Directory.CreateDirectory(path);
            }
        }
        public static void WriteToFile(string data,string path) {
            using(var writer = OpenWriteStream(path)) {
                writer.WriteLine(data);
                writer.Close();
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
}

