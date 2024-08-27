using Cysharp.Threading.Tasks;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.Serialization.Formatters.Binary;
using UnityEngine;

namespace UCL.Core.FileLib
{
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
            var aRes = Resources.Load(LibName);
            string aPath = UCL.Core.EditorLib.AssetDatabaseMapper.GetAssetPath(aRes);

            return FileLib.Lib.RemoveFolderPath(aPath, 2);
        }
        public static string GetCoreFolderPath() {
            return UCL_CoreSetting.GetFolderPath();
        }
        public static void ExploreFile(string iTitle, string iFolder, string iExtension = "") {
            string path = UCL.Core.EditorLib.EditorUtilityMapper.OpenFilePanel(iTitle, iFolder, iExtension);

            if(!string.IsNullOrEmpty(path) && path != iFolder) {
                Application.OpenURL(path);
            }
        }
        public static void ExploreFolder(string folder) {
            string path = UCL.Core.EditorLib.EditorUtilityMapper.OpenFolderPanel("Open Folder", folder, string.Empty);

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
            string path = UCL.Core.EditorLib.EditorUtilityMapper.OpenFilePanel("Open Folder", assets_path, string.Empty);
            if(string.IsNullOrEmpty(path)) return iFilePath;

            return path.Replace(asset_root, "");
        }

        /// <summary>
        /// Open folder explorer under assets folder
        /// 在Assets資料夾中開啟資料夾瀏覽器
        /// </summary>
        /// <param name="iFolder"></param>
        /// <returns></returns>
        public static string OpenAssetsFolderExplorer(string iFolder) {
            string aAssetsRoot = FileLib.Lib.AssetsRoot;
            string aAssetsPath = aAssetsRoot + iFolder;
            string aPath = UCL.Core.EditorLib.EditorUtilityMapper.OpenFolderPanel("Open Folder", aAssetsPath, string.Empty);
            if(string.IsNullOrEmpty(aPath)) return iFolder;

            return aPath.Replace(aAssetsRoot, "");
        }
        /// <summary>
        /// Open folder explorer
        /// 開啟資料夾瀏覽器
        /// </summary>
        /// <param name="folder"></param>
        /// <returns></returns>
        public static string OpenFolderExplorer(string folder) {
            string path = UCL.Core.EditorLib.EditorUtilityMapper.OpenFolderPanel("Open Folder", folder, string.Empty);
            if(string.IsNullOrEmpty(path)) return folder;

            return path;
        }
        public static string SelectScript(MonoBehaviour iMonoBehaviour) {
            var aPath = GetScriptPath(iMonoBehaviour);
            UCL.Core.EditorLib.SelectionMapper.activeObject = UCL.Core.EditorLib.AssetDatabaseMapper.LoadMainAssetAtPath(aPath);
            return aPath;
        }
        public static string SelectScript(ScriptableObject iScriptableObject) {
            var path = GetScriptPath(iScriptableObject);
            UCL.Core.EditorLib.SelectionMapper.activeObject = UCL.Core.EditorLib.AssetDatabaseMapper.LoadMainAssetAtPath(path);
            return path;
        }
        public static string GetScriptPath(MonoBehaviour iMonoBehaviour) {
            return UCL.Core.EditorLib.AssetDatabaseMapper.GetAssetPath(UCL.Core.EditorLib.MonoScriptMapper.FromMonoBehaviour(iMonoBehaviour));
        }
        public static string GetScriptPath(ScriptableObject iScriptableObject) {
            return UCL.Core.EditorLib.AssetDatabaseMapper.GetAssetPath(UCL.Core.EditorLib.MonoScriptMapper.FromScriptableObject(iScriptableObject));
        }
    }
