using System;
using System.Windows;
using System.Windows.Controls;

namespace MelodyLauncher.Views
{
    public partial class MessageOverlay : UserControl
    {
        public event EventHandler Dismissed;

        public MessageOverlay()
        {
            InitializeComponent();
            try { var __mc = MelodyLauncher.Services.AppSettings.Load().McFolder;  } catch { }
            
            BtnClose.Click += (s, e) => Dismissed?.Invoke(this, EventArgs.Empty);
            BtnConfirm.Click += (s, e) => Dismissed?.Invoke(this, EventArgs.Empty);
        }

        public void Show(string title, string body)
        {
            TextTitle.Text = title;
            TextBody.Text = body;
            Visibility = Visibility.Visible;
        }
    }
}
