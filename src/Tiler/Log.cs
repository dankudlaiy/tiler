using System;
using System.IO;

namespace Tiler;

internal static class Log
{
    const long MaxSize = 1024 * 1024;
    static readonly object Sync = new();

    public static string FilePath => Path.Combine(Settings.Folder, "tiler.log");

    /// <summary>Starts a fresh log once the old one grows past <see cref="MaxSize"/>.</summary>
    public static void Rotate()
    {
        try
        {
            var file = new FileInfo(FilePath);
            if (file.Exists && file.Length > MaxSize)
                file.Delete();
        }
        catch (IOException)
        {
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? e = null) => Write("ERROR", e == null ? message : $"{message}: {e}");

    static void Write(string level, string message)
    {
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(Settings.Folder);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
            }
        }
    }
}
