using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using System;
using System.Threading.Tasks;
using WorkLedger_Client.Views;
using Avalonia.Platform;
using Avalonia.Controls;
using Avalonia.Threading;

namespace WorkLedger_Client.Services
{
    public class ToastService
    {
        private readonly IClassicDesktopStyleApplicationLifetime _desktop;

        public ToastService(IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop ?? throw new ArgumentNullException(nameof(desktop));
        }

        public void ShowToast(string message, int durationMs = 1400)
        {
            // Run on UI thread
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    var toast = new ToastWindow();
                    toast.SetMessage(message);

                    // Position bottom-right of main window with small margin
                    var main = _desktop.MainWindow;
                    if (main != null)
                    {
                        var bounds = main.Bounds;
                        // Compute position: bottom-right inside main window
                        var px = (int)(bounds.X + bounds.Width - toast.Width - 24);
                        var py = (int)(bounds.Y + bounds.Height - toast.Height - 48);
                        toast.Position = new PixelPoint(px, py);
                    }

                    // Non-blocking show and auto-close
                    await toast.ShowTemporarily(durationMs);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Toast error: {ex.Message}");
                }
            });
        }
    }
}
