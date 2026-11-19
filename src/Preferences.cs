using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

internal static class Preferences
{
    private static readonly object LogGate = new object();
    internal static string Folder
    {
        get
        {
#if TEST_BUILD
            if (!String.IsNullOrEmpty(TestFolder)) return TestFolder;
#endif
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                "OracleBackupManager.Standard");
        }
    }
#if TEST_BUILD
    internal static string TestFolder;
#endif

    private static string PathFor(string name)
    {
        return Path.Combine(Folder, name + ".settings");
    }

    internal static Dictionary<string, string> Load(string name)
    {
        Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string file = PathFor(name);
            if (!File.Exists(file)) return result;
            foreach (string line in File.ReadAllLines(file, Encoding.UTF8))
            {
                int i = line.IndexOf('=');
                if (i > 0) result[line.Substring(0, i)] = line.Substring(i + 1);
            }
        }
        catch { }
        return result;
    }

    internal static void Save(string name, Dictionary<string, string> values)
    {
        string file = PathFor(name);
        Directory.CreateDirectory(Path.GetDirectoryName(file));
        List<string> lines = new List<string>();
        foreach (KeyValuePair<string, string> pair in values)
        {
            string value = pair.Value ?? "";
            if (value.IndexOfAny(new char[] { '\r', '\n' }) >= 0) continue;
            lines.Add(pair.Key + "=" + value);
        }
        File.WriteAllLines(file, lines.ToArray(), Encoding.UTF8);
    }

    internal static string Get(Dictionary<string, string> values, string key, string fallback)
    {
        string value;
        return values.TryGetValue(key, out value) ? value : fallback;
    }

    internal static void AppendLog(string name, string message)
    {
        try
        {
            lock (LogGate)
            {
                string folder = Path.GetDirectoryName(PathFor(name));
                Directory.CreateDirectory(folder);
                string file = Path.Combine(folder, name + ".log");
                string previous = file + ".previous";
                if (File.Exists(file) && new FileInfo(file).Length > 10 * 1024 * 1024)
                {
                    if (File.Exists(previous)) File.Delete(previous);
                    File.Move(file, previous);
                }
                File.AppendAllText(file, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                                   "  " + message + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch { }
    }
}
