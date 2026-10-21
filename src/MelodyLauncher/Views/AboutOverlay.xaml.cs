using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using MelodyLauncher.Services;
using MelodyLauncher.Services.Bridge;

namespace MelodyLauncher.Views
{
    public partial class AboutOverlay : UserControl
    {
        public event EventHandler CloseRequested;

        public AboutOverlay()
        {
            InitializeComponent();
            BtnClose.Click += (s, e) => CloseRequested?.Invoke(this, EventArgs.Empty);
            BtnOpenData.Click += (s, e) => Open(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MelodyLauncher"));
            BtnOpenMc.Click += (s, e) => Open(AppSettings.Load().McFolder);
            Refresh();
        }

        public void Refresh()
        {
            var asm = typeof(AboutOverlay).Assembly.GetName().Version;
            var ver = asm == null ? "5.31.0.0" : $"{asm.Major}.{asm.Minor}.0.0";
            TextAbout.Text =
                "MCLauncher 5.31（旋律启动器复刻版）\n" +
                "前端：.NET 8 WPF 全新实现（非 引擎 换皮）\n" +
                "内核：MCLauncher 5.31 无头子进程（协议 v1 / build 1）\n" +
                "作者：忘却的旋律\n" +
                "版本：" + ver + "   构建：" + File.GetLastWriteTime(typeof(AboutOverlay).Assembly.Location).ToString("yyyy-MM-dd") + "\n" +
                "说明：界面复刻自 2014 年旋律启动器，功能由内置内核提供。";

            var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MelodyLauncher");
            var localAppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MelodyLauncher");
            TextSettingsPath.Text = Path.Combine(appData, "settings.json");
            TextApiPath.Text = Path.Combine(appData, "Api.json");
            TextEnginePath.Text = EngineLocator.Resolve();
            TextJobsPath.Text = Path.Combine(localAppData, "jobs");
            TextMcPath.Text = AppSettings.Load().McFolder;
        }

        private void Open(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
            }
            catch { }
        }
    }
}
