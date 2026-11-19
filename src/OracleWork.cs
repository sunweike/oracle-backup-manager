using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

internal sealed class OracleOptions
{
    internal string Host, Service, User, Password, Schema, DirectoryObject, Staging, Bin;
    internal int Port;

    internal void Validate()
    {
        if (!Regex.IsMatch(Host ?? "", @"^[A-Za-z0-9.-]{1,253}$")) throw new Exception("数据库地址无效");
        if (Port < 1 || Port > 65535) throw new Exception("数据库端口无效");
        if (!Regex.IsMatch(Service ?? "", @"^[A-Za-z0-9._-]{1,128}$")) throw new Exception("数据库服务名无效");
        if (!Wire.SchemaPattern.IsMatch(User ?? "") || !Wire.SchemaPattern.IsMatch(Schema ?? "") ||
            !Wire.SchemaPattern.IsMatch(DirectoryObject ?? "")) throw new Exception("用户名、账号或目录对象无效");
        if (!Regex.IsMatch(Password ?? "", @"^[A-Za-z0-9_]{1,128}$"))
            throw new Exception("此版本数据库密码仅支持字母、数字和下划线");
        if (!String.IsNullOrWhiteSpace(Staging)) Staging = NormalizeLocalDirectory(Staging.Trim());
        else Staging = "";
        if (!String.IsNullOrWhiteSpace(Bin)) Bin = Path.GetFullPath(Bin.Trim());
    }

    internal static string NormalizeLocalDirectory(string value)
    {
        if (String.IsNullOrWhiteSpace(value) || value.Length < 3 ||
            !Char.IsLetter(value[0]) || value[1] != ':' ||
            (value[2] != '\\' && value[2] != '/'))
            throw new Exception("暂存目录必须是完整的本机盘符路径，例如 C:\\OracleBackupStaging");
        if (value.IndexOfAny(new char[] { '\r', '\n', '"' }) >= 0) throw new Exception("暂存目录无效");
        string full = Path.GetFullPath(value);
        string root = Path.GetPathRoot(full);
        if (String.IsNullOrEmpty(root) || root.Length != 3 || root[1] != ':')
            throw new Exception("暂存目录必须是本机盘符路径");
        return full.Length == root.Length ? root : full.TrimEnd('\\', '/');
    }
    internal string Connect
    {
        get { return User + "/" + Password + "@//" + Host + ":" + Port + "/" + Service; }
    }
    internal string Tool(string exe)
    {
        return String.IsNullOrWhiteSpace(Bin) ? exe : Path.Combine(Bin, exe);
    }
}

internal sealed class ExportedSet
{
    internal string Id, Dump, Log;
}

internal static class OracleWork
{
    private static string SecretFile(string contents)
    {
        string name = Path.Combine(Path.GetTempPath(), "oracle_backup_" + Guid.NewGuid().ToString("N") + ".par");
        FileSecurity acl = new FileSecurity();
        acl.SetAccessRuleProtection(true, false);
        SecurityIdentifier sid = WindowsIdentity.GetCurrent().User;
        acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        using (FileStream file = new FileStream(name, FileMode.CreateNew, FileSystemRights.Write,
                                               FileShare.None, 4096, FileOptions.None, acl))
        using (StreamWriter writer = new StreamWriter(file, Encoding.ASCII))
            writer.Write(contents);
        return name;
    }

