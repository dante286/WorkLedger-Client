using System;
using System.Globalization;
using Avalonia.Data.Converters;
using WorkLedger_Client.Services;

namespace WorkLedger_Client.Converters
{
    // "File"/"Api" are wire-ish enum names; the settings screen says what they actually do.
    // Mirrors BackupIntervalToDisplayConverter.
    public class SyncModeToDisplayConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is SyncMode mode)
            {
                return mode switch
                {
                    SyncMode.Off => "Off",
                    SyncMode.File => "Shared folder",
                    SyncMode.Api => "WorkLedger API",
                    _ => mode.ToString()
                };
            }
            return value;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
