using System;
using System.IO;
using System.Linq;
using System.Windows;
using MelodyLauncher.Views;

namespace MelodyLauncher
{
    public partial class App : Application
    {
        private static void LogCrash(Exception ex)
        {
            try
            {
                var dir = MelodyLauncher.Services.AppPaths.DataDir;
                Directory.CreateDirectory(dir);
                File.AppendAllText(MelodyLauncher.Services.AppPaths.CrashLogFile,
                    "==== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ====\r\n" + ex + "\r\n\r\n");
            }
            catch { }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += (s, ev) =>
            {
                LogCrash(ev.Exception);
                MessageBox.Show("程序遇到错误：" + ev.Exception.Message, "错误");
                ev.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
            {
                if (ev.ExceptionObject is Exception ex) LogCrash(ex);
            };
            TaskScheduler.UnobservedTaskException += (s, ev) =>
            {
                LogCrash(ev.Exception);
                ev.SetObserved();
            };
            base.OnStartup(e);
            var args = Environment.GetCommandLineArgs();
            bool forceFirstRun = args.Contains("--first-run");
            bool skipFirstRun = args.Contains("--skip-firstrun");
            string settingsPath = MelodyLauncher.Services.AppPaths.SettingsFile;
            bool freshSettings = !File.Exists(settingsPath);
            if (freshSettings)
                MelodyLauncher.Services.AppPaths.CreateDefaultGameDirIfMissing();
            if (args.Contains("--auto-setup"))
            {
                var s = MelodyLauncher.Services.AppSettings.Load();
                var found = MelodyLauncher.Services.JavaScanner.FindJava();
                if (found.Count > 0) s.JavaPath = found[0];
                if (string.IsNullOrWhiteSpace(s.McFolder)) s.McFolder = MelodyLauncher.Services.AppPaths.DefaultMcFolder;
                try { System.IO.Directory.CreateDirectory(s.McFolder); } catch { }
                s.Save();
                skipFirstRun = true;
            }
            bool firstRun = forceFirstRun || !File.Exists(settingsPath);
            if (firstRun && !skipFirstRun)
            {
                var win = new FirstRunWindow();
                win.Completed += (s, ev) =>
                {
                    var main = new MainWindow();
                    MainWindow = main;
                    main.Show();
                };
                MainWindow = win;
                win.Show();
            }
            else
            {
                var main = new MainWindow();
                MainWindow = main;
                main.Show();
            }
        }
    }
}
