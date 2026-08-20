using System;
using System.Globalization;
using Avalonia.Data.Converters;
using WorkLedger_Client.Services;

namespace WorkLedger_Client.Converters
{
    public class SyncIntervalToDisplayConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is SyncInterval interval)
            {
                return interval switch
                {
                    // Not "Disabled": sync itself stays on, only the background tick stops.
                    SyncInterval.Disabled => "Manual only",
                    SyncInterval.FiveMinutes => "Every 5 minutes",
                    SyncInterval.FifteenMinutes => "Every 15 minutes",
                    SyncInterval.OneHour => "Every hour",
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
