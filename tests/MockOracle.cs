using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

internal static class MockOracle
{
    private static int Main(string[] args)
    {
        string name = Path.GetFileNameWithoutExtension(Environment.GetCommandLineArgs()[0]).ToLowerInvariant();
        string specialPassword = Environment.GetEnvironmentVariable("MOCK_SPECIAL_PASSWORD");
        string password = specialPassword ?? "TEST_SECRET";
        string loginPattern = @"\A[A-Z_]+/" +
            Regex.Escape(specialPassword == null ? password : "\"" + password + "\"") +
            @"@//127\.0\.0\.1:1521/TEST_ORCL2?\z";
        if (args.Any(arg => arg.Contains(password))) return 15;
        if (name == "sqlplus")
        {
            string input = Console.In.ReadToEnd();
            const string begin = "WHENEVER OSERROR EXIT FAILURE\r\nWHENEVER SQLERROR EXIT FAILURE\r\n" +
                "SET DEFINE OFF\r\nCONNECT ";
            if (!input.StartsWith(begin, StringComparison.Ordinal)) return 4;
            int end = input.IndexOf("\r\n", begin.Length, StringComparison.Ordinal);
            if (end < 0 || !Regex.IsMatch(input.Substring(begin.Length, end - begin.Length), loginPattern)) return 15;
            Console.WriteLine("Mock SQL*Plus emitted " + password);
            if (input.Contains("ALL_DIRECTORIES"))
            {
                if (!input.Contains("OBM_DIRECTORY_PATH=") || !input.Contains("ORACLE_BACKUP_DIR") ||
                    !input.Contains("SYS_CONTEXT('USERENV','SERVER_HOST')")) return 11;
                string mode = Environment.GetEnvironmentVariable("MOCK_DIRECTORY_MODE") ?? "match";
                if (mode == "sqlerror")
                {
                    Console.WriteLine("ORA-01031: insufficient privileges");
                    return 1;
                }
                Console.WriteLine("OBM_DATABASE_HOST=" +
                    (mode == "remote" ? "SOME_OTHER_SERVER" : Environment.MachineName));
                string created = Environment.GetEnvironmentVariable("MOCK_CREATED_FLAG");
                if (mode == "missing" || (mode == "auto" && !File.Exists(created)))
                {
                    Console.WriteLine("OBM_DIRECTORY_CHECK_COMPLETE");
                    return 0;
                }
                if (mode == "incomplete") return 0;
                string directory = Environment.GetEnvironmentVariable("MOCK_STAGING");
                if (mode == "mismatch") directory = @"C:\SomeOtherDirectory";
                if (mode == "case_trailing") directory = directory.ToLowerInvariant() + "\\";
                Console.WriteLine("OBM_DIRECTORY_PATH=" + directory);
                Console.WriteLine("OBM_DIRECTORY_CHECK_COMPLETE");
                return 0;
            }
            if (input.Contains("CREATE DIRECTORY"))
            {
                if (input.Contains("OR REPLACE") || !input.Contains("SET DEFINE OFF")) return 12;
                if (Environment.GetEnvironmentVariable("MOCK_DIRECTORY_MODE") == "missing")
                {
                    Console.WriteLine("ORA-01031: insufficient privileges");
                    return 1;
                }
                if (Environment.GetEnvironmentVariable("MOCK_DIRECTORY_MODE") != "auto") return 13;
                File.WriteAllText(Environment.GetEnvironmentVariable("MOCK_CREATED_FLAG"), "created");
                Console.WriteLine("OBM_DIRECTORY_CREATED");
                return 0;
            }
            if (input.Contains("DBA_USERS") && input.Contains("DBA_OBJECTS"))
            {
                Console.WriteLine("USER_COUNT=1");
                Console.WriteLine("OBJECT_COUNT=" + (Environment.GetEnvironmentVariable("MOCK_OBJECTS") ?? "0"));
                return 0;
            }
            return 4;
        }
        if (args.Length != 1 || !args[0].StartsWith("parfile=")) return 5;
        string login = Console.In.ReadLine();
        if (login == null || !Regex.IsMatch(login, loginPattern) || Console.In.ReadLine() != null) return 15;
        string path = args[0].Substring("parfile=".Length).Trim('"');
        if (!File.Exists(path)) return 6;
        Dictionary<string, string> values = new Dictionary<string, string>();
        foreach (string line in File.ReadAllLines(path))
        {
            int pos = line.IndexOf('=');
            if (pos > 0) values[line.Substring(0, pos)] = line.Substring(pos + 1);
        }
        if (values.ContainsKey("USERID") || !values.ContainsKey("DIRECTORY") ||
            !values.ContainsKey("DUMPFILE") || !values.ContainsKey("LOGFILE")) return 7;
        string dir = Environment.GetEnvironmentVariable("MOCK_STAGING");
        string dump = Path.Combine(dir, values["DUMPFILE"]);
        string sample = Environment.GetEnvironmentVariable("MOCK_DUMP_SAMPLE");
        if (name == "expdp")
        {
            string marker = Environment.GetEnvironmentVariable("MOCK_PUMP_MARKER");
            if (!String.IsNullOrEmpty(marker)) File.AppendAllText(marker, "started\r\n");
            if (Environment.GetEnvironmentVariable("MOCK_PUMP_ERROR") == "file")
            {
                Console.WriteLine("ORA-39070: cannot open the log file");
                Console.WriteLine("ORA-29283: invalid file operation");
                return 1;
            }
            if (values["SCHEMAS"] != "DEMO_SCHEMA" || values["FLASHBACK_TIME"] != "SYSTIMESTAMP") return 8;
            if (String.IsNullOrEmpty(sample))
                File.WriteAllText(dump, "MOCK DATA PUMP DUMP");
            else
                File.Copy(sample, dump);
            File.WriteAllText(Path.Combine(dir, values["LOGFILE"]), "MOCK EXPORT LOG");
        }
        else if (name == "impdp")
        {
            if (values["TABLE_EXISTS_ACTION"] != "SKIP" ||
                values["REMAP_SCHEMA"] != "DEMO_SCHEMA:TEST_EMPTY") return 9;
            if (!File.Exists(dump) || (!String.IsNullOrEmpty(sample) &&
                !File.ReadAllBytes(dump).SequenceEqual(File.ReadAllBytes(sample)))) return 14;
            File.WriteAllText(Path.Combine(dir, values["LOGFILE"]), "MOCK IMPORT LOG");
        }
        else return 10;
        Console.WriteLine("Mock Data Pump emitted " + password);
        return 0;
    }
}
