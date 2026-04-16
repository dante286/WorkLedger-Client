using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace WorkLedger_Client.Converters
{
    public class BooleanToChevronConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool isExpanded)
            {
                return isExpanded ? "▴" : "▾";
            }
            return "▾";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
