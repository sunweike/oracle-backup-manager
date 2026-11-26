using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;

internal static class Harness
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    private static Stream ConnectRaw(TcpClient tcp, PairingKeys keys)
    {
        SslStream tls = new SslStream(tcp.GetStream(), false, delegate(object sender,
            X509Certificate certificate, X509Chain chain, SslPolicyErrors errors) { return keys.Trusts(certificate); });
        tls.AuthenticateAsClient("localhost", null, SslProtocols.Tls12, false);
        return tls;
    }

    private static byte[] FileHash(string path)
    {
        using (SHA256 sha = SHA256.Create())
        using (FileStream file = File.OpenRead(path))
            return sha.ComputeHash(file);
    }

    private static void BuildLegacyZip(string archive, string dump, string hash)
    {
        using (FileStream file = new FileStream(archive, FileMode.CreateNew))
        using (ZipArchive zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            using (Stream entry = zip.CreateEntry("backup.dmp", CompressionLevel.Optimal).Open())
            using (FileStream input = File.OpenRead(dump))
                input.CopyTo(entry);
            using (StreamWriter writer = new StreamWriter(zip.CreateEntry("sha256.txt").Open(), Encoding.ASCII))
                writer.WriteLine(hash);
        }
    }

    private static void SetAge(string setDirectory, int days)
    {
        string manifest = Path.Combine(setDirectory, "manifest.txt");
        string text = File.ReadAllText(manifest);
        int start = text.IndexOf("utc_ticks=") + "utc_ticks=".Length;
        int end = text.IndexOf('\r', start);
        File.WriteAllText(manifest, text.Substring(0, start) + DateTime.UtcNow.AddDays(-days).Ticks +
            text.Substring(end), Encoding.ASCII);
    }

    private static int Main(string[] args)
    {
        try { Run(args); return 0; }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL " + ex.GetType().FullName);
            try { Console.WriteLine(ex.Message); } catch { Console.WriteLine("Message unavailable"); }
            try { Console.WriteLine(ex.StackTrace); } catch { }
            return 1;
        }
    }
    private static void Run(string[] args)
    {
        string root = Path.GetFullPath(args[0]);
        string mockBin = Path.GetFullPath(args[1]);
        string temp = Path.Combine(root, Guid.NewGuid().ToString("N"));
        string staging = Path.Combine(temp, "staging");
        string dest = Path.Combine(temp, "receiver");
        string pumpMarker = Path.Combine(temp, "pump-started.txt");
        string createdMarker = Path.Combine(temp, "directory-created.txt");
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(dest);
        Preferences.TestFolder = Path.Combine(temp, "runtime-settings");
        Environment.SetEnvironmentVariable("TEMP", temp);
        Environment.SetEnvironmentVariable("MOCK_STAGING", staging);
        Environment.SetEnvironmentVariable("MOCK_PUMP_MARKER", pumpMarker);
        Environment.SetEnvironmentVariable("MOCK_CREATED_FLAG", createdMarker);
        List<string> logs = new List<string>();
        OracleOptions config = new OracleOptions {
            Host = "127.0.0.1", Port = 1521, Service = "TEST_ORCL",
            User = "DEMO_SCHEMA", Password = "TEST_SECRET", Schema = "DEMO_SCHEMA",
            DirectoryObject = "ORACLE_BACKUP_DIR", Staging = staging, Bin = mockBin
        };
        foreach (string mode in new string[] { "missing", "mismatch", "sqlerror", "incomplete", "remote" })
        {
            Environment.SetEnvironmentVariable("MOCK_DIRECTORY_MODE", mode);
            string problem = null;
            try { OracleWork.Export(config, delegate(string s) { logs.Add(s); }); }
            catch (Exception ex) { problem = ex.Message; }
            Check(!String.IsNullOrEmpty(problem), "directory precheck accepted " + mode);
            Check(!File.Exists(pumpMarker), "expdp started after " + mode + " precheck failed");
            if (mode == "mismatch") Check(problem.Contains("不一致"), "mismatch message was not helpful");
            if (mode == "missing") Check(problem.Contains("CREATE ANY DIRECTORY"), "missing-object message was not helpful");
            if (mode == "remote") Check(problem.Contains("主机不一致"), "remote-host message was not helpful");
        }
        Environment.SetEnvironmentVariable("MOCK_DIRECTORY_MODE", "match");
        string unavailableBin = Path.Combine(temp, "without-sqlplus");
        Directory.CreateDirectory(unavailableBin);
        config.Bin = unavailableBin;
        string missingSqlPlus = null;
        try { OracleWork.Export(config, delegate(string s) { logs.Add(s); }); }
        catch (Exception ex) { missingSqlPlus = ex.Message; }
        Check(missingSqlPlus != null && missingSqlPlus.Contains("sqlplus.exe"), "missing SQL*Plus not explained");
        Check(!File.Exists(pumpMarker), "expdp started without SQL*Plus");
        config.Bin = mockBin;
        Check(OracleOptions.NormalizeLocalDirectory(@"C:\") == @"C:\", "drive root changed into a relative path");
        Console.WriteLine("PASS missing, mismatched and failed directory checks block export; missing SQL*Plus explained");

        foreach (string invalidPassword in new string[] { "", "demo\nsecond line", "demo\r", "demo\t", "demo\"", "demo\u5bc6" })
        {
            config.Password = invalidPassword;
            bool invalidPasswordRejected = false;
            try { config.Validate(); }
            catch (Exception ex) { invalidPasswordRejected = ex.Message.Contains("密码"); }
            Check(invalidPasswordRejected, "unsupported password accepted");
        }
        const string specialPassword = " Test@/&!#%,=;')\\ ";
        config.Password = specialPassword;
        Environment.SetEnvironmentVariable("MOCK_SPECIAL_PASSWORD", specialPassword);
        config.Staging = "";
        OracleWork.CheckDirectory(config, delegate(string s) { logs.Add(s); });
        Check(config.Staging.Equals(staging, StringComparison.OrdinalIgnoreCase), "existing staging path not adopted");
        Environment.SetEnvironmentVariable("MOCK_DIRECTORY_MODE", "auto");
        OracleWork.CheckDirectory(config, delegate(string s) { logs.Add(s); });
        Check(File.Exists(createdMarker), "missing directory object was not created");
        Console.WriteLine("PASS automatic staging discovery and non-replacing directory creation");

        Environment.SetEnvironmentVariable("MOCK_DIRECTORY_MODE", "case_trailing");
        ExportedSet set = OracleWork.Export(config, delegate(string s) { logs.Add(s); });
        Check(File.Exists(set.Dump) && File.Exists(set.Log), "mock export did not create both files");
        Check(File.Exists(pumpMarker), "matching directory did not reach expdp");
        Check(logs.Contains("Mock SQL*Plus emitted ********") && logs.Contains("Mock Data Pump emitted ********") &&
              logs.TrueForAll(delegate(string s) { return !s.Contains("TEST_SECRET") && !s.Contains(specialPassword); }),
              "Oracle tool output did not redact passwords");
        Check(Directory.GetFiles(temp, "oracle_backup_*.par").Length == 0, "temporary parameter file was not deleted");
        Console.WriteLine("PASS special-character login for mocked SQL*Plus and export, redaction, parameter-file cleanup");
        Environment.SetEnvironmentVariable("MOCK_DIRECTORY_MODE", "match");
        Environment.SetEnvironmentVariable("MOCK_PUMP_ERROR", "file");
        string fileProblem = null;
        try { OracleWork.Export(config, delegate(string s) { logs.Add(s); }); }
        catch (Exception ex) { fileProblem = ex.Message; }
        Environment.SetEnvironmentVariable("MOCK_PUMP_ERROR", null);
        Check(fileProblem != null && fileProblem.Contains("ORA-29283") && fileProblem.Contains("服务"),
              "ORA-29283 diagnosis was not shown");
        Check(Directory.GetFiles(temp, "oracle_backup_*.par").Length == 0, "failed export leaked parameter file");
        Console.WriteLine("PASS Oracle file-operation failure explains service-account permissions");

        BackupTask firstTask = BackupTask.NewTask("task one");
        BackupTask secondTask = BackupTask.NewTask("task two");
        firstTask.Password = "TASK_ONE_SECRET"; secondTask.Password = "TASK_TWO_SECRET";
        firstTask.Server = secondTask.Server = "127.0.0.1";
        secondTask.Compression = BackupCompression.None;
        firstTask.Scheduled = secondTask.Scheduled = true;
        firstTask.NextUtc = DateTime.UtcNow.AddMinutes(-10);
        secondTask.NextUtc = DateTime.UtcNow.AddMinutes(-1);
        BackupTaskStore taskStore = new BackupTaskStore(Path.Combine(temp, "task-settings"));
        taskStore.Save(new List<BackupTask> { firstTask, secondTask });
        string savedSettings = File.ReadAllText(taskStore.FileName);
        // Simulate a pre-level settings file with the old compression checkbox.
        File.WriteAllText(taskStore.FileName, savedSettings.Replace("<compressionLevel>3</compressionLevel>", "")
                                                          .Replace("<compressionLevel>0</compressionLevel>", ""));
        List<BackupTask> loaded = taskStore.Load();
        Check(loaded[0].Compression == BackupCompression.Maximum && loaded[1].Compression == BackupCompression.None,
              "legacy compression settings did not preserve previous behavior");
        Check(loaded.Count == 2 && loaded[0].Password == "TASK_ONE_SECRET" && loaded[1].Password == "TASK_TWO_SECRET",
              "multiple tasks or encrypted passwords did not persist");
        string storedSettings = File.ReadAllText(taskStore.FileName);
        Check(!storedSettings.Contains("TASK_ONE_SECRET") && !storedSettings.Contains("TASK_TWO_SECRET"),
              "task password stored as plaintext");
        Check(TaskPlanner.NextDue(loaded, DateTime.UtcNow).Id == firstTask.Id, "overdue tasks not ordered");
        TaskPlanner.Completed(loaded[0], DateTime.UtcNow, true);
        Check(TaskPlanner.NextDue(loaded, DateTime.UtcNow).Id == secondTask.Id, "completed task was not rescheduled");
        loaded[1].ScheduleMode = "daily";
        loaded[0].Compression = BackupCompression.Balanced;
        DateTime dailyNext = TaskPlanner.Following(loaded[1], DateTime.UtcNow);
        Check(dailyNext > DateTime.UtcNow && dailyNext < DateTime.UtcNow.AddHours(26), "daily schedule invalid");
        taskStore.Save(loaded);
        loaded = taskStore.Load();
        Check(loaded[1].ScheduleMode == "daily", "updated schedule did not persist");
        Check(loaded[0].Compression == BackupCompression.Balanced && loaded[1].Compression == BackupCompression.None,
              "per-job compression levels did not persist");
        string lockFolder = Path.Combine(temp, "lock-test");
        using (ClientInstanceLock firstLock = new ClientInstanceLock(lockFolder))
        {
            bool duplicateBlocked = false;
            try { using (ClientInstanceLock secondLock = new ClientInstanceLock(lockFolder)) { } }
            catch (IOException) { duplicateBlocked = true; }
            Check(duplicateBlocked, "second client instance was allowed");
        }
        using (ClientInstanceLock reopened = new ClientInstanceLock(lockFolder)) { }
        Console.WriteLine("PASS multi-task scheduling, compression migration, restart persistence and Windows DPAPI round trip");
        Console.WriteLine("PASS concurrent clients using the same Windows profile are blocked");

        string packingRoot = Path.Combine(temp, "压缩 文件");
        Directory.CreateDirectory(packingRoot);
        string packingId = "DEMO_SCHEMA_TEST_COMPLETE";
        string compressedId = packingId + Wire.SevenZipSuffix;
        string largeDump = Path.Combine(packingRoot, packingId + ".dmp");
        byte[] repeated = new byte[2 * 1024 * 1024];
        for (int i = 0; i < repeated.Length; i++) repeated[i] = (byte)('A' + i % 13);
        File.WriteAllBytes(largeDump, repeated);
        DumpProof original;
        foreach (BackupCompression level in new[] { BackupCompression.Fast, BackupCompression.Maximum })
        {
            DumpProof proof;
            string packed = BackupPacking.Prepare(largeDump, packingId, level, null, delegate(string s) { }, out proof);
            Check(proof != null && new FileInfo(packed).Length < repeated.Length, "7z level did not produce a smaller file");
            BackupPacking.Verify(packed, compressedId, proof, null);
            File.Delete(packed);
        }
        string lastStage = "";
        int stagePercent = -1;
        string archive = BackupPacking.Prepare(largeDump, packingId, BackupCompression.Balanced,
            delegate(string stage, int percent) { lastStage = stage; stagePercent = percent; },
            delegate(string s) { }, out original);
        Check(lastStage == "7z 压缩" && stagePercent == 100, "compression progress did not report completion");
        byte[] archiveBytes = File.ReadAllBytes(archive);
        Check(original != null && original.Length == repeated.Length && archiveBytes.Length < repeated.Length &&
              archiveBytes[0] == 0x37 && archiveBytes[1] == 0x7a, "expected 7z archive");
        string unpacked = Path.Combine(temp, "unpacked.dmp");
        BackupPacking.Materialize(archive, unpacked, compressedId, original, null, delegate(string s) { });
        Check(Wire.Equal(FileHash(unpacked), FileHash(largeDump)), "decompressed backup differs");
        string existingTarget = Path.Combine(temp, "existing.dmp");
        File.WriteAllText(existingTarget, "preserve");
        try { BackupPacking.Materialize(archive, existingTarget, compressedId, original, null, delegate(string s) { }); }
        catch (IOException) { }
        Check(File.ReadAllText(existingTarget) == "preserve", "decompression damaged an existing file");
        DumpProof wrongProof = new DumpProof(original.Length, new byte[32]);
        string badOutput = Path.Combine(temp, "bad.dmp");
        bool badRejected = false;
        try { BackupPacking.Materialize(archive, badOutput, compressedId, wrongProof, null, delegate(string s) { }); }
        catch (IOException) { badRejected = true; }
        Check(badRejected && !File.Exists(badOutput) && Directory.GetFiles(temp, "bad.dmp.part_*").Length == 0,
              "bad uncompressed checksum accepted or left a restorable file");

        string randomId = "DEMO_SCHEMA_TEST_RANDOM";
        string incompressible = Path.Combine(temp, randomId + ".dmp");
        byte[] noise = new byte[262144];
        using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
            rng.GetBytes(noise);
        File.WriteAllBytes(incompressible, noise);
        DumpProof fallbackProof;
        string fallback = BackupPacking.Prepare(incompressible, randomId, BackupCompression.Fast,
            null, delegate(string s) { }, out fallbackProof);
        Check(fallbackProof == null && fallback == incompressible, "ineffective compression did not retain original");
        Check(Directory.GetFiles(temp, "OracleBackup7z_*.exe").Length == 0, "7z executable was left in TEMP");

        string legacyZip = Path.Combine(temp, "old-zip");
        string legacyId = "DEMO_SCHEMA_TEST_LEGACY_Z1";
        BuildLegacyZip(legacyZip, largeDump, Wire.Hex(original.Hash));
        string badZip = Path.Combine(temp, "bad-zip");
        BuildLegacyZip(badZip, largeDump, new String('0', 64));
        badRejected = false;
        try { BackupPacking.Materialize(badZip, badOutput, legacyId, null, null, delegate(string s) { }); }
        catch (IOException) { badRejected = true; }
        Check(badRejected && !File.Exists(badOutput), "legacy ZIP accepted a bad SHA-256");
        string oldDownload = Path.Combine(temp, "old-download");
        string oldDump = Path.Combine(temp, "old-backup.dmp");
        File.WriteAllText(oldDownload, "legacy uncompressed backup");
        BackupPacking.Materialize(oldDownload, oldDump, "DEMO_SCHEMA_TEST_LEGACY_DMP", null, null, delegate(string s) { });
        Check(File.ReadAllText(oldDump) == "legacy uncompressed backup" && !File.Exists(oldDownload),
              "legacy uncompressed backup was not preserved");
        Console.WriteLine("PASS 7z round trip, raw fallback, failed-check cleanup and legacy ZIP verification");

        BackupTask blank = BackupTask.NewTask("Blank task");
        Check(blank.Host == "" && blank.Server == "" && blank.Service == "" && blank.User == "" &&
              blank.Schema == "" && blank.Password == "", "new task has deployment-specific defaults");
        PairingKeys keys;
        string pairPath = Path.Combine(temp, "test.obm-pair");
        using (ReceiverIdentity identity = ReceiverIdentity.LoadOrCreate())
        {
            Check(identity.Certificate.HasPrivateKey && identity.Certificate.PublicKey.Key.KeySize == 2048 &&
                  identity.Certificate.SignatureAlgorithm.Value == "1.2.840.113549.1.1.11",
                  "receiver certificate does not have the expected RSA key and SHA-256 signature");
            identity.Keys.Export(pairPath);
            keys = PairingKeys.Import(pairPath);
            Check(Wire.Equal(keys.Token, identity.Keys.Token) && keys.Trusts(identity.Certificate),
                  "exported pairing does not match the receiver identity");
            byte[] altered = identity.Certificate.RawData;
            altered[altered.Length - 1] ^= 1;
            X509Certificate2 wrong = new X509Certificate2(altered);
            try { Check(!keys.Trusts(wrong), "modified certificate was trusted"); }
            finally { wrong.Reset(); }
        }
        File.Delete(pairPath);
        using (ReceiverIdentity reopenedIdentity = ReceiverIdentity.LoadOrCreate())
            Check(keys.Trusts(reopenedIdentity.Certificate) && Wire.Equal(keys.Token, reopenedIdentity.Keys.Token),
                  "receiver identity did not persist");
        bool missingPair = false;
        try { PairingStore.GetClient("127.0.0.1"); } catch (IOException) { missingPair = true; }
        Check(missingPair, "an unpaired address was accepted");
        PairingStore.SaveClient("127.0.0.1", keys);
        Console.WriteLine("PASS blank defaults, pairing import/export, identity persistence and certificate validation");

        BackupReceiver receiver = new BackupReceiver(dest, "127.0.0.1", delegate(string s) { Console.WriteLine("RECEIVER " + s); });
        Console.WriteLine("DEBUG receiver certificate imported");
        receiver.Start(0);
        Console.WriteLine("DEBUG listener started");
        int port = receiver.Port;
        try
        {
            try { BackupNetworkClient.Upload("127.0.0.1", port, set.Id, "DEMO_SCHEMA", set.Dump, set.Log, null); }
            catch { Thread.Sleep(500); throw; }
            List<BackupRecord> list = BackupNetworkClient.List("127.0.0.1", port);
            Check(list.Count == 1 && list[0].Id == set.Id, "uploaded backup not listed");
            string restored = Path.Combine(staging, "RESTORED.dmp");
            BackupNetworkClient.Download("127.0.0.1", port, set.Id, 1, restored, null);
            Check(File.ReadAllText(restored) == File.ReadAllText(set.Dump), "download differs");
            Console.WriteLine("PASS transfer protocol, complete-set listing and verified download");

            int verificationProgress = -1;
            BackupNetworkClient.Upload("127.0.0.1", port, compressedId, "DEMO_SCHEMA", archive, set.Log,
                delegate(int percent) { verificationProgress = percent; }, original);
            string setsRoot = Path.Combine(dest, "OracleBackupSets");
            string compressedDir = Path.Combine(setsRoot, compressedId);
            Check(File.Exists(Path.Combine(compressedDir, "backup.7z")) &&
                  Directory.GetFiles(compressedDir, "*.dmp").Length == 0 &&
                  receiver.GetRecords().Count == 2 && verificationProgress == 100,
                  "receiver did not publish a verified compressed-only backup");
            string receivedArchive = Path.Combine(temp, "received.7z");
            string receivedDump = Path.Combine(staging, "RECEIVED.dmp");
            DumpProof receivedProof = BackupNetworkClient.Download("127.0.0.1", port, compressedId, 1, receivedArchive, null);
            BackupPacking.Materialize(receivedArchive, receivedDump, compressedId, receivedProof, null, delegate(string s) { });
            Check(Wire.Equal(FileHash(receivedDump), original.Hash), "compressed network backup differs after restore");
            string badId = "DEMO_SCHEMA_TEST_CORRUPT_7Z";
            bool receiveRejected = false;
            try
            {
                BackupNetworkClient.Upload("127.0.0.1", port, badId, "DEMO_SCHEMA", archive, set.Log, null, original);
            }
            catch (IOException ex) { receiveRejected = ex.Message.Contains("校验或保存失败"); }
            Check(receiveRejected && !Directory.Exists(Path.Combine(setsRoot, badId)), "wrong archive entry was committed");
            string corruptArchive = Path.Combine(temp, "corrupt.7z");
            File.WriteAllBytes(corruptArchive, new byte[] { 0x37, 0x7a, 1, 2, 3 });
            receiveRejected = false;
            try
            {
                BackupNetworkClient.Upload("127.0.0.1", port, "DEMO_SCHEMA_TRUNCATED_7Z", "DEMO_SCHEMA",
                    corruptArchive, set.Log, null, original);
            }
            catch (IOException ex) { receiveRejected = ex.Message.Contains("校验或保存失败"); }
            Check(receiveRejected && receiver.GetRecords().Count == 2, "truncated archive was cataloged");
            BackupNetworkClient.Upload("127.0.0.1", port, legacyId, "DEMO_SCHEMA", legacyZip, set.Log, null);
            string legacyDownload = Path.Combine(temp, "legacy-download");
            string legacyRestore = Path.Combine(temp, "legacy-restored.dmp");
            BackupNetworkClient.Download("127.0.0.1", port, legacyId, 1, legacyDownload, null);
            BackupPacking.Materialize(legacyDownload, legacyRestore, legacyId, null, null, delegate(string s) { });
            Check(Wire.Equal(FileHash(legacyRestore), original.Hash), "legacy ZIP network restore differs");
            list = BackupNetworkClient.List("127.0.0.1", port);
            Check(list.Count == 3 && list.Exists(delegate(BackupRecord item) { return item.Id == compressedId; }),
                  "new list did not include old and new formats");
            using (TcpClient tcp = new TcpClient("127.0.0.1", port))
            using (Stream transport = ConnectRaw(tcp, keys))
            using (BinaryReader reader = new BinaryReader(transport))
            using (BinaryWriter writer = new BinaryWriter(transport))
            {
                writer.Write(Wire.Magic);
                writer.Write(keys.Token);
                writer.Flush();
                Check(reader.ReadByte() == 1, "legacy list authentication failed");
                writer.Write((byte)2);
                writer.Flush();
                Check(reader.ReadInt32() == 2, "legacy client was offered unsupported 7z backups");
            }
            Check(Directory.GetDirectories(setsRoot, ".incoming_*").Length == 0, "rejected files were left in receiver staging");
            Console.WriteLine("PASS verified 7z upload, corrupt archive rejection, and ZIP/DMP compatibility");

            config.User = "SYSTEM";
            Environment.SetEnvironmentVariable("MOCK_OBJECTS", "3");
            bool blocked = false;
            try { OracleWork.CheckEmptyTarget(config, "TEST_EMPTY", delegate(string s) { }); }
            catch { blocked = true; }
            Check(blocked, "nonempty target was not blocked");
            Environment.SetEnvironmentVariable("MOCK_OBJECTS", "0");
            List<string> restoreLogs = new List<string>();
            OracleWork.CheckEmptyTarget(config, "TEST_EMPTY", restoreLogs.Add);
            string importLog = OracleWork.Restore(config, "DEMO_SCHEMA", "TEST_EMPTY", receivedDump, restoreLogs.Add);
            Check(File.Exists(importLog), "mock import log missing");
            Check(restoreLogs.Contains("Mock SQL*Plus emitted ********") && restoreLogs.Contains("Mock Data Pump emitted ********") &&
                  restoreLogs.TrueForAll(delegate(string s) { return !s.Contains(specialPassword); }),
                  "restore output did not redact passwords");
            Console.WriteLine("PASS empty-target check, nonempty rejection and special-character login for mocked import");

            using (TcpClient tcp = new TcpClient("127.0.0.1", port))
            using (Stream tls = ConnectRaw(tcp, keys))
            {
                tls.Write(Wire.Magic, 0, Wire.Magic.Length);
                tls.Write(new byte[32], 0, 32);
                tls.Flush();
                Check(tls.ReadByte() == 0, "wrong authentication token was accepted");
            }
            Console.WriteLine("PASS unauthorized token rejected");
            Thread.Sleep(150);

            string interruptedId = "DEMO_SCHEMA_INTERRUPTED_123456";
            using (TcpClient tcp = new TcpClient("127.0.0.1", port))
            using (Stream tls = ConnectRaw(tcp, keys))
            using (BinaryReader reader = new BinaryReader(tls))
            using (BinaryWriter writer = new BinaryWriter(tls))
            {
                writer.Write(Wire.Magic);
                writer.Write(keys.Token);
                writer.Flush();
                Check(reader.ReadByte() == 1, "test authentication failed");
                writer.Write((byte)1);
                Wire.WriteText(writer, interruptedId, 90);
                Wire.WriteText(writer, "DEMO_SCHEMA", 30);
                writer.Flush();
                Check(reader.ReadByte() == 1, "test upload not accepted");
                writer.Write((long)1024);
                writer.Write(new byte[3]);
                writer.Flush();
            }
            Thread.Sleep(250);
            Check(!Directory.Exists(Path.Combine(dest, "OracleBackupSets", interruptedId)),
                  "interrupted upload became a complete backup");
            Console.WriteLine("PASS interrupted transfer is not committed as a backup");

            string stored = Path.Combine(dest, "OracleBackupSets", set.Id, "backup.dmp");
            byte[] corrupted = File.ReadAllBytes(stored);
            corrupted[0] ^= 1;
            File.WriteAllBytes(stored, corrupted);
            string corruptedDest = Path.Combine(staging, "CORRUPT.dmp");
            bool mismatch = false;
            try { BackupNetworkClient.Download("127.0.0.1", port, set.Id, 1, corruptedDest, null); }
            catch { mismatch = true; }
            Check(mismatch && !File.Exists(corruptedDest), "stored file corruption was not rejected");
            Console.WriteLine("PASS stored-data corruption rejected before restore");

            Thread.Sleep(150);
            SetAge(Path.Combine(setsRoot, set.Id), 40);
            SetAge(compressedDir, 40);
            string unrelated = Path.Combine(dest, "unrelated.txt");
            File.WriteAllText(unrelated, "preserve");
            string secondId = "DEMO_SCHEMA_RETENTION_EXTRA_123456";
            BackupNetworkClient.Upload("127.0.0.1", port, secondId, "DEMO_SCHEMA", set.Dump, set.Log, null);
            string secondDir = Path.Combine(dest, "OracleBackupSets", secondId);
            SetAge(secondDir, 40);
            string extra = Path.Combine(secondDir, "unrelated.txt");
            File.WriteAllText(extra, "preserve");
            Check(receiver.CleanOlderThan(30) == 2 && File.Exists(unrelated) &&
                  !Directory.Exists(compressedDir), "retention removed wrong files");
            Check(File.Exists(extra), "retention deleted a directory containing an unexpected file");
            Console.WriteLine("PASS expiration removes only validated managed backup");
        }
        finally { receiver.Stop(); }

        BackupReceiver denied = new BackupReceiver(Path.Combine(temp, "denied"), "192.0.2.25", delegate(string s) { });
        denied.Start(0);
        bool rejected = false;
        try { BackupNetworkClient.List("127.0.0.1", denied.Port); } catch { rejected = true; }
        finally { denied.Stop(); }
        Check(rejected, "unlisted IP was not rejected");
        Console.WriteLine("PASS unlisted IP rejected");
        Console.WriteLine("ALL TESTS PASSED");
    }
}
