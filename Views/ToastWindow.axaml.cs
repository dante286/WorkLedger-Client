using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using System;
using System.Threading.Tasks;

namespace WorkLedger_Client.Views
{
    public partial class ToastWindow : Window
    {
        public ToastWindow()
        {
            Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
        }

        public void SetMessage(string message)
        {
            var tb = this.FindControl<TextBlock>("MessageText");
            if (tb != null) tb.Text = message;
        }

        public async Task ShowTemporarily(int milliseconds)
        {
            this.Show();
            await Task.Delay(milliseconds);
            this.Close();
        }
    }
}
