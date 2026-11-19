using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

internal sealed class BackupTask
{
    internal const string DefaultDirectoryObject = "ORACLE_BACKUP_DIR";
    internal string Id, Name, Server, Host, Service, User, Password, Schema, DirectoryObject, Staging, Bin;
    internal int Port = 1521, Hours = 24;
    internal string ScheduleMode = "interval";
    internal int DailyMinute = 60;
    internal bool Scheduled, DeleteStaging, Compress = true;
    internal DateTime NextUtc = DateTime.MinValue, LastUtc = DateTime.MinValue;
    internal string LastStatus = "";

    internal BackupTask Clone() { return (BackupTask)MemberwiseClone(); }

    internal static BackupTask NewTask(string name)
    {
        string id = Guid.NewGuid().ToString("N").ToUpperInvariant();
        return new BackupTask {
            Id = id, Name = name, Host = "", Service = "",
            User = "", Password = "", Schema = "",
            DirectoryObject = DefaultDirectoryObject, Staging = "", Server = ""
        };
    }

    internal void ValidateStorage()
    {
        if (!Regex.IsMatch(Id ?? "", "^[A-F0-9]{32}$")) throw new Exception("备份任务编号无效");
        if (String.IsNullOrWhiteSpace(Name) || Name.Length > 80 || Name.IndexOfAny(new char[] { '\r', '\n' }) >= 0)
            throw new Exception("任务名称不能为空，且不能超过 80 个字符");
        if (Port < 1 || Port > 65535 || Hours < 1 || Hours > 720) throw new Exception("端口或备份间隔无效");
        if ((ScheduleMode != "interval" && ScheduleMode != "daily") || DailyMinute < 0 || DailyMinute >= 1440)
            throw new Exception("备份计划无效");
    }

    internal OracleOptions Options()
    {
        ValidateStorage();
        if (!Regex.IsMatch(Server ?? "", @"^[A-Za-z0-9.-]{1,253}$")) throw new Exception("请填写有效的接收端 IP 或主机名");
        OracleOptions options = new OracleOptions {
            Host = Host, Port = Port, Service = Service, User = User, Password = Password,
            Schema = Schema, DirectoryObject = DirectoryObject, Staging = Staging, Bin = Bin
        };
        options.Validate();
        return options;
    }
}

internal static class TaskPlanner
{
    internal static DateTime Following(BackupTask task, DateTime nowUtc)
    {
        if (task.ScheduleMode == "interval") return nowUtc.AddHours(task.Hours);
        DateTime local = nowUtc.ToLocalTime();
        DateTime candidate = DateTime.SpecifyKind(local.Date.AddMinutes(task.DailyMinute), DateTimeKind.Unspecified);
        if (candidate <= local) candidate = candidate.AddDays(1);
        while (TimeZoneInfo.Local.IsInvalidTime(candidate)) candidate = candidate.AddMinutes(1);
        return TimeZoneInfo.ConvertTimeToUtc(candidate, TimeZoneInfo.Local);
    }

    internal static BackupTask NextDue(IEnumerable<BackupTask> tasks, DateTime nowUtc)
    {
        BackupTask next = null;
        foreach (BackupTask task in tasks)
            if (task.Scheduled && task.NextUtc != DateTime.MinValue && task.NextUtc <= nowUtc &&
                (next == null || task.NextUtc < next.NextUtc)) next = task;
        return next;
    }

    internal static void UpdateSchedule(BackupTask changed, BackupTask previous, DateTime nowUtc)
    {
        if (!changed.Scheduled) changed.NextUtc = DateTime.MinValue;
        else if (previous == null || !previous.Scheduled || previous.Hours != changed.Hours ||
                 previous.ScheduleMode != changed.ScheduleMode || previous.DailyMinute != changed.DailyMinute ||
                 previous.NextUtc == DateTime.MinValue)
            changed.NextUtc = Following(changed, nowUtc);
        else changed.NextUtc = previous.NextUtc;
    }

    internal static void Completed(BackupTask task, DateTime nowUtc, bool success)
    {
        task.LastUtc = nowUtc;
        task.LastStatus = success ? "成功" : "失败";
        task.NextUtc = task.Scheduled ? Following(task, nowUtc) : DateTime.MinValue;
    }
}

internal sealed class ClientInstanceLock : IDisposable
{
    private readonly FileStream handle;
    internal ClientInstanceLock(string directory)
    {
        Directory.CreateDirectory(directory);
        handle = new FileStream(Path.Combine(directory, "client.lock"), FileMode.OpenOrCreate,
                                FileAccess.ReadWrite, FileShare.None);
    }
    public void Dispose() { handle.Dispose(); }
}

internal sealed class BackupTaskStore
{
    private readonly string file;
    private readonly Func<string, string> protectPassword, unprotectPassword;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OracleBackupManager.TaskPassword.v1");

    internal BackupTaskStore(string directory) : this(directory, Protect, Unprotect) { }
    internal BackupTaskStore(string directory, Func<string, string> protect, Func<string, string> unprotect)
    {
        file = Path.Combine(directory, "backup-tasks.xml");
        protectPassword = protect;
        unprotectPassword = unprotect;
    }
    internal static BackupTaskStore Default()
    {
        return new BackupTaskStore(Preferences.Folder);
    }
    internal string FileName { get { return file; } }

