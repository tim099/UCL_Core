using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace UCL.Core.CsvLib
{
    [UCL.Core.ATTR.EnableUCLEditor]
    public class UCL_CSVParser : MonoBehaviour {

        public string m_TextData = string.Empty;
        public string m_FolderPath = string.Empty;
        public string m_FileName = string.Empty;
        public string m_SaveName = string.Empty;
        public string m_Encoding = "big5";
        public string FilePath { get { return Path.Combine(m_FolderPath, m_FileName); } }
        public string SaveFilePath { get { return Path.Combine(m_FolderPath, m_SaveName); } }
        public CSVData m_CSVData = null;
#if UNITY_EDITOR
        [UCL.Core.ATTR.UCL_FunctionButton]
        public void ExploreFile() {
            var path = UCL.Core.FileLib.EditorLib.OpenAssetsFileExplorer(m_FolderPath);
            m_FolderPath = UCL.Core.FileLib.Lib.RemoveFolderPath(path, 1);
            m_FileName = path.Substring(m_FolderPath.Length + 1, path.Length - m_FolderPath.Length - 1);
        }
#endif
#if UNITY_STANDALONE_WIN
        [UCL.Core.ATTR.UCL_FunctionButton]
        public void OpenFile() {
            var path = FilePath;
            if(!File.Exists(path)) {
                Debug.LogError("OpenFile:" + path + " fail, file not exist!!");
                return;
            }
            UCL.Core.FileLib.WindowsLib.OpenAssetExplorer(path);
        }
#endif
        [UCL.Core.ATTR.UCL_FunctionButton]
        public void ReadFile() {
            var path = FilePath;
            if(!File.Exists(path)) {
                Debug.LogError("ReadFile:" + path + " fail, file not exist!!");
                return;
            }
            byte[] aBytes = File.ReadAllBytes(path);
            //int i = 0;
            //foreach(var e1 in Encoding.GetEncodings()) {
            //    if(i++ > 100) break;
            //    var aNewBytes = Encoding.Convert(e1.GetEncoding(), Encoding.UTF8, aBytes);
            //    m_TextData = Encoding.UTF8.GetString(aNewBytes);
            //    Debug.LogWarning("e1:" + e1.UCL_ToString() + ",m_TextData:" + m_TextData);
            //}
            if(!string.IsNullOrEmpty(m_Encoding)) {
                aBytes = Encoding.Convert(Encoding.GetEncoding(m_Encoding), Encoding.UTF8, aBytes);
            }
            
            m_TextData = Encoding.UTF8.GetString(aBytes);
            m_CSVData = new CSVData(m_TextData);
            Debug.LogWarning("m_CSVData:" + m_TextData);
        }
        public CSVData GetCSVData() {
            return m_CSVData;
        }
        //public ClassData CreateClassData() {
        //    return new ClassData(m_CSVData);
        //}
        //public void SaveClassToFile(ClassData iClassData) {
        //    File.WriteAllText(SaveFilePath + ".cs", iClassData.ConvertToString());
        //}
        //[UCL.Core.ATTR.UCL_FunctionButton]
        //public void WriteToClass() {
        //    ClassData aClassData = CreateClassData();
        //    Debug.LogError(aClassData.UCL_ToString());
        //    SaveClassToFile(aClassData);
        //    //File.WriteAllText(SaveFilePath + ".cs", aClassData.ConvertToString());
        //}
    }
}