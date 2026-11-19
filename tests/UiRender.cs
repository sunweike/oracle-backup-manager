using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

internal static class UiRender
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Preferences.TestFolder = Path.Combine(Path.GetTempPath(), "OracleBackupUi_" + Guid.NewGuid().ToString("N"));
        using (Form server = new ServerForm())
        using (Form client = new ClientForm())
        {
            Render(server, Path.Combine(args[0], "server-ui.png"));
            Render(client, Path.Combine(args[0], "client-ui.png"));
        }
    }
    private static void Render(Form form, string path)
    {
        form.ShowInTaskbar = false;
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-20000, -20000);
        form.Show();
        Application.DoEvents();
        form.PerformLayout();
        using (Bitmap image = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(image, new Rectangle(0, 0, image.Width, image.Height));
            image.Save(path);
        }
        form.Hide();
    }
}
