using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using UnityEngine;

namespace UCL.Core.FileLib {
#if UNITY_EDITOR
    static public class EditorLib {
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

        public static void WriteBinaryToFile<T>(string path, T target, FileMode fileMode = FileMode.Create) {
            using(Stream stream = File.Open(path, fileMode)) {
                var formatter = new System.Runtime.Serialization.Formatters.Binary.BinaryFormatter();
                formatter.Serialize(stream, target);
            }
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
}

