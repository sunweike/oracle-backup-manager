using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

internal sealed class ServerForm : Form
{
    private readonly TextBox root = new TextBox();
    private readonly TextBox allow = new TextBox();
    private readonly NumericUpDown days = new NumericUpDown();
    private readonly CheckBox cleanup = new CheckBox();
    private readonly Button start = new Button();
    private readonly Button stop = new Button();
    private readonly Button refresh = new Button();
    private readonly Button cleanNow = new Button();
    private readonly Button exportPair = new Button();
    private readonly ListView list = new ListView();
    private readonly TextBox log = new TextBox();
    private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    private BackupReceiver receiver;
    private int cleaning;

    internal ServerForm()
    {
        Text = "Oracle接受端";
        using (Stream icon = typeof(ServerForm).Assembly.GetManifestResourceStream("OracleBackup.Receive.ico"))
            if (icon != null) Icon = new Icon(icon);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(850, 660);
        Size = new Size(1010, 760);
        Font = new Font("Microsoft YaHei UI", 9F);
        TableLayoutPanel layout = new TableLayoutPanel();
        layout.Dock = DockStyle.Fill;
        layout.Padding = new Padding(10);
        layout.ColumnCount = 1;
        layout.RowCount = 4;
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 185));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        Controls.Add(layout);

        TableLayoutPanel config = new TableLayoutPanel();
        config.Dock = DockStyle.Fill;
        config.ColumnCount = 4;
        config.RowCount = 4;
        config.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115));
        config.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        config.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        config.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
        for (int i = 0; i < 4; i++) config.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
        layout.Controls.Add(config, 0, 0);
        AddLabel(config, "备份根目录", 0, 0);
        root.Text = "";
        root.Dock = DockStyle.Fill;
        config.Controls.Add(root, 1, 0);
        AddLabel(config, "监听端口", 2, 0);
        AddLabel(config, "17999 / TLS", 3, 0);
        AddLabel(config, "允许来源 IP", 0, 1);
        allow.Text = "";
        allow.Dock = DockStyle.Fill;
        config.Controls.Add(allow, 1, 1);
        start.Text = "启动接收";
        start.Dock = DockStyle.Fill;
        stop.Text = "停止接收";
        stop.Dock = DockStyle.Fill;
        stop.Enabled = false;
        config.Controls.Add(start, 2, 1);
        config.Controls.Add(stop, 3, 1);
        AddLabel(config, "保留设置", 0, 2);
        FlowLayoutPanel retention = new FlowLayoutPanel();
        retention.Dock = DockStyle.Fill;
        cleanup.Text = "启用自动清理";
        cleanup.AutoSize = true;
        retention.Controls.Add(cleanup);
        days.Minimum = 1;
        days.Maximum = 3650;
        days.Value = 30;
        days.Width = 65;
        retention.Controls.Add(days);
        retention.Controls.Add(new Label { Text = "天以前的完整备份", AutoSize = true, Padding = new Padding(2, 6, 0, 0) });
        config.Controls.Add(retention, 1, 2);
        refresh.Text = "刷新列表";
        refresh.Dock = DockStyle.Fill;
        cleanNow.Text = "立即清理";
        cleanNow.Dock = DockStyle.Fill;
        config.Controls.Add(refresh, 2, 2);
        config.Controls.Add(cleanNow, 3, 2);
        AddLabel(config, "TLS 配对", 0, 3);
        AddLabel(config, "首次使用时导出配对文件，再到备份端导入。", 1, 3);
        exportPair.Text = "导出配对文件";
        exportPair.Dock = DockStyle.Fill;
        config.Controls.Add(exportPair, 3, 3);

        list.Dock = DockStyle.Fill;
        list.View = View.Details;
        list.FullRowSelect = true;
        list.Columns.Add("备份编号", 365);
        list.Columns.Add("账号", 150);
        list.Columns.Add("格式", 65);
        list.Columns.Add("接收时间", 175);
        list.Columns.Add("大小", 100);
        layout.Controls.Add(list, 0, 1);
        layout.Controls.Add(new Label { Text = "运行记录（只清理本工具已完成的备份）", Dock = DockStyle.Fill }, 0, 2);
        log.Multiline = true;
        log.ReadOnly = true;
        log.ScrollBars = ScrollBars.Vertical;
        log.Dock = DockStyle.Fill;
        layout.Controls.Add(log, 0, 3);

        LoadPreferences();
        start.Click += delegate { StartReceiver(); };
        stop.Click += delegate { StopReceiver(); };
        refresh.Click += delegate { RefreshList(); };
        exportPair.Click += delegate { ExportPairing(); };
        cleanup.CheckedChanged += delegate
        {
            if (cleanup.Checked && MessageBox.Show("启用后，程序运行时会永久删除超过设置天数的完整备份。是否启用？",
                "确认自动清理", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                cleanup.Checked = false;
                return;
            }
            SavePreferences();
        };
        days.ValueChanged += delegate { SavePreferences(); };
        cleanNow.Click += delegate
        {
            if (receiver == null) { MessageBox.Show("请先启动接收端。"); return; }
            if (MessageBox.Show("将永久删除本工具中超过 " + days.Value + " 天的完整备份，是否继续？",
                                "确认清理", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                CleanAsync();
        };
        timer.Interval = 3600000;
        timer.Tick += delegate { if (cleanup.Checked && receiver != null) CleanAsync(); };
        timer.Start();
        FormClosing += delegate { SavePreferences(); if (receiver != null) receiver.Stop(); };
    }

    private static void AddLabel(TableLayoutPanel panel, string text, int col, int row)
    {
        panel.Controls.Add(new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, col, row);
    }
    private void AddLog(string value)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(new Action<string>(AddLog), value); } catch { } return; }
        log.AppendText(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + value + Environment.NewLine);
        Preferences.AppendLog("server", value);
    }
    private void StartReceiver()
    {
        try
        {
            receiver = new BackupReceiver(root.Text.Trim(), allow.Text.Trim(), AddLog);
            receiver.Start(Wire.DefaultPort);
            SavePreferences();
            root.Enabled = allow.Enabled = start.Enabled = false;
            stop.Enabled = true;
            RefreshList();
        }
        catch (Exception ex) { receiver = null; MessageBox.Show(ex.Message, "启动失败"); }
    }
    private void ExportPairing()
    {
        try
        {
            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Title = "保存配对文件";
                dialog.Filter = "Oracle 配对文件 (*.obm-pair)|*.obm-pair";
                dialog.FileName = "OracleBackupManager.obm-pair";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                using (ReceiverIdentity identity = ReceiverIdentity.LoadOrCreate())
                    identity.Keys.Export(dialog.FileName);
                AddLog("已导出配对文件。请通过可信渠道交给对应的备份端。");
            }
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "配对文件导出失败"); }
    }
    private void StopReceiver()
    {
        if (receiver != null) receiver.Stop();
        receiver = null;
        root.Enabled = allow.Enabled = start.Enabled = true;
        stop.Enabled = false;
    }
    private void RefreshList()
    {
        if (receiver == null) return;
        try
        {
            List<BackupRecord> data = receiver.GetRecords();
            list.Items.Clear();
            foreach (BackupRecord r in data)
            {
                ListViewItem item = new ListViewItem(r.Id);
                item.SubItems.Add(r.Schema);
                item.SubItems.Add(Wire.FormatName(r.Id));
                item.SubItems.Add(r.Utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
                item.SubItems.Add(Wire.FormatSize(r.DumpSize));
                list.Items.Add(item);
            }
        }
        catch (Exception ex) { AddLog("读取备份列表失败：" + ex.Message); }
    }
    private void CleanAsync()
    {
        if (Interlocked.CompareExchange(ref cleaning, 1, 0) != 0) return;
        BackupReceiver instance = receiver;
        int keepDays = (int)days.Value;
        Thread t = new Thread(delegate()
        {
            try { AddLog("清理完成，删除 " + instance.CleanOlderThan(keepDays) + " 个到期备份"); }
            catch (Exception ex) { AddLog("清理失败：" + ex.Message); }
            finally
            {
                Interlocked.Exchange(ref cleaning, 0);
                try { if (!IsDisposed) BeginInvoke(new Action(RefreshList)); } catch { }
            }
        });
        t.IsBackground = true;
        t.Start();
    }

    private void LoadPreferences()
    {
        Dictionary<string, string> p = Preferences.Load("server");
        root.Text = Preferences.Get(p, "root", root.Text);
        allow.Text = Preferences.Get(p, "allow", allow.Text);
        int n;
        if (Int32.TryParse(Preferences.Get(p, "days", "30"), out n) && n >= 1 && n <= 3650)
            days.Value = n;
        cleanup.Checked = Preferences.Get(p, "cleanup", "0") == "1";
    }
    private void SavePreferences()
    {
        try
        {
            Preferences.Save("server", new Dictionary<string, string> {
                { "root", root.Text }, { "allow", allow.Text }, { "days", days.Value.ToString() },
                { "cleanup", cleanup.Checked ? "1" : "0" }
            });
        }
        catch (Exception ex) { AddLog("设置未能保存：" + ex.Message); }
    }
}

internal static class ServerEntry
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new ServerForm());
    }
}
