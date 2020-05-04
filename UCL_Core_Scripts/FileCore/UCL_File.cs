using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace UCL.Core.FileLib {
    static public class Lib{
        public static string GetFilesPath(bool create_if_not_exist = false) {
            string path = Application.persistentDataPath + "/files";
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

