using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace WorkLedger_Client.Converters
{
    public class BooleanToVisibilityConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool boolValue = value is bool && (bool)value;
            
            // Check if parameter is "Invert" to reverse the logic
            bool invert = parameter?.ToString()?.ToLower() == "invert";
            
            if (invert)
                boolValue = !boolValue;
                
            return boolValue;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
