using System;
using System.Windows.Forms;

namespace ModeSwitcher;

static class Program
{
    [STAThread]
    static void Main()
    {
        try
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            MessageBox.Show($"启动失败:\n{ex}", "ModeSwitcher Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
