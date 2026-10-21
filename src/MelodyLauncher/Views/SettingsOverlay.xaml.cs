using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using MelodyLauncher.Services;

namespace MelodyLauncher.Views
{
    public partial class SettingsOverlay : UserControl
    {
        public event EventHandler CloseRequested;
        public event EventHandler SettingsSaved;
        public event EventHandler<string> MessageRequested;
        public event EventHandler AccountRequested;

        private readonly AppSettings _settings = AppSettings.Load();

        public SettingsOverlay()
        {
            InitializeComponent();
            BtnPanelClose.Click += (s, e) => CloseRequested?.Invoke(this, EventArgs.Empty);
            BtnSaveSettings.Click += (s, e) => Save();
            BtnBrowseJava.Click += (s, e) => BrowseJava();
            BtnFindJava.Click += (s, e) => FindJava();
            BtnTestAuth.Click += (s, e) => AccountRequested?.Invoke(this, EventArgs.Empty);
            BtnDeleteAuth.Click += (s, e) => Message("删除正版信息", "当前未保存任何正版登录凭据。");
            BtnDeleteGame.Click += (s, e) => Message("删除选中游戏", "版本删除功能将在后续版本提供。");
            BtnRename.Click += (s, e) => Message("重命名", "版本重命名功能将在后续版本提供。");
            RadioManualMem.Checked += (s, e) => EditMemory.IsEnabled = true;
            RadioAutoMem.Checked += (s, e) => EditMemory.IsEnabled = false;
            LoadSettings();
        }

        public void LoadSettings()
        {
            var s = AppSettings.Load();
            EditUser.Text = s.OfflineName;
            EditPass.Text = "";
            RadioAutoMem.IsChecked = true;
            RadioManualMem.IsChecked = false;
            EditMemory.IsEnabled = false;
            EditMemory.Text = s.MemoryMB.ToString();
            RadioRandomSkin.IsChecked = true;
            EditJava.Text = s.JavaPath;
            CheckDebug.IsChecked = false;
            RadioDirMc.IsChecked = true;
            LoadGameList(s.McFolder);
        }

        private void LoadGameList(string mcFolder)
        {
            ComboGameList.Items.Clear();
            try
            {
                var dir = Path.Combine(mcFolder, "versions");
                if (Directory.Exists(dir))
                    foreach (var d in Directory.GetDirectories(dir))
                    {
                        var id = Path.GetFileName(d);
                        if (File.Exists(Path.Combine(d, id + ".json")))
                            ComboGameList.Items.Add(id);
                    }
            }
            catch { }
            if (ComboGameList.Items.Count > 0) ComboGameList.SelectedIndex = 0;
        }

        private void Message(string title, string body) => MessageRequested?.Invoke(this, title + "\u0001" + body);

        private void BrowseJava()
        {
            var dlg = new OpenFileDialog
            {
                Filter = "javaw.exe|javaw.exe|可执行文件|*.exe",
                Title = "选择 Java"
            };
            if (dlg.ShowDialog() == true) EditJava.Text = dlg.FileName;
        }

        private void FindJava()
        {
            var found = JavaScanner.FindJava();
            if (found.Count == 0)
            {
                Message("查找 Java", "未在常见位置找到 Java，请手动浏览选择 javaw.exe。");
                return;
            }
            EditJava.Text = found[0];
            Message("查找 Java", "找到 " + found.Count + " 个 Java：\n" + string.Join("\n", found));
        }

        private void Save()
        {
            var s = AppSettings.Load();
            s.OfflineName = string.IsNullOrWhiteSpace(EditUser.Text) ? "Player" : EditUser.Text.Trim();
            if (int.TryParse(EditMemory.Text, out var mem) && mem >= 512) s.MemoryMB = mem;
            s.JavaPath = EditJava.Text.Trim();
            if (ComboGameList.SelectedItem is string ver) s.LastVersion = ver;
            s.Save();
            SettingsSaved?.Invoke(this, EventArgs.Empty);
            Message("保存设置", "设置已保存。");
        }
    }
}