    private static int RunTool(string executable, string arguments, string input, string password,
                               Action<string> logger, List<string> captured)
    {
        ProcessStartInfo info = new ProcessStartInfo(executable, arguments);
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.RedirectStandardInput = true;
        using (Process process = new Process())
        {
            process.StartInfo = info;
            object gate = new object();
            DataReceivedEventHandler read = delegate(object sender, DataReceivedEventArgs e)
            {
                if (e.Data == null) return;
                string safe = String.IsNullOrEmpty(password) ? e.Data : e.Data.Replace(password, "********");
                lock (gate)
                {
                    if (captured != null) captured.Add(e.Data);
                    if (logger != null) logger(safe);
                }
            };
            process.OutputDataReceived += read;
            process.ErrorDataReceived += read;
            if (!process.Start()) throw new Exception("无法启动 " + executable);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!String.IsNullOrEmpty(input)) process.StandardInput.Write(input);
            process.StandardInput.Close();
            process.WaitForExit();
            process.WaitForExit();
            return process.ExitCode;
        }
    }

    private static void RunPump(string tool, OracleOptions options, string parameters, Action<string> log)
    {
        string par = SecretFile("USERID=" + options.Connect + "\r\n" + parameters);
        try
        {
            bool fileError = false;
            int code = RunTool(options.Tool(tool), "parfile=\"" + par + "\"", null,
                               options.Password, delegate(string line)
                               {
                                   if (line.Contains("ORA-29283")) fileError = true;
                                   if (log != null) log(line);
                               }, null);
            if (code != 0)
            {
                if (fileError)
                    throw new Exception(tool + " 返回代码 " + code +
                        "（ORA-29283）。请检查数据库服务器上的目录是否存在，以及 Oracle 数据库 Windows 服务运行账号是否有读写权限。");
                throw new Exception(tool + " 返回错误或警告代码 " + code + "，请检查日志");
            }
        }
        finally { try { File.Delete(par); } catch { } }
    }

    private static bool SameHost(string databaseHost)
    {
        if (String.IsNullOrWhiteSpace(databaseHost)) return false;
        string actual = databaseHost.Trim().TrimEnd('.');
        string local = Environment.MachineName.Trim().TrimEnd('.');
        string actualShort = actual.Split('.')[0];
        string localShort = local.Split('.')[0];
        return String.Equals(actualShort, localShort, StringComparison.OrdinalIgnoreCase);
    }

    private static void CreateDirectoryObject(OracleOptions options, Action<string> log)
    {
        string path = options.Staging.Replace("'", "''");
        string input = "WHENEVER OSERROR EXIT FAILURE\r\nWHENEVER SQLERROR EXIT FAILURE\r\n" +
            "CONNECT " + options.Connect + "\r\nSET DEFINE OFF\r\n" +
            "CREATE DIRECTORY " + options.DirectoryObject + " AS '" + path + "';\r\n" +
            "SELECT 'OBM_DIRECTORY_CREATED' FROM DUAL;\r\nEXIT SUCCESS\r\n";
        List<string> output = new List<string>();
        log("目录对象不可见，尝试使用当前备份账号创建：" + options.DirectoryObject);
        int code = RunTool(options.Tool("sqlplus.exe"), "-L -S /nolog", input,
                           options.Password, log, output);
        bool created = output.Exists(delegate(string line) { return line.Trim() == "OBM_DIRECTORY_CREATED"; });
        if (code != 0 || !created)
        {
            log("请 DBA 确认对象不存在后创建：CREATE DIRECTORY " + options.DirectoryObject + " AS '" + path + "';");
            log("如对象已存在，请 DBA 确认路径正确并授权：GRANT READ, WRITE ON DIRECTORY " +
                options.DirectoryObject + " TO " + options.User + ";");
            throw new Exception("自动创建目录对象失败。该对象可能已存在但当前账号无权访问，也可能缺少 CREATE ANY DIRECTORY 权限；请数据库管理员核对上面的 Oracle 错误并创建或授权。未覆盖已有对象。");
        }
        log("已创建数据库目录对象，将重新查询并核对。");
    }

    internal static void CheckDirectory(OracleOptions options, Action<string> log)
    {
        CheckDirectory(options, log, true);
    }

    private static void CheckDirectory(OracleOptions options, Action<string> log, bool allowCreate)
    {
        options.Validate();
        const string marker = "OBM_DIRECTORY_PATH=";
        string input = "WHENEVER OSERROR EXIT FAILURE\r\nWHENEVER SQLERROR EXIT FAILURE\r\n" +
            "CONNECT " + options.Connect + "\r\n" +
            "SET PAGESIZE 0\r\nSET HEADING OFF\r\nSET FEEDBACK OFF\r\nSET VERIFY OFF\r\n" +
            "SET TRIMOUT ON\r\nSET LINESIZE 32767\r\nSET TAB OFF\r\n" +
            "SELECT 'OBM_DATABASE_HOST=' || SYS_CONTEXT('USERENV','SERVER_HOST') FROM DUAL;\r\n" +
            "SELECT '" + marker + "' || DIRECTORY_PATH FROM ALL_DIRECTORIES WHERE DIRECTORY_NAME='" +
            options.DirectoryObject + "';\r\n" +
            "SELECT 'OBM_DIRECTORY_CHECK_COMPLETE' FROM DUAL;\r\nEXIT SUCCESS\r\n";
        List<string> output = new List<string>();
        log("正在查询数据库目录对象：" + options.DirectoryObject);
        int code;
        try
        {
            code = RunTool(options.Tool("sqlplus.exe"), "-L -S /nolog", input,
                           options.Password, log, output);
        }
        catch (Win32Exception ex)
        {
            throw new Exception("无法启动 sqlplus.exe。请检查 Oracle bin 或系统 PATH，确保 SQL*Plus 已安装。", ex);
        }
        if (code != 0)
            throw new Exception("SQL*Plus 查询数据库目录失败，返回代码 " + code + "；请检查上面的数据库连接或权限错误。");
        string actual = null;
        string databaseHost = null;
        bool completed = false;
        foreach (string line in output)
        {
            string trimmed = line.Trim();
            if (trimmed == "OBM_DIRECTORY_CHECK_COMPLETE") completed = true;
            if (trimmed.StartsWith("OBM_DATABASE_HOST=", StringComparison.Ordinal))
                databaseHost = trimmed.Substring("OBM_DATABASE_HOST=".Length).Trim();
            if (trimmed.StartsWith(marker, StringComparison.Ordinal))
            {
                if (actual != null) throw new Exception("查询到多个数据库目录路径，已取消操作。");
                actual = trimmed.Substring(marker.Length).Trim();
            }
        }
        if (!completed)
            throw new Exception("SQL*Plus 未完整返回目录检查结果；请检查上面的 SQL*Plus 或数据库错误。");
        if (!String.IsNullOrEmpty(databaseHost))
            log("数据库实例所在主机：" + databaseHost + "；本客户端所在主机：" + Environment.MachineName);
        if (!SameHost(databaseHost))
            throw new Exception("数据库实例主机与客户端主机不一致或无法确认；请在对应数据库服务器本机运行客户端。数据库实例：" +
                (databaseHost ?? "未知") + "；客户端：" + Environment.MachineName);
        if (String.IsNullOrEmpty(actual))
        {
            if (!allowCreate)
                throw new Exception("创建后仍看不到目录对象 " + options.DirectoryObject +
                    "；请数据库管理员检查目录权限。");
            if (String.IsNullOrWhiteSpace(options.Staging))
                options.Staging = @"C:\OracleBackupStaging";
            options.Staging = OracleOptions.NormalizeLocalDirectory(options.Staging);
            Directory.CreateDirectory(options.Staging);
            CreateDirectoryObject(options, log);
            CheckDirectory(options, log, false);
            return;
        }
        log("数据库目录对象实际路径：" + actual);
        string normalized;
        try { normalized = OracleOptions.NormalizeLocalDirectory(actual); }
        catch
        {
            throw new Exception("数据库目录对象的路径不是可比较的 Windows 本机盘符路径：" + actual +
                "；请确认数据库运行在本机，且路径与本机暂存目录一致。");
        }
        if (String.IsNullOrWhiteSpace(options.Staging))
        {
            options.Staging = normalized;
            log("未指定本机暂存目录，自动使用数据库目录对象的现有路径。");
        }
        else if (!String.Equals(normalized, options.Staging, StringComparison.OrdinalIgnoreCase))
            throw new Exception("数据库目录对象与本机暂存目录不一致。数据库路径：" + actual +
                "；本机暂存目录：" + options.Staging + "。请核对后重试。");
        Directory.CreateDirectory(options.Staging);
        log("目录路径一致；此检查不能验证 Oracle 服务账号的文件权限。");
    }

    internal static ExportedSet Export(OracleOptions options, Action<string> log)
    {
        options.Validate();
        CheckDirectory(options, log);
        Directory.CreateDirectory(options.Staging);
        string id = options.Schema.Replace('$', '_').Replace('#', '_') + "_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + "_" +
                    Guid.NewGuid().ToString("N").Substring(0, 8).ToUpperInvariant();
        string dumpName = id + ".dmp";
        string logName = id + ".log";
        string dump = Path.Combine(options.Staging, dumpName);
        string exportLog = Path.Combine(options.Staging, logName);
        string p = "SCHEMAS=" + options.Schema + "\r\nDIRECTORY=" + options.DirectoryObject +
                   "\r\nDUMPFILE=" + dumpName + "\r\nLOGFILE=" + logName +
                   "\r\nFLASHBACK_TIME=SYSTIMESTAMP\r\n";
        log("开始导出。");
        RunPump("expdp.exe", options, p, log);
        if (!File.Exists(dump) || !File.Exists(exportLog) ||
            new FileInfo(dump).Length == 0 || new FileInfo(exportLog).Length == 0)
            throw new Exception("导出完成但暂存目录中没有完整文件，请检查 Oracle 目录对象映射");
        return new ExportedSet { Id = id, Dump = dump, Log = exportLog };
    }

    private static int ReadCount(List<string> lines, string marker)
    {
        foreach (string line in lines)
        {
            string s = line.Trim();
            int n;
            if (s.StartsWith(marker, StringComparison.Ordinal) &&
                Int32.TryParse(s.Substring(marker.Length).Trim(), out n)) return n;
        }
        throw new Exception("无法核实目标账号，已取消还原");
    }

    internal static void CheckEmptyTarget(OracleOptions admin, string target, Action<string> log)
    {
        admin.Validate();
        if (!Wire.SchemaPattern.IsMatch(target ?? "")) throw new Exception("目标账号名无效");
        string input = "WHENEVER OSERROR EXIT FAILURE\r\nWHENEVER SQLERROR EXIT FAILURE\r\n" +
            "CONNECT " + admin.Connect + "\r\n" +
            "SET PAGESIZE 0\r\nSET HEADING OFF\r\nSET FEEDBACK OFF\r\nSET VERIFY OFF\r\nSET TRIMOUT ON\r\n" +
            "SELECT 'USER_COUNT=' || COUNT(*) FROM DBA_USERS WHERE USERNAME='" + target + "';\r\n" +
            "SELECT 'OBJECT_COUNT=' || COUNT(*) FROM DBA_OBJECTS WHERE OWNER='" + target + "';\r\nEXIT SUCCESS\r\n";
        List<string> output = new List<string>();
        int code = RunTool(admin.Tool("sqlplus.exe"), "-L -S /nolog", input,
                           admin.Password, log, output);
        if (code != 0 || ReadCount(output, "USER_COUNT=") != 1)
            throw new Exception("目标账号不存在或管理员无权查询 DBA_USERS");
        if (ReadCount(output, "OBJECT_COUNT=") != 0)
            throw new Exception("目标账号不是空账号，已取消还原");
    }

    internal static string Restore(OracleOptions admin, string sourceSchema, string targetSchema,
                                   string dumpFile, Action<string> log)
    {
        admin.Validate();
        if (!Wire.SchemaPattern.IsMatch(sourceSchema ?? "") || !Wire.SchemaPattern.IsMatch(targetSchema ?? ""))
            throw new Exception("还原账号无效");
        string local = Path.GetFullPath(dumpFile);
        if (!String.Equals(Path.GetDirectoryName(local).TrimEnd('\\'), admin.Staging.TrimEnd('\\'),
                           StringComparison.OrdinalIgnoreCase))
            throw new Exception("还原文件必须位于 Oracle 目录对象对应的暂存目录");
        if (!File.Exists(local) || new FileInfo(local).Length == 0) throw new Exception("还原文件不存在");
        string restoreLog = "RESTORE_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + "_" +
                            Guid.NewGuid().ToString("N").Substring(0, 8).ToUpperInvariant() + ".log";
        string p = "DIRECTORY=" + admin.DirectoryObject + "\r\nDUMPFILE=" + Path.GetFileName(local) +
                   "\r\nLOGFILE=" + restoreLog + "\r\nSCHEMAS=" + sourceSchema +
                   "\r\nTABLE_EXISTS_ACTION=SKIP\r\n";
        if (sourceSchema != targetSchema) p += "REMAP_SCHEMA=" + sourceSchema + ":" + targetSchema + "\r\n";
        log("开始还原到已检查的空账号；完成后请检查日志并验证数据。");
        RunPump("impdp.exe", admin, p, log);
        string logPath = Path.Combine(admin.Staging, restoreLog);
        if (!File.Exists(logPath)) throw new Exception("还原命令结束，但未找到导入日志");
        return logPath;
    }
}
