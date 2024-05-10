
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEngine;


namespace UCL.Core
{
    public static partial class UCL_ZipFile
    {
        //public static ZipArchive CreateZipArchive(byte[] iBytes)
        //{
        //    using (Stream aStream = new MemoryStream(iBytes))
        //    {
        //        return new ZipArchive(aStream);
        //    }
        //}
        /// <summary>
        /// https://forum.unity.com/threads/crash-while-writing-zip-file-on-android.391667/
        /// Stripping Level in PlayerSetting for Android, set it to DISABLE
        /// </summary>
        /// <param name="iBytes"></param>
        /// <param name="iTargetPath"></param>
        /// <param name="iOverwriteFiles"></param>
        public static void ExtractToDirectory(byte[] iBytes, string iTargetPath, bool iOverwriteFiles = true)
        {
            using (Stream aStream = new MemoryStream(iBytes))
            {
                using (ZipArchive aZip = new ZipArchive(aStream, ZipArchiveMode.Read))
                {
                    //Directory.CreateDirectory(iTargetPath);
                    aZip.ExtractToDirectory(iTargetPath, iOverwriteFiles);
                }
            }
        }
    }
}

public static partial class UCL_ZipFileExtension
{
    public static string ReadAllTextFromEntry(this ZipArchive iZip, string iEntryName)
    {
        ZipArchiveEntry aEntry = iZip.GetEntry(iEntryName);
        if (aEntry != null)
        {
            using (Stream aStream = aEntry.Open())
            {
                using (StreamReader aReader = new StreamReader(aStream))
                {
                    return aReader.ReadToEnd();
                }
            }
        }
        return string.Empty;
    }
}
