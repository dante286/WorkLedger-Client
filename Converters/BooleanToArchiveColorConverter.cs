using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace WorkLedger_Client.Converters
{
    public class BooleanToArchiveColorConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool isArchived = value is bool b && b;
            
            if (isArchived)
            {
                // Return Teal color when archived
                var app = Avalonia.Application.Current;
                if (app != null && app.TryGetResource("TealActive", Avalonia.Styling.ThemeVariant.Default, out var tealObj) && tealObj is IBrush tealBrush)
                    return tealBrush;
                return Brushes.Teal;
            }
            else
            {
                // Return Tertiary text color when not archived
                var app = Avalonia.Application.Current;
                if (app != null && app.TryGetResource("TextTertiary", Avalonia.Styling.ThemeVariant.Default, out var tertiaryObj) && tertiaryObj is IBrush tertiaryBrush)
                    return tertiaryBrush;
                return Brushes.Gray;
            }
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
