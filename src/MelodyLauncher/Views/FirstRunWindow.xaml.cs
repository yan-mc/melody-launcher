using System;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using MelodyLauncher.Services;

namespace MelodyLauncher.Views
{
    public partial class FirstRunWindow : Window
    {
        public event EventHandler Completed;

        public FirstRunWindow()
        {
            InitializeComponent();
            BtnBrowse.Click += (s, e) => Browse();
            BtnDone.Click += (s, e) => Done();
            MouseLeftButtonDown += (s, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed)
                {
                    try { DragMove(); } catch { }
                }
            };
            Loaded += (s, e) =>
            {
                var found = JavaScanner.FindJava();
                if (found.Count > 0) EditJava.Text = found[0];
                else TextHint.Text = "未自动找到 Java，安装 MC 时会自动下载";
                EditName.Focus();
            };
        }

        private void Browse()
        {
            var dlg = new OpenFileDialog { Filter = "javaw.exe|javaw.exe|可执行文件|*.exe", Title = "选择 Java" };
            if (dlg.ShowDialog() == true) EditJava.Text = dlg.FileName;
        }

        private void Done()
        {
            var s = AppSettings.Load();
            s.OfflineName = string.IsNullOrWhiteSpace(EditName.Text) ? "Player" : EditName.Text.Trim();
            if (int.TryParse(EditMemory.Text, out var mem) && mem >= 512) s.MemoryMB = mem;
            s.JavaPath = EditJava.Text.Trim();
            s.Save();
            Completed?.Invoke(this, EventArgs.Empty);
            Close();
        }
    }
}
