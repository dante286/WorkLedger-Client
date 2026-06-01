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
                Avalonia.Application.Current.TryGetResource("TealActive", Avalonia.Styling.ThemeVariant.Default, out var teal);
                return teal ?? Brushes.Teal;
            }
            else
            {
                // Return Tertiary text color when not archived
                Avalonia.Application.Current.TryGetResource("TextTertiary", Avalonia.Styling.ThemeVariant.Default, out var tertiary);
                return tertiary ?? Brushes.Gray;
            }
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