#endif
    static public class Lib{
        public static string AssetsRoot => Application.dataPath.Replace("Assets", string.Empty);
        public static string GetProjectPath() {
            return Application.dataPath.Replace("Assets", string.Empty);
        }

        /// <summary>
        /// Remove file name from path and return folder path
        /// </summary>
        /// <param name="iFilePath"></param>
        /// <returns></returns>
        public static string GetFolderPath(string iFilePath) {
            for(int i = iFilePath.Length - 1; i >= 0; i--) {
                var c = iFilePath[i];
                if(c == '/' || c == '\\') {
                    iFilePath = iFilePath.Substring(0, i);
                    break;
                }
            }
            return iFilePath;
        }
        /// <summary>
        /// Remove folder and file from path
        /// Example path is "root/folder/c.txt" and remove_count is 2, then return "root" 
        /// 根據設定數量移除路徑
        /// </summary>
        /// <param name="iPath"></param>
        /// <param name="iRemoveCount"></param>
        /// <returns></returns>
        public static string RemoveFolderPath(string iPath, int iRemoveCount = 1) {
            if(iRemoveCount <= 0) return iPath;

            int i = iPath.Length - 1;
            for(; i >= 0; i--) {
                var c = iPath[i];
                if(c == '/' || c == '\\') {
                    if(--iRemoveCount <= 0) break;
                }
            }
            if(i <= 0) return string.Empty;
            return iPath.Substring(0, i);
        }
        /// <summary>
        /// return filename from input path
        /// </summary>
        /// <param name="iPath"></param>
        /// <returns></returns>
        public static string GetFileName(string iPath) {
            for(int i = iPath.Length - 1; i >= 0; i--) {
                var c = iPath[i];
                if(c == '/' || c == '\\') {
                    return iPath.Substring(i + 1, iPath.Length - i - 1);
                }
            }
            return iPath;
        }
        /// <summary>
        /// return filename from input path
        /// </summary>
        /// <param name="iPath"></param>
        /// <param name="iIsRemoveExtension"></param>
        /// <returns></returns>
        public static string GetFileName(string iPath, bool iIsRemoveExtension)
        {
            int aLastIndex = iPath.Length - 1;
            if (iIsRemoveExtension)
            {
                for (int i = aLastIndex; i >= 0; i--)
                {
                    if (iPath[i] == '.')
                    {
                        aLastIndex = i - 1;
                        break;
                    }
                }
            }

            for (int i = aLastIndex; i >= 0; i--)
            {
                var c = iPath[i];
                if (c == '/' || c == '\\')
                {
                    return iPath.Substring(i + 1, aLastIndex - i);
                }
            }
            return iPath;
        }
        /// <summary>
        /// return FolderName from input path
        /// </summary>
        /// <param name="iPath"></param>
        /// <returns></returns>
        public static string GetFolderName(string iPath)
        {
            for (int i = iPath.Length - 1; i >= 0; i--)
            {
                var c = iPath[i];
                if (c == '/' || c == '\\')
                {
                    return iPath.Substring(i + 1, iPath.Length - i - 1);
                }
            }
            return iPath;
        }
        /// <summary>
        /// return RootFolder Name from input path
        /// (etc. Resources/Datas this will return Resources)
        /// </summary>
        /// <param name="iPath"></param>
        /// <returns></returns>
        public static string GetRootFolderName(string iPath)
        {
            if (iPath.Length <= 1)
            {
                return iPath;
            }
            for (int i = 1; i < iPath.Length ; i++)
            {
                char aChar = iPath[i];
                if (aChar == '/' || aChar == '\\')
                {
                    return iPath.Substring(0, i);
                }
            }
            return iPath;
        }
        /// <summary>
        /// return SubFolder Name from input path
        /// (etc. Resources/Datas this will return Datas)
        /// </summary>
        /// <param name="iPath"></param>
        /// <returns></returns>
        public static string GetSubFolderName(string iPath)
        {
            if (iPath.Length <= 1)
            {
                return string.Empty;
            }
            for (int i = 0; i < iPath.Length - 1; i++)
            {
                char aChar = iPath[i];
                if (aChar == '/' || aChar == '\\')
                {
                    return iPath.Substring(i + 1, iPath.Length - i - 1);
                }
            }
            return string.Empty;
        }
        /// <summary>
        /// return all folders name in path
        /// </summary>
        /// <param name="iPath"></param>
        /// <returns></returns>
        public static List<string> GetFoldersName(string iPath)
        {
            List<string> aFoldersName = new List<string>();
            int PrevAt = 0;
            for (int i = 0; i < iPath.Length; i++)
            {
                var c = iPath[i];
                if (c == '/' || c == '\\')
                {
                    string aFolderName = iPath.Substring(PrevAt, i - PrevAt);
                    aFoldersName.Add(aFolderName);
                    PrevAt = i + 1;
                }
            }
            return aFoldersName;
        }
        /// <summary>
        /// Split a path into two part
        /// Example path is "root/folder/c.txt" and split_at is 2, then return "root","folder/c.txt" 
        /// 根據指定的位置切分路徑
        /// </summary>
        /// <param name="iPath">input path</param>
        /// <param name="iSplitAt">split position, if negetive then reverse order</param>
        /// <returns></returns>
        public static System.Tuple<string,string> SplitPath(string iPath, int iSplitAt = 1) {
            if(iSplitAt == 0) return new System.Tuple<string, string>(iPath, string.Empty);
            if (iSplitAt > 0)
            {
                int i = iPath.Length - 1;
                for (; i >= 0; i--)
                {
                    var c = iPath[i];
                    if (c == '/' || c == '\\')
                    {
                        if (--iSplitAt <= 0) break;
                    }
                }
                if (i <= 0) return new System.Tuple<string, string>(string.Empty, iPath);
                return new System.Tuple<string, string>(iPath.Substring(0, i), iPath.Substring(i + 1, iPath.Length - i - 1));
            }
            else//iSplitAt < 0
            {
                iSplitAt = -iSplitAt;
                int i = 0;
                for (; i < iPath.Length; i++)
                {
                    var c = iPath[i];
                    if (c == '/' || c == '\\')
                    {
                        if (--iSplitAt <= 0) break;
                    }
                }
                if (i >= iPath.Length - 1) return new System.Tuple<string, string>(iPath, string.Empty);
                return new System.Tuple<string, string>(iPath.Substring(0, i), iPath.Substring(i + 1, iPath.Length - i - 1));
            }

        }
        public static void DeleteFile(string iPath, bool iIsDeleteMeta = true)
        {
            if (File.Exists(iPath)) File.Delete(iPath);
            if (iIsDeleteMeta)
            {
                string aMetaPath = iPath + ".meta";
                if (File.Exists(aMetaPath)) File.Delete(aMetaPath);
            }
        }
        public static void DeleteDirectory(string iPath,bool iIsDeleteMeta = true)
        {
            if (Directory.Exists(iPath))
            {
                Directory.Delete(iPath, true);
            }
            
            if (iIsDeleteMeta)
            {
                string aMetaPath = iPath + ".meta";
                if (File.Exists(aMetaPath)) File.Delete(aMetaPath);
            }
        }
        public static void MoveDirectory(string iOldDir, string iNewDir)
        {
            if (!Directory.Exists(iOldDir))
            {
                Debug.LogError("MoveDirectory !Directory.Exists("+iOldDir+")");
                return;
            }
            try
            {
                Directory.Move(iOldDir, iNewDir);
            }
            catch(System.Exception iE)
            {
                Debug.LogException(iE);
            }
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
        public static string GetFilesPath(bool iCreateIfNotExist = false) {
            string path = Path.Combine(Application.persistentDataPath, "files");
            if(iCreateIfNotExist) CreateDirectory(path);
            return path;
        }
        /// <summary>
        /// https://stackoverflow.com/questions/411592/how-do-i-save-a-stream-to-a-file-in-c
        /// </summary>
        /// <param name="stream"></param>
        /// <param name="destinationFile"></param>
        /// <param name="mode"></param>
        /// <param name="access"></param>
        /// <param name="share"></param>
        public static void WriteToFile(Stream stream, string destinationFile, FileMode mode = FileMode.OpenOrCreate, FileAccess access = FileAccess.ReadWrite, FileShare share = FileShare.ReadWrite)
        {
            //stream.Position = 0;
            using (var destinationFileStream = new FileStream(destinationFile, mode, access, share))
            {
                CopyStream(stream, destinationFileStream);
            }
        }
        /// <summary>
        /// Copies the contents of input to output. Doesn't close either stream.
        /// https://stackoverflow.com/questions/411592/how-do-i-save-a-stream-to-a-file-in-c/411605#411605
        /// </summary>
        public static void CopyStream(Stream input, Stream output, int bufferSize = 8)
        {
            byte[] buffer = new byte[bufferSize * 1024];
            int len;
            while ((len = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, len);
            }
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
        /// <param name="iPath">The relative or absolute path to the directory to search. This string is not case-sensitive.</param>
        /// <param name="iSearchPattern">The search string to match against the names of subdirectories in path.
        /// This parameter can contain a combination of valid literal and wildcard characters, but it doesn't support regular expressions.</param>
        /// <param name="iSearchOption">One of the enumeration values that specifies whether the search operation 
        /// should include all subdirectories or only the current directory.</param>
        /// <returns></returns>
        public static string[] GetDirectories(string iPath,
            string iSearchPattern = "*",
            SearchOption iSearchOption = SearchOption.AllDirectories, bool iRemoveRootPath = false) {
            if (!Directory.Exists(iPath)) return new string[0];
            var aDirs = Directory.GetDirectories(iPath, iSearchPattern, iSearchOption);
            if (iRemoveRootPath)
            {
                for (int i = 0; i < aDirs.Length; i++)
                {
                    aDirs[i] = GetFileName(aDirs[i]);
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
        public static string[] GetFiles(string iPath, string iSearchPattern = "*", 
            SearchOption iSearchOption = SearchOption.TopDirectoryOnly) {
            if (!Directory.Exists(iPath))
            {
                return new string[0];
            }
            return Directory.GetFiles(iPath, iSearchPattern, iSearchOption);
        }
        public static string[] GetFilesName(string iPath, string iSearchPattern = "*",
            SearchOption iSearchOption = SearchOption.TopDirectoryOnly, bool iIsRemoveExtension = false) {
            if (!Directory.Exists(iPath))
            {
                return new string[0];
            }
            var aFiles = Directory.GetFiles(iPath, iSearchPattern, iSearchOption);
            for(int i = 0; i < aFiles.Length; i++)
            {
                aFiles[i] = GetFileName(aFiles[i], iIsRemoveExtension);
            }
            return aFiles;
        }
        public static string ConvertToAssetsPath(string iPath)
        {
            return "Assets" + Path.GetFullPath(iPath).Replace(Path.GetFullPath(Application.dataPath), string.Empty);
        }
        /// <summary>
        /// Create the Directory if not exist
        /// </summary>
        /// <param name="iPath"></param>
        public static void CreateDirectory(string iPath) {
            if(string.IsNullOrEmpty(iPath)) return;
            if(iPath[iPath.Length - 1] == '/') {
                iPath.Remove(iPath.Length - 1);
            }
            if(!Directory.Exists(iPath)) {
                Directory.CreateDirectory(iPath);
            }
        }
        public static void WriteAllText(string iPath, string iContents)
        {
            string aFolderPath = GetFolderPath(iPath);
            if (!Directory.Exists(aFolderPath))
            {
                CreateDirectory(aFolderPath);
            }
            File.WriteAllText(iPath, iContents);
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
            DirectoryInfo aSourceInfo = new DirectoryInfo(source);
            DirectoryInfo aTargetInfo = new DirectoryInfo(target);
            foreach(FileInfo aFileInfo in aSourceInfo.GetFiles()) {
                try
                {
                    aFileInfo.CopyTo(Path.Combine(target.ToString(), aFileInfo.Name), true);
                }
                catch (System.Exception e)
                {
                    Debug.LogException(e);
                }
            }
            foreach(DirectoryInfo diSourceSubDir in aSourceInfo.GetDirectories()) {
                try
                {
                    DirectoryInfo aNextTargetSubDir = aTargetInfo.CreateSubdirectory(diSourceSubDir.Name);
                    CopyDirectory(diSourceSubDir.FullName, aNextTargetSubDir.FullName);
                }
                catch (System.Exception e)
                {
                    Debug.LogException(e);
                }
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

    static public class ZipLib
    {
        public static void UnzipFromBytes(byte[] iBytes, string iTargetPath)
        {
            using (Stream aStream = new MemoryStream(iBytes))
            {
                //using(GZipStream aGZipStream = new GZipStream(aStream, System.IO.Compression.CompressionLevel.NoCompression, true))
                {
                    using (ZipArchive aZip = new ZipArchive(aStream, ZipArchiveMode.Read, true))
                    {
                        foreach (var aEntry in aZip.Entries)
                        {
                            try
                            {
                                string aCompleteFileName = Path.Combine(iTargetPath, aEntry.FullName);
                                string aDirectory = Path.GetDirectoryName(aCompleteFileName);
                                Directory.CreateDirectory(aDirectory);

                                try
                                {
                                    using (Stream aEntryStream = aEntry.Open())
                                    {
                                        if (File.Exists(aCompleteFileName))
                                        {
                                            File.Delete(aCompleteFileName);
                                        }
                                        UCL.Core.FileLib.Lib.WriteToFile(aEntryStream, aCompleteFileName, FileMode.Create, FileAccess.Write, FileShare.Write);
                                    }
                                }
                                catch (System.Exception ex)
                                {
                                    Debug.LogException(ex);
                                    Debug.LogError($"ZipLib.UnzipFromBytes aDirectory:{aDirectory},Exception:{ex}");
                                }
                            }
                            catch (System.Exception ex)
                            {
                                Debug.LogException(ex);
                                Debug.LogError($"ZipLib.UnzipFromBytes Entry:{aEntry.FullName},Exception:{ex}");
                            }


                        }
                    }
                }
            }

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
        /// <param name="iFolder">target folder within Assets folder</param>
        static public void OpenAssetExplorer(string iFolder) {
            string aAssetRoot = Application.dataPath.Replace("Assets", string.Empty);
            var aPath = aAssetRoot + iFolder;
            try
            {
                //Debug.LogWarning("asset_root + folder:" + asset_root + folder);
                System.Diagnostics.Process.Start(aPath);//"explorer.exe", 
            }
            catch(System.Exception iE)
            {
                Debug.LogException(iE);
                Debug.LogError("OpenAssetExplorer Fail iFolder:" + iFolder);
            }

        }
        /// <summary>
        /// Open explorer of target folder
        /// </summary>
        /// <param name="iFolder"></param>
        static public void OpenExplorer(string iFolder) {
            //Debug.LogWarning("folder:" + folder);
            try
            {
                System.Diagnostics.Process.Start(iFolder);//"explorer.exe", 
            }
            catch(System.Exception iE)
            {
                Debug.LogException(iE);
                Debug.LogError("OpenExplorer iFolder:" + iFolder + ",Exception:" + iE);
            }

        }
    }
#endif
}

