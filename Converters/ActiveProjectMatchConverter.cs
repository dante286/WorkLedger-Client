using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;

namespace WorkLedger_Client.Converters
{
    public class ActiveProjectMatchConverter : IMultiValueConverter
    {
        public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values.Count < 2)
                return false;

            if (values[0] is int activeProjectId && values[1] is int projectId)
            {
                var match = activeProjectId == projectId;
                if (parameter is string str && str.Equals("Invert", StringComparison.OrdinalIgnoreCase))
                {
                    return !match;
                }
                return match;
            }
            if (values[0] == null && values[1] is int)
            {
                return false;
            }

            return false;
        }

        public IList<object?> ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