    private static string Protect(string value)
    {
        byte[] plain = Encoding.UTF8.GetBytes(value ?? "");
        return Convert.ToBase64String(ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
    }

    private static string Unprotect(string value)
    {
        byte[] plain = ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plain);
    }

    private static string Read(XmlElement element, string name)
    {
        XmlNode node = element.SelectSingleNode(name);
        if (node == null) throw new IOException("任务文件缺少字段：" + name);
        return node.InnerText;
    }
    private static int Number(XmlElement element, string name)
    {
        int value;
        if (!Int32.TryParse(Read(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            throw new IOException("任务文件字段无效：" + name);
        return value;
    }
    private static DateTime Time(XmlElement element, string name)
    {
        long ticks;
        if (!Int64.TryParse(Read(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks) ||
            ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
            throw new IOException("任务文件时间无效：" + name);
        return ticks == 0 ? DateTime.MinValue : new DateTime(ticks, DateTimeKind.Utc);
    }

    internal List<BackupTask> Load()
    {
        if (!File.Exists(file)) return null;
        XmlReaderSettings settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                                                             MaxCharactersInDocument = 4000000 };
        XmlDocument document = new XmlDocument();
        document.XmlResolver = null;
        using (XmlReader reader = XmlReader.Create(file, settings)) document.Load(reader);
        XmlElement root = document.DocumentElement;
        if (root == null || root.Name != "backupTasks" || root.GetAttribute("version") != "1")
            throw new IOException("任务文件格式不受支持");
        List<BackupTask> tasks = new List<BackupTask>();
        HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (XmlNode node in root.ChildNodes)
        {
            XmlElement e = node as XmlElement;
            if (e == null || e.Name != "task") continue;
            BackupTask task = new BackupTask {
                Id = Read(e, "id"), Name = Read(e, "name"), Server = Read(e, "server"),
                Host = Read(e, "host"), Port = Number(e, "port"), Service = Read(e, "service"),
                User = Read(e, "user"), Password = unprotectPassword(Read(e, "password")), Schema = Read(e, "schema"),
                DirectoryObject = Read(e, "directory"), Staging = Read(e, "staging"), Bin = Read(e, "bin"),
                Hours = Number(e, "hours"), Scheduled = Read(e, "scheduled") == "1",
                ScheduleMode = Read(e, "scheduleMode"), DailyMinute = Number(e, "dailyMinute"),
                DeleteStaging = Read(e, "deleteStaging") == "1", Compress = Read(e, "compress") == "1",
                NextUtc = Time(e, "nextUtc"), LastUtc = Time(e, "lastUtc"), LastStatus = Read(e, "lastStatus")
            };
            task.ValidateStorage();
            if (!ids.Add(task.Id) || tasks.Count >= 500) throw new IOException("任务数量或编号无效");
            tasks.Add(task);
        }
        return tasks;
    }

    private static void Write(XmlWriter writer, string name, string value)
    {
        writer.WriteElementString(name, value ?? "");
    }

    internal void Save(List<BackupTask> tasks)
    {
        if (tasks.Count > 500) throw new Exception("最多支持 500 个任务");
        string directory = Path.GetDirectoryName(file);
        Directory.CreateDirectory(directory);
        string temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                XmlWriterSettings settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true };
                using (XmlWriter writer = XmlWriter.Create(stream, settings))
                {
                    writer.WriteStartElement("backupTasks");
                    writer.WriteAttributeString("version", "1");
                    foreach (BackupTask task in tasks)
                    {
                        task.ValidateStorage();
                        writer.WriteStartElement("task");
                        Write(writer, "id", task.Id); Write(writer, "name", task.Name); Write(writer, "server", task.Server);
                        Write(writer, "host", task.Host); Write(writer, "port", task.Port.ToString(CultureInfo.InvariantCulture));
                        Write(writer, "service", task.Service); Write(writer, "user", task.User);
                        Write(writer, "password", protectPassword(task.Password)); Write(writer, "schema", task.Schema);
                        Write(writer, "directory", task.DirectoryObject); Write(writer, "staging", task.Staging);
                        Write(writer, "bin", task.Bin); Write(writer, "hours", task.Hours.ToString(CultureInfo.InvariantCulture));
                        Write(writer, "scheduleMode", task.ScheduleMode);
                        Write(writer, "dailyMinute", task.DailyMinute.ToString(CultureInfo.InvariantCulture));
                        Write(writer, "scheduled", task.Scheduled ? "1" : "0");
                        Write(writer, "deleteStaging", task.DeleteStaging ? "1" : "0");
                        Write(writer, "compress", task.Compress ? "1" : "0");
                        Write(writer, "nextUtc", task.NextUtc.Ticks.ToString(CultureInfo.InvariantCulture));
                        Write(writer, "lastUtc", task.LastUtc.Ticks.ToString(CultureInfo.InvariantCulture));
                        Write(writer, "lastStatus", task.LastStatus);
                        writer.WriteEndElement();
                    }
                    writer.WriteEndElement();
                }
            }
            if (File.Exists(file)) File.Replace(temporary, file, file + ".bak", true);
            else File.Move(temporary, file);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
    }
}
