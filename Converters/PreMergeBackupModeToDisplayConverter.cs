using System;
using System.Globalization;
using Avalonia.Data.Converters;
using WorkLedger_Client.Services;

namespace WorkLedger_Client.Converters
{
    public class PreMergeBackupModeToDisplayConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is PreMergeBackupMode mode)
            {
                return mode switch
                {
                    PreMergeBackupMode.Never => "Never",
                    PreMergeBackupMode.OncePerSession => "Once per session",
                    PreMergeBackupMode.Always => "Before every merge",
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
