using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

internal static class BackupPacking
{
    internal static string Prepare(string dump, string id, Action<int> progress, Action<string> log,
                                   out DumpProof original)
    {
        original = null;
        string entryName = id + ".dmp";
        if (!Wire.IdPattern.IsMatch(id) || Path.GetFileName(dump) != entryName)
            throw new IOException("导出文件名与备份编号不一致");

        string archive = dump + "." + Guid.NewGuid().ToString("N") + ".tmp.7z";
        bool keepArchive = false;
        try
        {
            using (FileStream input = File.OpenRead(dump))
            {
                long length = input.Length;
                if (length <= 0 || length > Wire.MaxFile) throw new IOException("导出文件大小无效");
                log("正在计算原始导出文件的 SHA-256。");
                byte[] hash = HashDump(input, Stream.Null, length, progress);
                log("正在使用 7z 高压缩模式。");
                if (progress != null) progress(0);
                Run7z("a -t7z -m0=LZMA2 -mx=9 -md=32m -mmt=2", Quote(archive) + " " + Quote(dump),
                      delegate(Stream output) { output.CopyTo(Stream.Null); });
                long packedLength = new FileInfo(archive).Length;
                if (packedLength >= length)
                {
                    log("压缩未减少文件大小，发送原始导出文件。");
                    return dump;
                }
                original = new DumpProof(length, hash);
                log("压缩完成，发送文件大小为原文件的 " + (packedLength / (double)length).ToString("P1") + "。");
                keepArchive = true;
                return archive;
            }
        }
        finally { if (!keepArchive) TryDelete(archive); }
    }

    internal static void Verify(string archive, string id, DumpProof original, Action<int> progress)
    {
        Decode(archive, id, original, Stream.Null, progress);
    }

    internal static void Materialize(string downloaded, string dump, string id, DumpProof original,
                                     Action<int> progress, Action<string> log)
    {
        if (!Wire.IsCompressed(id))
        {
            File.Move(downloaded, dump);
            return;
        }
        log("正在解压并校验原始导出文件。");
        string temporary = dump + ".part_" + Guid.NewGuid().ToString("N");
        try
        {
            using (FileStream output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                Decode(downloaded, id, original, output, progress);
                output.Flush(true);
            }
            File.Move(temporary, dump);
            log("解压文件通过 SHA-256 校验。");
        }
        finally { TryDelete(temporary); }
    }

    private static void Decode(string archive, string id, DumpProof original, Stream output, Action<int> progress)
    {
        if (!Wire.IdPattern.IsMatch(id)) throw new IOException("备份编号无效");
        if (Wire.IsSevenZip(id))
        {
            if (original == null) throw new IOException("7z 备份缺少原始文件校验信息");
            string entryName = id.Substring(0, id.Length - Wire.SevenZipSuffix.Length) + ".dmp";
            Run7z("x -t7z -so -spd -ssc", Quote(archive) + " " + entryName,
                  delegate(Stream decoded) { CheckDump(decoded, output, original, progress); });
        }
        else if (Wire.IsLegacyZip(id))
        {
            using (FileStream input = File.OpenRead(archive))
            using (ZipArchive zip = new ZipArchive(input, ZipArchiveMode.Read))
            {
                ZipArchiveEntry entry = zip.GetEntry("backup.dmp");
                ZipArchiveEntry checksum = zip.GetEntry("sha256.txt");
                if (zip.Entries.Count != 2 || entry == null || checksum == null || checksum.Length < 64 ||
                    checksum.Length > 80 || entry.Length <= 0 || entry.Length > Wire.MaxFile)
                    throw new IOException("ZIP 备份格式无效");

                byte[] hash;
                using (Stream stream = checksum.Open())
                {
                    byte[] bytes = Wire.ReadExactly(stream, (int)checksum.Length);
                    if (stream.ReadByte() != -1) throw new IOException("ZIP 校验信息长度无效");
                    hash = Wire.ParseHash(Encoding.ASCII.GetString(bytes).Trim());
                }
                if (hash == null) throw new IOException("ZIP 校验信息无效");
                using (Stream decoded = entry.Open())
                    CheckDump(decoded, output, new DumpProof(entry.Length, hash), progress);
            }
        }
        else throw new IOException("压缩备份格式无效");
    }

    private static void CheckDump(Stream input, Stream output, DumpProof original, Action<int> progress)
    {
        byte[] actual = HashDump(input, output, original.Length, progress);
        if (!Wire.Equal(actual, original.Hash)) throw new IOException("原始导出文件的 SHA-256 校验失败");
    }

    private static byte[] HashDump(Stream input, Stream output, long length, Action<int> progress)
    {
        using (SHA256 sha = SHA256.Create())
        {
            byte[] buffer = new byte[131072];
            long processed = 0;
            int previous = -1;
            Stopwatch sinceReport = Stopwatch.StartNew();
            int count;
            while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                processed += count;
                if (processed > length) throw new IOException("解码数据超过记录的原始文件大小");
                sha.TransformBlock(buffer, 0, count, buffer, 0);
                output.Write(buffer, 0, count);
                int percent = (int)(processed * 100 / length);
                if (progress != null && (percent != previous || sinceReport.ElapsedMilliseconds >= 10000))
                {
                    progress(percent);
                    previous = percent;
                    sinceReport.Restart();
                }
            }
            if (processed != length) throw new IOException("文件大小与备份记录不符");
            sha.TransformFinalBlock(new byte[0], 0, 0);
            return sha.Hash;
        }
    }

    private static string Quote(string path) { return "\"" + Path.GetFullPath(path) + "\""; }

    private static void Run7z(string operation, string files, Action<Stream> readOutput)
    {
        string executable = Path.Combine(Path.GetTempPath(), "OracleBackup7z_" + Guid.NewGuid().ToString("N") + ".exe");
        bool created = false;
        try
        {
            using (Stream resource = typeof(BackupPacking).Assembly.GetManifestResourceStream("OracleBackup.7zr.exe"))
            {
                if (resource == null) throw new IOException("程序缺少 7z 压缩组件，请重新解压安装包");
                using (FileStream file = new FileStream(executable, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    created = true;
                    resource.CopyTo(file);
                }
            }
            ProcessStartInfo info = new ProcessStartInfo(executable,
                operation + " -y -bso0 -bse2 -bsp0 -sccUTF-8 -- " + files);
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardInput = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.StandardErrorEncoding = Encoding.UTF8;
            using (Process process = new Process())
            {
                StringBuilder errors = new StringBuilder();
                process.StartInfo = info;
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs entry)
                {
                    if (!String.IsNullOrWhiteSpace(entry.Data) && errors.Length < 2000)
                        errors.AppendLine(entry.Data.Substring(0, Math.Min(entry.Data.Length, 2000 - errors.Length)));
                };
                if (!process.Start()) throw new IOException("无法启动 7z 压缩组件");
                try
                {
                    process.BeginErrorReadLine();
                    process.StandardInput.Close();
                    readOutput(process.StandardOutput.BaseStream);
                    process.WaitForExit();
                    if (process.ExitCode != 0)
                        throw new IOException("7z 操作失败，返回代码 " + process.ExitCode + "。" + errors.ToString().Trim());
                }
                finally
                {
                    if (!process.HasExited)
                    {
                        try { process.Kill(); process.WaitForExit(2000); } catch (InvalidOperationException) { }
                    }
                }
            }
        }
        finally { if (created) TryDelete(executable); }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
