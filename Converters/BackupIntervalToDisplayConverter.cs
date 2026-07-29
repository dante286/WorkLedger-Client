using System;
using System.Globalization;
using Avalonia.Data.Converters;
using WorkLedger_Client.Services;

namespace WorkLedger_Client.Converters
{
    public class BackupIntervalToDisplayConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is BackupInterval interval)
            {
                return interval switch
                {
                    BackupInterval.Disabled => "Disabled",
                    BackupInterval.OneMinute => "1 minute",
                    BackupInterval.FiveMinutes => "5 minutes",
                    BackupInterval.OneHour => "1 hour",
                    _ => interval.ToString()
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
