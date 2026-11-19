using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

internal sealed class BackupRecord
{
    public string Id;
    public string Schema;
    public DateTime Utc;
    public long DumpSize;
#if SERVER_BUILD
    public byte[] DumpHash;
    public byte[] LogHash;
    public DumpProof Original;
#endif
}

internal sealed class DumpProof
{
    internal readonly long Length;
    internal readonly byte[] Hash;

    internal DumpProof(long length, byte[] hash)
    {
        if (length <= 0 || length > Wire.MaxFile || hash == null || hash.Length != 32)
            throw new IOException("原始导出文件的大小或校验值无效");
        Length = length;
        Hash = hash;
    }

    internal void Write(BinaryWriter writer)
    {
        writer.Write(Length);
        writer.Write(Hash);
    }

    internal static DumpProof Read(BinaryReader reader)
    {
        return new DumpProof(reader.ReadInt64(), Wire.ReadExactly(reader.BaseStream, 32));
    }
}

internal static class Wire
{
    internal const int DefaultPort = 17999;
    internal const long MaxFile = 4L * 1024 * 1024 * 1024 * 1024;
    internal const string SevenZipSuffix = "_7Z";
    internal const byte UploadVerified = 4, ListAll = 5, DownloadSevenZip = 3, Verifying = 2;
    internal static readonly byte[] Magic = Encoding.ASCII.GetBytes("OBK2026!");
    internal static readonly Regex IdPattern = new Regex(@"^[A-Z0-9_]{8,90}\z");
    internal static readonly Regex SchemaPattern = new Regex(@"^[A-Z][A-Z0-9_$#]{0,29}$");

    internal static bool IsSevenZip(string id) { return id.EndsWith(SevenZipSuffix, StringComparison.Ordinal); }
    internal static bool IsLegacyZip(string id) { return id.EndsWith("_Z1", StringComparison.Ordinal); }
    internal static bool IsCompressed(string id) { return IsSevenZip(id) || IsLegacyZip(id); }
    internal static string FormatName(string id) { return IsSevenZip(id) ? "7z" : IsLegacyZip(id) ? "ZIP" : "DMP"; }
    internal static string StoredDumpName(string id) { return IsSevenZip(id) ? "backup.7z" : "backup.dmp"; }

