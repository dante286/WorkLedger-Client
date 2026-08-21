using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WorkLedger_Client.Models;
using WorkLedger_Client.Services;

namespace WorkLedger_Client.ViewModels
{
    // The Reports tab (plan 7 items 7-8). Read-only and entirely local: it is derived from
    // LocalDbService on demand, so it works with sync switched off and adds no schema.
    public partial class MainWindowViewModel
    {
        // Rebuilt wholesale on each refresh rather than mutated in place - the report is a
        // snapshot of a query, and swapping the object is what tells the view every total
        // beneath it changed.
        [ObservableProperty]
        private WeeklyReport? _weeklyReport;

        [ObservableProperty]
        private bool _isBuildingReport;

        [RelayCommand]
        private Task RefreshReportAsync() => LoadWeeklyReportAsync();

        public async Task LoadWeeklyReportAsync()
        {
            if (IsBuildingReport)
                return;

            IsBuildingReport = true;
            try
            {
                WeeklyReport = await _reportsService.BuildWeeklyReportAsync(DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to build the weekly report: {ex.Message}");
                _toastService?.ShowToast("Failed to build the weekly report", 2500);
            }
            finally
            {
                IsBuildingReport = false;
            }
        }

        [RelayCommand]
        private async Task ExportReportCsvAsync()
        {
            var report = WeeklyReport;
            if (report == null || report.IsEmpty)
            {
                _toastService?.ShowToast("Nothing to export for this week", 2000);
                return;
            }

            var desktop = Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            if (desktop?.MainWindow == null) return;

            try
            {
                var topLevel = TopLevel.GetTopLevel(desktop.MainWindow);
                if (topLevel?.StorageProvider == null)
                    return;

                var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Export weekly report",
                    SuggestedFileName = $"workledger-week-{report.WeekStartUtc:yyyy-MM-dd}.csv",
                    DefaultExtension = "csv",
                    FileTypeChoices = new[]
                    {
                        new FilePickerFileType("CSV") { Patterns = new[] { "*.csv" } }
                    }
                });

                if (file == null) return;

                // WriteToStreamAsync rather than File.WriteAllText: on a sandboxed platform
                // the picked file may have no usable local path, and the stream the picker
                // hands back is the only way to write to it.
                await using var stream = await file.OpenWriteAsync();
                await using var writer = new StreamWriter(stream);
                await writer.WriteAsync(ReportsService.ToCsv(report));

                _toastService?.ShowToast("Report exported", 1800);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Report export failed: {ex.Message}");
                _toastService?.ShowToast($"Export failed: {ex.Message}", 3000);
            }
        }
    }
}
