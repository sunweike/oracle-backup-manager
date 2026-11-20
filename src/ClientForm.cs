using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

internal sealed class ClientForm : Form
{
    private static readonly BackupCompression[] CompressionLevels = {
        BackupCompression.None, BackupCompression.Fast, BackupCompression.Balanced, BackupCompression.Maximum
    };
    private readonly TextBox taskName = new TextBox(), server = new TextBox(), dbHost = new TextBox(),
        dbPort = new TextBox(), service = new TextBox(), user = new TextBox(), password = new TextBox(),
        schema = new TextBox(), directory = new TextBox(), staging = new TextBox(), oracleBin = new TextBox(),
        target = new TextBox(), admin = new TextBox(), adminPassword = new TextBox();
    private readonly Button addTask = new Button(), saveTask = new Button(), deleteTask = new Button(),
        backup = new Button(), refresh = new Button(), restore = new Button(), importPair = new Button();
    private readonly CheckBox auto = new CheckBox(), deleteStaging = new CheckBox();
    private readonly NumericUpDown interval = new NumericUpDown();
    private readonly ComboBox scheduleMode = new ComboBox(), compression = new ComboBox();
    private readonly DateTimePicker dailyTime = new DateTimePicker();
    private readonly ListView taskList = new ListView(), records = new ListView();
    private readonly Label dirtyLabel = new Label();
    private readonly TextBox log = new TextBox();
    private readonly ProgressBar progress = new ProgressBar();
    private readonly Label progressLabel = Label("就绪");
    private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    private readonly BackupTaskStore store;
    private readonly List<Control> editControls = new List<Control>();
    private List<BackupTask> tasks = new List<BackupTask>();
    private BackupTask selected;
    private string recordsServer;
    private bool busy, loading, dirty, storeReady;

    internal ClientForm() : this(BackupTaskStore.Default(), true) { }

