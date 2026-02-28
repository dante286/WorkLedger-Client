using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace WorkLedger_Client.Converters
{
    public class UtcToLocalTimeConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is DateTime dateTime)
            {
                var localTime = dateTime.ToLocalTime();
                if (parameter is string format)
                {
                    return localTime.ToString(format, culture);
                }
                return localTime;
            }
            return value;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is DateTime dateTime)
            {
                return dateTime.ToUniversalTime();
            }
            return value;
        }
    }
}
