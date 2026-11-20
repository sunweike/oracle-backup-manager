using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

internal static class ClientUiSmoke
{
    private static T Field<T>(ClientForm form, string name) where T : class
    {
        return (T)typeof(ClientForm).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
    }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static void WaitFor(Func<bool> condition, string message)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        do
        {
            Application.DoEvents();
            if (condition()) return;
            Thread.Sleep(50);
        }
        while (DateTime.UtcNow < deadline);
        throw new Exception(message);
    }
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Preferences.TestFolder = Path.Combine(args[0], "preferences");
            Func<string, string> protect = delegate(string text) { return Convert.ToBase64String(Encoding.UTF8.GetBytes(text)); };
            Func<string, string> unprotect = delegate(string text) { return Encoding.UTF8.GetString(Convert.FromBase64String(text)); };
            BackupTaskStore store = new BackupTaskStore(args[0], protect, unprotect);
            BackupTask first = BackupTask.NewTask("First"); first.Server = "127.0.0.1";
            BackupTask second = BackupTask.NewTask("Second"); second.Server = "127.0.0.1";
            store.Save(new List<BackupTask> { first, second });
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (ClientForm form = new ClientForm(store, false))
            {
                form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-20000, -20000);
                form.Show(); Application.DoEvents();
                ListView list = Field<ListView>(form, "taskList");
                ComboBox compression = Field<ComboBox>(form, "compression");
                Check(list.Items.Count == 2 && list.SelectedItems.Count == 1, "task list did not initialize");
                Check(compression.Text == "快速（3）", "new tasks did not default to Fast");
                compression.SelectedIndex = 2;
                Field<TextBox>(form, "taskName").Text = "First edited";
                list.Items[1].Selected = true; Application.DoEvents();
                Check((string)list.SelectedItems[0].Tag == second.Id, "selection did not change");
                Check(store.Load()[0].Name == "First edited", "switching did not persist edits");
                Check(store.Load()[0].Compression == BackupCompression.Balanced && compression.Text == "快速（3）",
                      "switching tasks did not preserve independent compression choices");
                Field<Button>(form, "addTask").PerformClick(); Application.DoEvents();
                Check(list.Items.Count == 3 && store.Load().Count == 3, "adding task failed");
                Field<TextBox>(form, "server").Text = "127.0.0.1";
                Field<TextBox>(form, "dbHost").Text = "127.0.0.1";
                Field<TextBox>(form, "service").Text = "TEST_ORCL";
                Field<TextBox>(form, "user").Text = "DEMO_SCHEMA";
                Field<TextBox>(form, "schema").Text = "DEMO_SCHEMA";
                Field<TextBox>(form, "password").Text = "TEST_SECRET";
                Field<ComboBox>(form, "scheduleMode").SelectedIndex = 1;
                Field<DateTimePicker>(form, "dailyTime").Value = DateTime.Today.AddHours(2).AddMinutes(15);
                Field<CheckBox>(form, "auto").Checked = true;
                Field<Button>(form, "saveTask").PerformClick(); Application.DoEvents();
                List<BackupTask> saved = store.Load();
                Check(saved[2].Scheduled && saved[2].ScheduleMode == "daily" && saved[2].DailyMinute == 135,
                      "daily schedule settings did not save");
                form.PerformLayout();
                using (Bitmap image = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(image, new Rectangle(0, 0, image.Width, image.Height));
                    image.Save(args[1]);
                }
                form.Close();
            }
            Console.WriteLine("PASS UI task selection, edit persistence, add and daily schedule");

            string scheduledRoot = Path.Combine(args[0], "scheduled");
            string staging = Path.Combine(scheduledRoot, "staging");
            string sample = Path.Combine(scheduledRoot, "sample.dmp");
            string destination = Path.Combine(scheduledRoot, "receiver");
            Directory.CreateDirectory(staging);
            File.WriteAllText(sample, new string('A', 2 * 1024 * 1024));
            Environment.SetEnvironmentVariable("TEMP", scheduledRoot);
            Environment.SetEnvironmentVariable("MOCK_STAGING", staging);
            Environment.SetEnvironmentVariable("MOCK_DUMP_SAMPLE", sample);
            Environment.SetEnvironmentVariable("MOCK_DIRECTORY_MODE", "match");
            BackupTask scheduledOne = BackupTask.NewTask("Scheduled one");
            BackupTask scheduledTwo = BackupTask.NewTask("Scheduled two");
            scheduledOne.DeleteStaging = true;
            scheduledTwo.Service = "TEST_ORCL2";
            scheduledTwo.Compression = BackupCompression.None;
            foreach (BackupTask task in new BackupTask[] { scheduledOne, scheduledTwo })
            {
                task.Server = task.Host = "127.0.0.1";
                if (String.IsNullOrEmpty(task.Service)) task.Service = "TEST_ORCL";
                task.User = task.Schema = "DEMO_SCHEMA";
                task.Password = "TEST_SECRET"; task.Bin = args[2]; task.Staging = staging;
                task.Scheduled = true; task.NextUtc = DateTime.UtcNow.AddMinutes(-2);
            }
            BackupTaskStore scheduledStore = new BackupTaskStore(Path.Combine(scheduledRoot, "settings"), protect, unprotect);
            scheduledStore.Save(new List<BackupTask> { scheduledOne, scheduledTwo });
            using (ReceiverIdentity identity = ReceiverIdentity.LoadOrCreate())
                PairingStore.SaveClient("127.0.0.1", identity.Keys);
            BackupReceiver receiver = new BackupReceiver(destination, "127.0.0.1",
                                                          delegate(string line) { Console.WriteLine("UI RECEIVER " + line); });
            receiver.Start(Wire.DefaultPort);
            try
            {
                using (ClientForm form = new ClientForm(scheduledStore, true, delegate(string message)
                {
                    return message.Contains("127.0.0.1/TEST_ORCL") && message.Contains("TEST_EMPTY");
                }))
                {
                    form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual;
                    form.Location = new Point(-20000, -20000); form.Show();
                    WaitFor(delegate
                    {
                        List<BackupTask> current = scheduledStore.Load();
                        return current[0].LastStatus == "成功" && current[1].LastStatus == "成功";
                    }, "scheduled tasks did not both succeed");
                    List<BackupRecord> delivered = receiver.GetRecords();
                    Check(delivered.Count == 2 &&
                          delivered.Exists(item => Wire.IsSevenZip(item.Id) && item.DumpSize < item.Original.Length) &&
                          delivered.Exists(item => !Wire.IsCompressed(item.Id) && item.DumpSize == new FileInfo(sample).Length),
                          "scheduled tasks did not deliver both the selected 7z and raw DMP formats");
                    Check(Directory.GetFiles(staging, "*.dmp").Length == 1 &&
                          Directory.GetFiles(staging, "*.7z").Length == 0, "staging cleanup was incorrect");
                    Console.WriteLine("PASS scheduled UI tasks used their chosen compression levels and delivered 7z and DMP over TLS");

                    ListView records = Field<ListView>(form, "records");
                    Button refresh = Field<Button>(form, "refresh");
                    Button restore = Field<Button>(form, "restore");
                    refresh.PerformClick();
                    WaitFor(delegate { return records.Items.Count == 2 && refresh.Enabled; },
                            "refresh did not show both archives");
                    ListViewItem compressed = null;
                    foreach (ListViewItem item in records.Items)
                        if (item.SubItems[2].Text == "7z") compressed = item;
                    Check(compressed != null, "UI did not display the archive format");
                    compressed.Selected = true;
                    Field<TextBox>(form, "target").Text = "TEST_EMPTY";
                    Field<TextBox>(form, "admin").Text = "TEST_ADMIN";
                    Field<TextBox>(form, "adminPassword").Text = "TEST_SECRET";
                    restore.PerformClick();
                    TextBox log = Field<TextBox>(form, "log");
                    WaitFor(delegate { return restore.Enabled && log.Text.Contains("导入命令结束"); },
                            "restore did not import the verified sample");
                    Check(Directory.GetFiles(staging, "RESTORE_*.log").Length == 1 &&
                          Directory.GetFiles(staging, "*_RESTORE_*.dmp").Length == 0, "restore cleanup was incorrect");

                    string recordId = compressed.Text;
                    string manifest = Path.Combine(destination, "OracleBackupSets", recordId, "manifest.txt");
                    string original = File.ReadAllText(manifest);
                    string broken = Regex.Replace(original, "original_sha256=[A-F0-9]{64}",
                                                  "original_sha256=" + new string('0', 64));
                    Check(broken != original, "test did not modify the stored original checksum");
                    File.WriteAllText(manifest, broken, Encoding.ASCII);
                    restore.PerformClick();
                    WaitFor(delegate { return restore.Enabled && log.Text.Contains("SHA-256 校验失败"); },
                            "restore did not report the incorrect original checksum");
                    Check(Directory.GetFiles(staging, "RESTORE_*.log").Length == 1 &&
                          Directory.GetFiles(staging, "*_RESTORE_*.dmp*").Length == 0, "bad restore reached import or left temporary files");
                    Console.WriteLine("PASS UI restore checked bytes before import and blocked an incorrect SHA-256");
                    form.Close(); Application.DoEvents();
                }
            }
            finally { receiver.Stop(); }
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL UI " + ex); return 1; }
    }
}