    internal ClientForm(BackupTaskStore taskStore, bool startTimer, Func<string, bool> confirmRestore = null)
    {
        store = taskStore;
        Text = "Oracle备份端";
        using (Stream icon = typeof(ClientForm).Assembly.GetManifestResourceStream("OracleBackup.Backup.ico"))
            if (icon != null) Icon = new Icon(icon);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1040, 850);
        Size = new Size(1220, 1010);
        Font = new Font("Microsoft YaHei UI", 9F);
        TableLayoutPanel main = new TableLayoutPanel();
        main.Dock = DockStyle.Fill;
        main.Padding = new Padding(10);
        main.ColumnCount = 1;
        main.RowCount = 7;
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 178));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 215));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 116));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 48));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 98));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 52));
        Controls.Add(main);

        TableLayoutPanel taskArea = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        taskArea.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        taskArea.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        main.Controls.Add(taskArea, 0, 0);
        FlowLayoutPanel taskBar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        taskBar.Controls.Add(new Label { Text = "任务名称", Width = 72, Padding = new Padding(0, 6, 0, 0) });
        taskName.Width = 180;
        taskBar.Controls.Add(taskName);
        addTask.Text = "新增任务"; addTask.Width = 90; taskBar.Controls.Add(addTask);
        saveTask.Text = "保存任务"; saveTask.Width = 90; taskBar.Controls.Add(saveTask);
        deleteTask.Text = "删除任务"; deleteTask.Width = 90; taskBar.Controls.Add(deleteTask);
        dirtyLabel.AutoSize = true; dirtyLabel.Padding = new Padding(8, 6, 0, 0); taskBar.Controls.Add(dirtyLabel);
        taskArea.Controls.Add(taskBar, 0, 0);
        taskList.Dock = DockStyle.Fill;
        taskList.View = View.Details;
        taskList.FullRowSelect = true;
        taskList.HideSelection = false;
        taskList.MultiSelect = false;
        taskList.Columns.Add("任务", 155);
        taskList.Columns.Add("数据库", 180);
        taskList.Columns.Add("备份账号", 130);
        taskList.Columns.Add("计划", 170);
        taskList.Columns.Add("下次执行", 165);
        taskList.Columns.Add("上次结果", 145);
        taskArea.Controls.Add(taskList, 0, 1);

        TableLayoutPanel settings = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 5 };
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        for (int i = 0; i < 5; i++) settings.RowStyles.Add(new RowStyle(SizeType.Percent, 20));
        main.Controls.Add(settings, 0, 1);
        Field(settings, "接受端 IP", server, 0, 0);
        settings.Controls.Add(Label("接受端端口"), 2, 0);
        FlowLayoutPanel pairing = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        pairing.Controls.Add(new Label { Text = "17999 / TLS", AutoSize = true, Padding = new Padding(0, 6, 6, 0) });
        importPair.Text = "导入配对"; importPair.Width = 100;
        pairing.Controls.Add(importPair);
        settings.Controls.Add(pairing, 3, 0);
        Field(settings, "数据库 IP", dbHost, 0, 1);
        Field(settings, "数据库端口", dbPort, 2, 1);
        Field(settings, "数据库服务", service, 0, 2);
        Field(settings, "备份账号", user, 2, 2);
        Field(settings, "数据库密码", password, 0, 3); password.UseSystemPasswordChar = true;
        Field(settings, "备份用户名", schema, 2, 3);
        Field(settings, "Oracle 目录", directory, 0, 4);
        Field(settings, "暂存目录（可空）", staging, 2, 4);

        TableLayoutPanel actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 3 };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        for (int i = 0; i < 3; i++) actions.RowStyles.Add(new RowStyle(SizeType.Percent, 33.333F));
        main.Controls.Add(actions, 0, 2);
        actions.Controls.Add(Label("Oracle bin"), 0, 0);
        oracleBin.Dock = DockStyle.Fill; actions.Controls.Add(oracleBin, 1, 0);
        backup.Text = "备份当前任务"; backup.Dock = DockStyle.Fill; actions.Controls.Add(backup, 2, 0);
        refresh.Text = "刷新备份列表"; refresh.Dock = DockStyle.Fill; actions.Controls.Add(refresh, 3, 0);
        FlowLayoutPanel schedule = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        auto.Text = "启用计划"; auto.AutoSize = true; schedule.Controls.Add(auto);
        scheduleMode.DropDownStyle = ComboBoxStyle.DropDownList; scheduleMode.Width = 105;
        scheduleMode.Items.AddRange(new object[] { "每隔小时", "每天指定时间" }); schedule.Controls.Add(scheduleMode);
        interval.Minimum = 1; interval.Maximum = 720; interval.Width = 60; schedule.Controls.Add(interval);
        schedule.Controls.Add(new Label { Text = "小时", AutoSize = true, Padding = new Padding(0, 5, 0, 0) });
        dailyTime.Format = DateTimePickerFormat.Custom; dailyTime.CustomFormat = "HH:mm";
        dailyTime.ShowUpDown = true; dailyTime.Width = 82; schedule.Controls.Add(dailyTime);
        schedule.Controls.Add(new Label { Text = "（客户端需保持运行）", AutoSize = true, Padding = new Padding(0, 5, 0, 0) });
        actions.Controls.Add(schedule, 1, 1); actions.SetColumnSpan(schedule, 3);
        FlowLayoutPanel options = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        actions.Controls.Add(Label("7z 压缩等级"), 0, 2);
        compression.DropDownStyle = ComboBoxStyle.DropDownList;
        compression.Width = 145;
        compression.Items.AddRange(new object[] { "不压缩", "快速（3）", "均衡（5）", "最高（9）" });
        options.Controls.Add(compression);
        deleteStaging.Text = "接收并校验后删除本机导出文件"; deleteStaging.AutoSize = true; options.Controls.Add(deleteStaging);
        actions.Controls.Add(options, 1, 2); actions.SetColumnSpan(options, 3);

        records.Dock = DockStyle.Fill;
        records.View = View.Details;
        records.FullRowSelect = true;
        records.HideSelection = false;
        records.MultiSelect = false;
        records.Columns.Add("接受端备份", 380);
        records.Columns.Add("源账号", 145);
        records.Columns.Add("格式", 65);
        records.Columns.Add("接收时间", 170);
        records.Columns.Add("保存大小", 100);
        main.Controls.Add(records, 0, 3);

        TableLayoutPanel import = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 7, RowCount = 2 };
        import.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 95));
        import.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        import.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 95));
        import.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        import.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 95));
        import.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        import.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
        import.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); import.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        main.Controls.Add(import, 0, 4);
        import.Controls.Add(Label("目标空账号"), 0, 0); target.Dock = DockStyle.Fill; import.Controls.Add(target, 1, 0);
        import.Controls.Add(Label("导入管理员"), 2, 0); admin.Dock = DockStyle.Fill; import.Controls.Add(admin, 3, 0);
        import.Controls.Add(Label("管理员密码"), 4, 0); adminPassword.UseSystemPasswordChar = true;
        adminPassword.Dock = DockStyle.Fill; import.Controls.Add(adminPassword, 5, 0);
        restore.Text = "还原到空账号"; restore.Dock = DockStyle.Fill; import.Controls.Add(restore, 6, 0);
        import.Controls.Add(Label("还原到当前任务的数据库；目标账号必须为空，管理员需有导入和查询权限。"), 0, 1);
        import.SetColumnSpan(import.GetControlFromPosition(0, 1), 7);

        TableLayoutPanel status = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        status.Margin = new Padding(3, 0, 3, 0);
        status.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 165));
        status.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        status.Controls.Add(progressLabel, 0, 0);
        progress.Dock = DockStyle.Fill;
        status.Controls.Add(progress, 1, 0);
        main.Controls.Add(status, 0, 5);
        log.Multiline = true; log.ReadOnly = true; log.ScrollBars = ScrollBars.Vertical;
        log.Dock = DockStyle.Fill; main.Controls.Add(log, 0, 6);

        foreach (Control control in new Control[] { taskName, server, dbHost, dbPort, service, user, password, schema,
                 directory, staging, oracleBin, target, admin, adminPassword, auto, deleteStaging, compression,
                 interval, scheduleMode, dailyTime, taskList, addTask, saveTask, deleteTask, backup, refresh, restore, importPair })
            editControls.Add(control);
        foreach (TextBox box in new TextBox[] { taskName, server, dbHost, dbPort, service, user, password, schema,
                                                directory, staging, oracleBin })
            box.TextChanged += delegate { MarkDirty(); };
        auto.CheckedChanged += delegate { MarkDirty(); UpdateScheduleControls(); };
        compression.SelectedIndexChanged += delegate { MarkDirty(); };
        deleteStaging.CheckedChanged += delegate
        {
            if (!loading && deleteStaging.Checked &&
                MessageBox.Show("接收端确认备份完整后，将删除本次数据库服务器上的原始导出文件。是否启用？",
                    "确认本机清理", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                loading = true; deleteStaging.Checked = false; loading = false;
            }
            MarkDirty();
        };
        interval.ValueChanged += delegate { MarkDirty(); };
        scheduleMode.SelectedIndexChanged += delegate { MarkDirty(); UpdateScheduleControls(); };
        dailyTime.ValueChanged += delegate { MarkDirty(); };
        taskList.SelectedIndexChanged += delegate
        {
            if (!loading && taskList.SelectedItems.Count == 1)
                BeginInvoke(new Action(delegate { SwitchSelection(); }));
        };
        addTask.Click += delegate { AddTask(); };
        saveTask.Click += delegate { try { if (!SwitchSelection()) return; SaveCurrent(); AddLog("任务设置已保存。"); TryDue(); } catch (Exception ex) { ShowError(ex); } };
        deleteTask.Click += delegate { DeleteTask(); };
        backup.Click += delegate { StartManualBackup(); };
        refresh.Click += delegate { RefreshBackups(); };
        importPair.Click += delegate { ImportPairing(); };
        restore.Click += delegate { StartRestore(confirmRestore ?? ConfirmRestore); };
        timer.Interval = 5000;
        timer.Tick += delegate { TryDue(); };
        FormClosing += delegate(object sender, FormClosingEventArgs e)
        {
            if (busy) { MessageBox.Show("任务进行中，请等待完成后关闭。"); e.Cancel = true; return; }
            try { SaveCurrent(); }
            catch (Exception ex) { ShowError(ex); e.Cancel = true; }
        };
        LoadTasks();
        if (startTimer) timer.Start();
        AddLog("请选择或新增任务；切换任务时会保存已修改的设置。暂存目录留空时自动选择。");
    }

    private static Label Label(string text)
    {
        return new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    }
    private static void Field(TableLayoutPanel panel, string title, TextBox box, int col, int row)
    {
        panel.Controls.Add(Label(title), col, row); box.Dock = DockStyle.Fill; panel.Controls.Add(box, col + 1, row);
    }
    private void MarkDirty()
    {
        if (loading) return;
        dirty = true;
        dirtyLabel.Text = "有未保存的修改";
    }
    private void UpdateScheduleControls()
    {
        bool scheduled = auto.Checked && !busy && storeReady;
        interval.Enabled = scheduled && scheduleMode.SelectedIndex == 0;
        dailyTime.Enabled = scheduled && scheduleMode.SelectedIndex == 1;
        scheduleMode.Enabled = scheduled;
    }
    private void AddLog(string message)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(new Action<string>(AddLog), message); } catch { } return; }
        log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine);
        Preferences.AppendLog("client", message);
    }
    private void SetProgress(string stage, int percent)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(new Action<string, int>(SetProgress), stage, percent); } catch (InvalidOperationException) { }
            return;
        }
        int value = Math.Min(100, percent);
        progressLabel.Text = value < 0 ? stage : stage + "  " + value + "%";
        progress.Style = value < 0 ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
        if (value >= 0) progress.Value = value;
    }
    private void ShowError(Exception ex) { MessageBox.Show(ex.Message, "操作未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning); }

    private void ImportPairing()
    {
        try
        {
            string host = server.Text.Trim();
            if (!Regex.IsMatch(host, @"^[A-Za-z0-9.-]{1,253}$"))
                throw new IOException("请先填写接受端 IP 或主机名。");
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "选择接受端导出的配对文件";
                dialog.Filter = "Oracle 配对文件 (*.obm-pair)|*.obm-pair";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                PairingStore.SaveClient(host, PairingKeys.Import(dialog.FileName));
                AddLog("已导入接受端 " + host + " 的配对信息。");
            }
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void LoadTasks()
    {
        try
        {
            List<BackupTask> loaded = store.Load();
            if (loaded == null || loaded.Count == 0)
            {
                loaded = new List<BackupTask> { BackupTask.NewTask("备份任务 1") };
                store.Save(loaded);
            }
            tasks = loaded;
            storeReady = true;
            selected = tasks[0];
            RebuildTaskList(selected.Id);
            LoadSelection();
        }
        catch (Exception ex)
        {
            storeReady = false;
            AddLog("无法读取或保存任务设置，计划已停止：" + ex.Message);
            MessageBox.Show("任务设置无法读取或解密，计划已停止。请检查当前 Windows 登录账号和任务文件：" +
                Environment.NewLine + store.FileName + Environment.NewLine + ex.Message, "任务设置错误");
            SetEditing(false);
        }
    }

    private static string PlanText(BackupTask t)
    {
        if (!t.Scheduled) return "未启用";
        return t.ScheduleMode == "daily" ? String.Format("每天 {0:00}:{1:00}", t.DailyMinute / 60, t.DailyMinute % 60)
                                         : "每 " + t.Hours + " 小时";
    }
    private void RebuildTaskList(string selectedId)
    {
        loading = true;
        taskList.BeginUpdate();
        taskList.Items.Clear();
        foreach (BackupTask t in tasks)
        {
            ListViewItem item = new ListViewItem(t.Name); item.Tag = t.Id;
            item.SubItems.Add(t.Host + "/" + t.Service);
            item.SubItems.Add(t.Schema);
            item.SubItems.Add(PlanText(t));
            item.SubItems.Add(t.Scheduled && t.NextUtc != DateTime.MinValue ? t.NextUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "—");
            item.SubItems.Add(t.LastUtc == DateTime.MinValue ? "—" : t.LastStatus + " " + t.LastUtc.ToLocalTime().ToString("MM-dd HH:mm"));
            taskList.Items.Add(item);
            if (t.Id == selectedId) item.Selected = true;
        }
        taskList.EndUpdate();
        loading = false;
    }
    private void LoadSelection()
    {
        if (selected == null) return;
        loading = true;
        taskName.Text = selected.Name; server.Text = selected.Server; dbHost.Text = selected.Host;
        dbPort.Text = selected.Port.ToString(); service.Text = selected.Service; user.Text = selected.User;
        password.Text = selected.Password; schema.Text = selected.Schema; directory.Text = selected.DirectoryObject;
        staging.Text = selected.Staging; oracleBin.Text = selected.Bin; interval.Value = selected.Hours;
        scheduleMode.SelectedIndex = selected.ScheduleMode == "daily" ? 1 : 0;
        dailyTime.Value = DateTime.Today.AddMinutes(selected.DailyMinute);
        auto.Checked = selected.Scheduled; deleteStaging.Checked = selected.DeleteStaging;
        compression.SelectedIndex = Array.IndexOf(CompressionLevels, selected.Compression);
        dirty = false; dirtyLabel.Text = ""; loading = false;
        UpdateScheduleControls();
    }
    private BackupTask FindTask(string id)
    {
        return tasks.Find(delegate(BackupTask t) { return t.Id == id; });
    }
    private bool SwitchSelection()
    {
        if (loading || taskList.SelectedItems.Count != 1) return true;
        string id = (string)taskList.SelectedItems[0].Tag;
        if (selected != null && id == selected.Id) return true;
        try
        {
            SaveCurrent();
            selected = FindTask(id);
            RebuildTaskList(id);
            LoadSelection();
            return true;
        }
        catch (Exception ex)
        {
            ShowError(ex);
            RebuildTaskList(selected == null ? null : selected.Id);
            return false;
        }
    }

    private void SaveCurrent()
    {
        if (!storeReady || selected == null || !dirty) return;
        BackupTask updated = selected.Clone();
        int port;
        if (!Int32.TryParse(dbPort.Text.Trim(), out port)) throw new Exception("数据库端口无效");
        updated.Name = taskName.Text.Trim(); updated.Server = server.Text.Trim(); updated.Host = dbHost.Text.Trim();
        updated.Port = port; updated.Service = service.Text.Trim(); updated.User = user.Text.Trim().ToUpperInvariant();
        updated.Password = password.Text; updated.Schema = schema.Text.Trim().ToUpperInvariant();
        updated.DirectoryObject = directory.Text.Trim().ToUpperInvariant();
        if (String.IsNullOrWhiteSpace(updated.DirectoryObject)) updated.DirectoryObject = BackupTask.DefaultDirectoryObject;
        updated.Staging = staging.Text.Trim(); updated.Bin = oracleBin.Text.Trim();
        updated.Hours = (int)interval.Value; updated.ScheduleMode = scheduleMode.SelectedIndex == 1 ? "daily" : "interval";
        updated.DailyMinute = dailyTime.Value.Hour * 60 + dailyTime.Value.Minute;
        updated.Scheduled = auto.Checked; updated.DeleteStaging = deleteStaging.Checked;
        updated.Compression = CompressionLevels[compression.SelectedIndex];
        updated.ValidateStorage();
        if (updated.Scheduled) updated.Options();
        TaskPlanner.UpdateSchedule(updated, selected, DateTime.UtcNow);
        List<BackupTask> next = new List<BackupTask>(tasks);
        int index = next.FindIndex(delegate(BackupTask t) { return t.Id == updated.Id; });
        next[index] = updated;
        store.Save(next);
        tasks = next; selected = updated; dirty = false; dirtyLabel.Text = "";
        RebuildTaskList(selected.Id);
    }
    private void AddTask()
    {
        try
        {
            if (!SwitchSelection()) return;
            SaveCurrent();
            BackupTask t = BackupTask.NewTask("备份任务 " + (tasks.Count + 1));
            if (selected != null) t.Server = selected.Server;
            List<BackupTask> next = new List<BackupTask>(tasks); next.Add(t); store.Save(next);
            tasks = next; selected = t; RebuildTaskList(t.Id); LoadSelection();
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private void DeleteTask()
    {
        if (!SwitchSelection()) return;
        if (selected == null) return;
        if (MessageBox.Show("删除任务“" + selected.Name + "”？已接收的备份文件不会删除。", "删除任务",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try
        {
            List<BackupTask> next = new List<BackupTask>(tasks);
            next.RemoveAll(delegate(BackupTask t) { return t.Id == selected.Id; });
            if (next.Count == 0) next.Add(BackupTask.NewTask("备份任务 1"));
            store.Save(next); tasks = next; selected = tasks[0]; RebuildTaskList(selected.Id); LoadSelection();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void SetEditing(bool enabled)
    {
        foreach (Control control in editControls) control.Enabled = enabled;
        if (enabled) UpdateScheduleControls();
    }
    private void RunAsync(string stage, Action work, Action<Exception> finished)
    {
        if (busy) return;
        busy = true; SetEditing(false); SetProgress(stage, -1);
        Thread thread = new Thread(delegate()
        {
            Exception failure = null;
            try { work(); }
            catch (Exception ex) { failure = ex; AddLog("错误：" + ex.Message); }
            finally
            {
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        busy = false; SetEditing(storeReady);
                        progress.Style = ProgressBarStyle.Continuous;
                        progress.Value = failure == null ? 100 : 0;
                        progressLabel.Text = failure == null ? "已完成" : "失败，详见日志";
                        if (finished != null) finished(failure);
                        TryDue();
                    }));
                }
                catch { }
            }
        });
        thread.IsBackground = true; thread.Start();
    }
    private void CompleteTask(string id, Exception failure)
    {
        BackupTask task = FindTask(id);
        if (task == null) return;
        BackupTask updated = task.Clone();
        TaskPlanner.Completed(updated, DateTime.UtcNow, failure == null);
        List<BackupTask> next = new List<BackupTask>(tasks);
        int index = next.FindIndex(delegate(BackupTask t) { return t.Id == id; });
        next[index] = updated;
        try
        {
            store.Save(next); tasks = next;
            if (selected != null && selected.Id == id) selected = updated;
            RebuildTaskList(selected == null ? null : selected.Id);
        }
        catch (Exception ex)
        {
            storeReady = false; timer.Stop(); SetEditing(false);
            AddLog("任务结果无法保存，定时计划已停止：" + ex.Message);
        }
    }
    private void TryDue()
    {
        if (busy || !storeReady) return;
        BackupTask due = TaskPlanner.NextDue(tasks, DateTime.UtcNow);
        if (due != null) StartTask(due.Clone(), true);
    }
    private void StartManualBackup()
    {
        try { if (!SwitchSelection()) return; SaveCurrent(); if (selected != null) StartTask(selected.Clone(), false); }
        catch (Exception ex) { ShowError(ex); }
    }
    private void StartTask(BackupTask task, bool scheduled)
    {
        OracleOptions options;
        try { options = task.Options(); }
        catch (Exception ex)
        {
            AddLog("[" + task.Name + "] 参数错误：" + ex.Message);
            CompleteTask(task.Id, ex);
            return;
        }
        AddLog("[" + task.Name + "] " + (scheduled ? "计划开始执行。" : "手动开始执行。"));
        Action<string> taskLog = delegate(string message) { AddLog("[" + task.Name + "] " + message); };
        RunAsync("导出数据库", delegate
        {
            PairingStore.GetClient(task.Server);
            ExportedSet set = OracleWork.Export(options, taskLog);
            string sendFile = set.Dump;
            DumpProof original = null;
            try
            {
                sendFile = BackupPacking.Prepare(set.Dump, set.Id, task.Compression, SetProgress, taskLog, out original);
                string backupId = set.Id + (original != null ? Wire.SevenZipSuffix : "");
                string phase = "发送备份";
                SetProgress(phase, 0);
                taskLog("开始加密发送到接受端。");
                BackupNetworkClient.Upload(task.Server, Wire.DefaultPort, backupId, options.Schema,
                    sendFile, set.Log, percent => SetProgress(phase, percent), original, delegate(string message)
                    {
                        taskLog(message);
                        phase = "接受端校验";
                        SetProgress(phase, 0);
                    });
                taskLog("接受端已校验并保存备份 " + backupId);
                if (task.DeleteStaging)
                {
                    try { File.Delete(set.Dump); File.Delete(set.Log); taskLog("已删除本机原始导出文件。"); }
                    catch (Exception ex) { taskLog("备份已接收，但本机清理失败：" + ex.Message); }
                }
            }
            finally
            {
                if (original != null) try { File.Delete(sendFile); } catch (Exception ex) { taskLog("压缩临时文件清理失败：" + ex.Message); }
            }
        }, delegate(Exception failure) { CompleteTask(task.Id, failure); });
    }
    private void RefreshBackups()
    {
        try
        {
            if (!SwitchSelection()) return;
            SaveCurrent();
            string host = selected.Server;
            if (!Regex.IsMatch(host ?? "", @"^[A-Za-z0-9.-]{1,253}$")) throw new Exception("请填写接收端 IP 或主机名");
            List<BackupRecord> result = null;
            RunAsync("读取备份列表", delegate { result = BackupNetworkClient.List(host, Wire.DefaultPort); },
                delegate(Exception failure)
                {
                    if (result == null) return;
                    records.Items.Clear();
                    recordsServer = host;
                    foreach (BackupRecord r in result)
                    {
                        ListViewItem row = new ListViewItem(r.Id); row.Tag = r;
                        row.SubItems.Add(r.Schema);
                        row.SubItems.Add(Wire.FormatName(r.Id));
                        row.SubItems.Add(r.Utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
                        row.SubItems.Add(Wire.FormatSize(r.DumpSize));
                        records.Items.Add(row);
                    }
                    AddLog("已获取 " + result.Count + " 个完整备份。");
                });
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private static bool ConfirmRestore(string message)
    {
        return MessageBox.Show(message, "确认还原", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
    }

    private void StartRestore(Func<string, bool> confirm)
    {
        try
        {
            if (!SwitchSelection()) return;
            SaveCurrent();
            if (records.SelectedItems.Count != 1) throw new Exception("请先选择一个备份");
            BackupRecord record = (BackupRecord)records.SelectedItems[0].Tag;
            BackupTask task = selected.Clone();
            if (!String.Equals(recordsServer, task.Server, StringComparison.OrdinalIgnoreCase))
                throw new Exception("当前任务的接收端已改变，请先重新刷新备份列表");
            OracleOptions options = task.Options();
            options.User = admin.Text.Trim().ToUpperInvariant(); options.Password = adminPassword.Text; options.Validate();
            string destinationSchema = target.Text.Trim().ToUpperInvariant();
            if (!Wire.SchemaPattern.IsMatch(destinationSchema)) throw new Exception("请输入目标空账号");
            if (!confirm("将把 " + record.Schema + " 的备份导入 " + options.Host + "/" + options.Service +
                " 的空账号 " + destinationSchema + "。程序将检查目标账号。导入会修改数据库，是否继续？")) return;
            RunAsync("检查目标数据库", delegate
            {
                PairingStore.GetClient(task.Server);
                OracleWork.CheckEmptyTarget(options, destinationSchema, AddLog);
                OracleWork.CheckDirectory(options, AddLog);
                string name = record.Id + "_RESTORE_" + Guid.NewGuid().ToString("N").Substring(0, 8).ToUpperInvariant() + ".dmp";
                string local = Path.Combine(options.Staging, name);
                string downloaded = local + ".download";
                try
                {
                    AddLog("正在下载并校验备份。");
                    SetProgress("下载备份", 0);
                    DumpProof original = BackupNetworkClient.Download(task.Server, Wire.DefaultPort, record.Id, 1,
                        downloaded, percent => SetProgress("下载备份", percent));
                    if (Wire.IsCompressed(record.Id)) SetProgress("解压并校验", 0);
                    BackupPacking.Materialize(downloaded, local, record.Id, original,
                        percent => SetProgress("解压并校验", percent), AddLog);
                    SetProgress("检查目标账号", -1);
                    OracleWork.CheckEmptyTarget(options, destinationSchema, AddLog);
                    SetProgress("导入到目标数据库", -1);
                    string importLog = OracleWork.Restore(options, record.Schema, destinationSchema, local, AddLog);
                    AddLog("导入命令结束。请核对数据及导入日志：" + importLog);
                    try { File.Delete(local); }
                    catch (Exception ex) { AddLog("导入完成，临时还原文件清理失败：" + ex.Message); }
                }
                finally { try { if (File.Exists(downloaded)) File.Delete(downloaded); } catch { } }
            }, null);
        }
        catch (Exception ex) { ShowError(ex); }
    }
}

internal static class ClientEntry
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        ClientInstanceLock instance;
        try { instance = new ClientInstanceLock(Preferences.Folder); }
        catch (Exception)
        {
            MessageBox.Show("客户端可能已经运行，或无法访问任务目录。请检查已打开的客户端和本机目录权限。",
                "无法启动", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        using (instance) Application.Run(new ClientForm());
    }
}
