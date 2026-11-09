using System;
using System.IO;

namespace Lindo.Services
{
    internal static class Log
    {
        private static readonly object Gate = new object();

        public static string FilePath => Path.Combine(SystemInfo.DataFolder, "lindo.log");

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 2 * 1024 * 1024) fi.Delete(); // non occupare spazio inutile
                    File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine);
                }
            }
            catch { /* il log non deve mai bloccare l'app */ }
        }
    }
}
