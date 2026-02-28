using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace WorkLedger_Client.Converters
{
    public class SecondsToDurationConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is long seconds)
            {
                return TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss");
            }
            if (value is int intSeconds)
            {
                return TimeSpan.FromSeconds(intSeconds).ToString(@"hh\:mm\:ss");
            }
            return "00:00:00";
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