    internal static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return size.ToString(unit == 0 ? "0" : "0.0") + " " + units[unit];
    }

    internal static bool Equal(byte[] a, byte[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        int difference = 0;
        for (int i = 0; i < a.Length; i++) difference |= a[i] ^ b[i];
        return difference == 0;
    }

    internal static byte[] ReadExactly(Stream s, int count)
    {
        byte[] data = new byte[count];
        int got = 0;
        while (got < count)
        {
            int n = s.Read(data, got, count - got);
            if (n <= 0) throw new IOException("连接中断");
            got += n;
        }
        return data;
    }

    internal static void WriteText(BinaryWriter w, string value, int max)
    {
        byte[] b = Encoding.ASCII.GetBytes(value);
        if (b.Length < 1 || b.Length > max) throw new IOException("字段长度无效");
        w.Write((ushort)b.Length);
        w.Write(b);
    }

    internal static string ReadText(BinaryReader r, int max)
    {
        int n = r.ReadUInt16();
        if (n < 1 || n > max) throw new IOException("字段长度无效");
        return Encoding.ASCII.GetString(ReadExactly(r.BaseStream, n));
    }

    internal static void SendFile(BinaryWriter w, BinaryReader r, string path, Action<int> progress)
    {
        using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (SHA256 hash = SHA256.Create())
        {
            long length = file.Length;
            if (length <= 0 || length > MaxFile) throw new IOException("文件为空或超过大小限制");
            w.Write(length);
            w.Flush();
            byte[] buffer = new byte[131072];
            long sent = 0;
            int previous = -1;
            while (sent < length)
            {
                int n = file.Read(buffer, 0, (int)Math.Min(buffer.Length, length - sent));
                if (n <= 0) throw new IOException("读取本地文件失败");
                hash.TransformBlock(buffer, 0, n, buffer, 0);
                w.Write(buffer, 0, n);
                sent += n;
                int p = (int)(sent * 100 / length);
                if (p != previous && progress != null) { previous = p; progress(p); }
            }
            hash.TransformFinalBlock(new byte[0], 0, 0);
            w.Write(hash.Hash);
            w.Flush();
            if (r.ReadByte() != 1) throw new IOException("接收端未通过文件校验");
        }
    }

    internal static byte[] ReceiveFile(BinaryReader r, BinaryWriter w, string path, Action<int> progress,
                                       byte[] expectedHash)
    {
        long length = r.ReadInt64();
        if (length <= 0 || length > MaxFile) throw new IOException("远端文件大小无效");
        byte[] finalHash;
        using (FileStream file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (SHA256 hash = SHA256.Create())
        {
            byte[] buffer = new byte[131072];
            long received = 0;
            int previous = -1;
            while (received < length)
            {
                int n = r.BaseStream.Read(buffer, 0, (int)Math.Min(buffer.Length, length - received));
                if (n <= 0) throw new IOException("文件传输中断");
                hash.TransformBlock(buffer, 0, n, buffer, 0);
                file.Write(buffer, 0, n);
                received += n;
                int p = (int)(received * 100 / length);
                if (p != previous && progress != null) { previous = p; progress(p); }
            }
            hash.TransformFinalBlock(new byte[0], 0, 0);
            byte[] remoteHash = ReadExactly(r.BaseStream, 32);
            if (!Equal(hash.Hash, remoteHash)) throw new IOException("SHA-256 校验失败");
            if (expectedHash != null && !Equal(hash.Hash, expectedHash))
                throw new IOException("文件与备份接收时的 SHA-256 不一致");
            finalHash = hash.Hash;
            file.Flush(true);
        }
        w.Write((byte)1);
        w.Flush();
        return finalHash;
    }

    internal static string Hex(byte[] data)
    {
        return BitConverter.ToString(data).Replace("-", "");
    }

    internal static byte[] ParseHash(string value)
    {
        if (value == null || !Regex.IsMatch(value, "^[0-9A-F]{64}$")) return null;
        byte[] result = new byte[32];
        for (int i = 0; i < result.Length; i++)
            result[i] = Convert.ToByte(value.Substring(i * 2, 2), 16);
        return result;
    }
}

#if SERVER_BUILD
internal sealed class BackupReceiver
{
    private readonly object gate = new object();
    private readonly string root;
    private readonly HashSet<string> allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string> log;
    private ReceiverIdentity identity;
    private TcpListener listener;
    private volatile bool running;
    private TcpClient active;
    private readonly SemaphoreSlim busy = new SemaphoreSlim(1, 1);
    internal int Port { get; private set; }

    internal BackupReceiver(string rootPath, string allowedIps, Action<string> logger)
    {
        if (String.IsNullOrWhiteSpace(rootPath) || !Path.IsPathRooted(rootPath))
            throw new ArgumentException("请填写完整的备份根目录");
        root = Path.GetFullPath(Path.Combine(rootPath, "OracleBackupSets"));
        foreach (string part in allowedIps.Split(','))
        {
            IPAddress ip;
            if (!IPAddress.TryParse(part.Trim(), out ip) || ip.AddressFamily != AddressFamily.InterNetwork)
                throw new ArgumentException("允许来源必须是 IPv4 地址，多个地址用英文逗号分隔");
            allowed.Add(ip.ToString());
        }
        if (allowed.Count == 0) throw new ArgumentException("至少需要一个允许的来源 IP");
        log = logger;
    }

    internal void Start(int port)
    {
        Directory.CreateDirectory(root);
        identity = ReceiverIdentity.LoadOrCreate();
        try
        {
            listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
        }
        catch
        {
            identity.Dispose();
            throw;
        }
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        running = true;
        Thread t = new Thread(AcceptLoop);
        t.IsBackground = true;
        t.Start();
        Log("接收端已启动，端口 " + Port + "，目录 " + root);
    }

    internal void Stop()
    {
        running = false;
        try { if (listener != null) listener.Stop(); } catch { }
        try { TcpClient current = active; if (current != null) current.Close(); } catch { }
        Log("接收端已停止");
    }

    private void Log(string text) { if (log != null) log(text); }

    private void AcceptLoop()
    {
        try { AcceptConnections(); }
        finally
        {
            busy.Wait();
            identity.Dispose();
        }
    }

    private void AcceptConnections()
    {
        while (running)
        {
            TcpClient client = null;
            try
            {
                client = listener.AcceptTcpClient();
                string ip = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
                if (!allowed.Contains(ip))
                {
                    Log("拒绝未列入允许名单的来源 " + ip);
                    client.Close();
                    continue;
                }
                if (!busy.Wait(2000))
                {
                    Log("已有任务进行中，拒绝新连接");
                    client.Close();
                    continue;
                }
                TcpClient accepted = client;
                try
                {
                    Thread t = new Thread(delegate() { Handle(accepted, ip); });
                    t.IsBackground = true;
                    t.Start();
                }
                catch { busy.Release(); throw; }
            }
            catch (Exception ex)
            {
                if (client != null) try { client.Close(); } catch { }
                if (running) Log("接收连接失败：" + ex.Message);
            }
        }
    }

    private void Handle(TcpClient client, string ip)
    {
        try
        {
            active = client;
            if (!running) { client.Close(); return; }
            client.ReceiveTimeout = 300000;
            client.SendTimeout = 300000;
            using (client)
            using (SslStream tls = new SslStream(client.GetStream(), false))
            {
                tls.AuthenticateAsServer(identity.Certificate, false, SslProtocols.Tls12, false);
                if (!tls.IsEncrypted || tls.CipherStrength < 128)
                    throw new AuthenticationException("TLS 加密强度不足");
                tls.ReadTimeout = 300000;
                tls.WriteTimeout = 300000;
                using (BinaryReader reader = new BinaryReader(tls))
                using (BinaryWriter writer = new BinaryWriter(tls))
                {
                    byte[] magic = Wire.ReadExactly(tls, Wire.Magic.Length);
                    byte[] token = Wire.ReadExactly(tls, 32);
                    bool authorized = Wire.Equal(magic, Wire.Magic)
                        && Wire.Equal(token, identity.Keys.Token);
                    writer.Write((byte)(authorized ? 1 : 0));
                    writer.Flush();
                    if (!authorized) { Log("拒绝身份验证失败的连接：" + ip); return; }
                    byte command = reader.ReadByte();
                    if (command == 1 || command == Wire.UploadVerified)
                        Upload(reader, writer, ip, command == Wire.UploadVerified);
                    else if (command == 2 || command == Wire.ListAll)
                        List(writer, command == Wire.ListAll);
                    else if (command == 3) Download(reader, writer, ip);
                    else throw new IOException("不支持的请求");
                }
            }
        }
        catch (Exception ex) { if (running) Log("连接或传输失败：" + ex.Message); }
        finally
        {
            if (Object.ReferenceEquals(active, client)) active = null;
            busy.Release();
        }
    }

    private void Upload(BinaryReader reader, BinaryWriter writer, string ip, bool verified)
    {
        string id = Wire.ReadText(reader, 90);
        string schema = Wire.ReadText(reader, 30);
        if (!Wire.IdPattern.IsMatch(id) || !Wire.SchemaPattern.IsMatch(schema))
            throw new IOException("备份编号或用户名无效");
        if (verified != Wire.IsSevenZip(id)) throw new IOException("备份格式与传输命令不符");
        DumpProof original = verified ? DumpProof.Read(reader) : null;
        string temporary = Path.Combine(root, ".incoming_" + Guid.NewGuid().ToString("N"));
        string final = Path.Combine(root, id);
        lock (gate) { if (Directory.Exists(final)) throw new IOException("备份编号已存在"); }
        Directory.CreateDirectory(temporary);
        try
        {
            writer.Write((byte)1);
            writer.Flush();
            string storedDump = Path.Combine(temporary, Wire.StoredDumpName(id));
            byte[] dumpHash = Wire.ReceiveFile(reader, writer, storedDump, null, null);
            byte[] logHash = Wire.ReceiveFile(reader, writer, Path.Combine(temporary, "export.log"), null, null);
            try
            {
                long length = new FileInfo(storedDump).Length;
                if (verified)
                {
                    if (length >= original.Length) throw new IOException("7z 大小与原始文件记录不符");
                    Log("正在校验 7z 备份 " + id);
                    Action<int> report = delegate(int percent)
                    {
                        writer.Write(Wire.Verifying);
                        writer.Write((byte)percent);
                        writer.Flush();
                    };
                    report(0);
                    BackupPacking.Verify(storedDump, id, original, report);
                }
                string manifest = "version=" + (verified ? "2" : "1") + "\r\nid=" + id + "\r\nschema=" + schema +
                    "\r\nutc_ticks=" + DateTime.UtcNow.Ticks + "\r\ndump_bytes=" + length +
                    "\r\ndump_sha256=" + Wire.Hex(dumpHash) +
                    "\r\nlog_sha256=" + Wire.Hex(logHash) + "\r\n";
                if (verified)
                    manifest += "original_bytes=" + original.Length + "\r\noriginal_sha256=" + Wire.Hex(original.Hash) + "\r\n";
                using (FileStream file = new FileStream(Path.Combine(temporary, "manifest.txt"), FileMode.CreateNew))
                {
                    byte[] data = Encoding.ASCII.GetBytes(manifest);
                    file.Write(data, 0, data.Length);
                    file.Flush(true);
                }
                lock (gate) { Directory.Move(temporary, final); }
            }
            catch
            {
                if (verified) try { writer.Write((byte)0); writer.Flush(); } catch (IOException) { }
                throw;
            }
            writer.Write((byte)1);
            writer.Flush();
            Log("已接收并校验备份 " + id + "，来源 " + ip);
        }
        finally { SafeDeleteSet(temporary, Wire.StoredDumpName(id)); }
    }

    private void List(BinaryWriter writer, bool allFormats)
    {
        List<BackupRecord> records = GetRecords();
        if (!allFormats) records.RemoveAll(delegate(BackupRecord record) { return Wire.IsSevenZip(record.Id); });
        writer.Write(records.Count);
        foreach (BackupRecord item in records)
        {
            Wire.WriteText(writer, item.Id, 90);
            Wire.WriteText(writer, item.Schema, 30);
            writer.Write(item.Utc.Ticks);
            writer.Write(item.DumpSize);
        }
        writer.Flush();
    }

    private void Download(BinaryReader r, BinaryWriter w, string ip)
    {
        string id = Wire.ReadText(r, 90);
        byte kind = r.ReadByte();
        if (!Wire.IdPattern.IsMatch(id) ||
            (kind != 2 && kind != (Wire.IsSevenZip(id) ? Wire.DownloadSevenZip : 1)))
            throw new IOException("备份编号无效");
        lock (gate)
        {
            BackupRecord record = ReadRecord(Path.Combine(root, id));
            if (record == null) throw new IOException("备份不存在或尚未完成");
            string path = Path.Combine(root, id, kind == 2 ? "export.log" : Wire.StoredDumpName(id));
            w.Write((byte)1);
            w.Write(kind == 2 ? record.LogHash : record.DumpHash);
            if (kind == Wire.DownloadSevenZip) record.Original.Write(w);
            w.Flush();
            Wire.SendFile(w, r, path, null);
        }
        Log("已发送备份 " + id + "，接收方 " + ip);
    }

    internal List<BackupRecord> GetRecords()
    {
        List<BackupRecord> records = new List<BackupRecord>();
        lock (gate)
        {
            if (!Directory.Exists(root)) return records;
            foreach (string dir in Directory.GetDirectories(root))
            {
                BackupRecord item = ReadRecord(dir);
                if (item != null) records.Add(item);
            }
        }
        records.Sort(delegate(BackupRecord a, BackupRecord b) { return b.Utc.CompareTo(a.Utc); });
        if (records.Count > 10000) records.RemoveRange(10000, records.Count - 10000);
        return records;
    }

    private static BackupRecord ReadRecord(string dir)
    {
        try
        {
            string id = Path.GetFileName(dir);
            if (!Wire.IdPattern.IsMatch(id) || (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                return null;
            string manifestPath = Path.Combine(dir, "manifest.txt");
            bool verified = Wire.IsSevenZip(id);
            string dumpPath = Path.Combine(dir, Wire.StoredDumpName(id));
            string logPath = Path.Combine(dir, "export.log");
            if (!File.Exists(manifestPath) || !File.Exists(dumpPath) || !File.Exists(logPath)) return null;
            foreach (string file in new string[] { manifestPath, dumpPath, logPath })
            {
                if ((File.GetAttributes(file) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    return null;
            }
            if (new FileInfo(manifestPath).Length > 4096 || new FileInfo(logPath).Length <= 0 ||
                new FileInfo(logPath).Length > Wire.MaxFile) return null;
            Dictionary<string, string> values = new Dictionary<string, string>();
            foreach (string line in File.ReadAllLines(manifestPath))
            {
                int index = line.IndexOf('=');
                if (index > 0) values[line.Substring(0, index)] = line.Substring(index + 1);
            }
            long ticks, size;
            if (!values.ContainsKey("version") || values["version"] != (verified ? "2" : "1") ||
                !values.ContainsKey("id") || values["id"] != id ||
                !values.ContainsKey("schema") || !Wire.SchemaPattern.IsMatch(values["schema"]) ||
                !values.ContainsKey("utc_ticks") || !Int64.TryParse(values["utc_ticks"], out ticks) ||
                !values.ContainsKey("dump_bytes") || !Int64.TryParse(values["dump_bytes"], out size) ||
                !values.ContainsKey("dump_sha256") || Wire.ParseHash(values["dump_sha256"]) == null ||
                !values.ContainsKey("log_sha256") || Wire.ParseHash(values["log_sha256"]) == null ||
                size <= 0 || size > Wire.MaxFile || new FileInfo(dumpPath).Length != size) return null;
            DumpProof original = null;
            if (verified)
            {
                string originalLength, originalHash;
                long length;
                if (!values.TryGetValue("original_bytes", out originalLength) ||
                    !Int64.TryParse(originalLength, out length) || length <= size ||
                    !values.TryGetValue("original_sha256", out originalHash)) return null;
                original = new DumpProof(length, Wire.ParseHash(originalHash));
            }
            return new BackupRecord { Id = id, Schema = values["schema"],
                Utc = new DateTime(ticks, DateTimeKind.Utc), DumpSize = size, Original = original,
                DumpHash = Wire.ParseHash(values["dump_sha256"]), LogHash = Wire.ParseHash(values["log_sha256"]) };
        }
        catch { return null; }
    }

    internal int CleanOlderThan(int days)
    {
        if (days < 1 || days > 3650) throw new ArgumentException("保留天数必须在 1 到 3650 之间");
        int count = 0;
        lock (gate)
        {
            foreach (string dir in Directory.GetDirectories(root))
            {
                BackupRecord r = ReadRecord(dir);
                if (r == null || r.Utc >= DateTime.UtcNow.AddDays(-days)) continue;
                if (SafeDeleteSet(dir, Wire.StoredDumpName(r.Id))) { count++; Log("已清理到期备份 " + r.Id); }
            }
        }
        return count;
    }

    private static bool SafeDeleteSet(string dir, string dumpName)
    {
        try
        {
            string[] entries = Directory.GetFileSystemEntries(dir);
            foreach (string item in entries)
            {
                string name = Path.GetFileName(item);
                if (name != dumpName && name != "export.log" && name != "manifest.txt") return false;
                if ((File.GetAttributes(item) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    return false;
            }
            foreach (string item in entries) File.Delete(item);
            Directory.Delete(dir, false);
            return true;
        }
        catch { return false; }
    }
}
#endif

#if CLIENT_BUILD
internal static class BackupNetworkClient
{
    private static void WithConnection(string host, int port, Action<BinaryReader, BinaryWriter> action)
    {
        PairingKeys keys = PairingStore.GetClient(host);
        using (TcpClient tcp = new TcpClient())
        {
            IAsyncResult pending = tcp.BeginConnect(host, port, null, null);
            using (WaitHandle completed = pending.AsyncWaitHandle)
            {
                if (!completed.WaitOne(TimeSpan.FromSeconds(15))) throw new IOException("连接接受端超时");
                tcp.EndConnect(pending);
            }
            tcp.ReceiveTimeout = 300000;
            tcp.SendTimeout = 300000;
            using (SslStream tls = new SslStream(tcp.GetStream(), false,
                delegate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors errors)
                {
                    return keys.Trusts(certificate);
                }))
            {
                tls.AuthenticateAsClient(host, null, SslProtocols.Tls12, false);
                if (!tls.IsEncrypted || tls.CipherStrength < 128)
                    throw new AuthenticationException("TLS 加密强度不足");
                tls.ReadTimeout = 300000;
                tls.WriteTimeout = 300000;
                using (BinaryReader r = new BinaryReader(tls))
                using (BinaryWriter w = new BinaryWriter(tls))
                {
                    w.Write(Wire.Magic);
                    w.Write(keys.Token);
                    w.Flush();
                    if (r.ReadByte() != 1) throw new IOException("接收端拒绝身份验证");
                    action(r, w);
                }
            }
        }
    }

    internal static void Upload(string host, int port, string id, string schema, string dump,
                                string log, Action<int> progress, DumpProof original = null,
                                Action<string> status = null)
    {
        if (Wire.IsSevenZip(id) != (original != null)) throw new IOException("备份校验信息与格式不符");
        WithConnection(host, port, delegate(BinaryReader reader, BinaryWriter writer)
        {
            writer.Write(original != null ? Wire.UploadVerified : (byte)1);
            Wire.WriteText(writer, id, 90);
            Wire.WriteText(writer, schema, 30);
            if (original != null) original.Write(writer);
            writer.Flush();
            if (reader.ReadByte() != 1) throw new IOException("接受端拒绝新备份");
            Wire.SendFile(writer, reader, dump, progress);
            Wire.SendFile(writer, reader, log, null);
            if (original != null && status != null) status("已发送，等待接受端解码并核对原始文件。");
            byte result = reader.ReadByte();
            while (original != null && result == Wire.Verifying)
            {
                byte percent = reader.ReadByte();
                if (progress != null) progress(percent);
                result = reader.ReadByte();
            }
            if (result != 1) throw new IOException("接受端校验或保存失败，请查看接受端日志");
        });
    }

    internal static List<BackupRecord> List(string host, int port)
    {
        List<BackupRecord> list = new List<BackupRecord>();
        WithConnection(host, port, delegate(BinaryReader r, BinaryWriter w)
        {
            w.Write(Wire.ListAll);
            w.Flush();
            int count = r.ReadInt32();
            if (count < 0 || count > 10000) throw new IOException("接收端列表无效");
            for (int i = 0; i < count; i++)
            {
                string id = Wire.ReadText(r, 90);
                string schema = Wire.ReadText(r, 30);
                long ticks = r.ReadInt64();
                long size = r.ReadInt64();
                if (!Wire.IdPattern.IsMatch(id) || !Wire.SchemaPattern.IsMatch(schema) ||
                    size <= 0 || size > Wire.MaxFile) throw new IOException("备份记录无效");
                list.Add(new BackupRecord { Id = id, Schema = schema,
                    Utc = new DateTime(ticks, DateTimeKind.Utc), DumpSize = size });
            }
        });
        return list;
    }

    internal static DumpProof Download(string host, int port, string id, byte kind, string destination,
                                       Action<int> progress)
    {
        if (kind == 1 && Wire.IsSevenZip(id)) kind = Wire.DownloadSevenZip;
        DumpProof original = null;
        string partial = destination + ".part_" + Guid.NewGuid().ToString("N");
        try
        {
            WithConnection(host, port, delegate(BinaryReader r, BinaryWriter w)
            {
                w.Write((byte)3);
                Wire.WriteText(w, id, 90);
                w.Write(kind);
                w.Flush();
                if (r.ReadByte() != 1) throw new IOException("备份不存在");
                byte[] expectedHash = Wire.ReadExactly(r.BaseStream, 32);
                if (kind == Wire.DownloadSevenZip) original = DumpProof.Read(r);
                Wire.ReceiveFile(r, w, partial, progress, expectedHash);
            });
            File.Move(partial, destination);
            return original;
        }
        finally { try { if (File.Exists(partial)) File.Delete(partial); } catch { } }
    }
}
#endif
