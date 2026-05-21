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

            // Extract activeProjectId
            int? activeProjectId = ExtractProjectId(values[0]);

            // Extract projectId  
            int? projectId = ExtractProjectId(values[1]);

            // If no active project, check parameter
            if (activeProjectId == null)
            {
                if (parameter is string str && str.Equals("Invert", StringComparison.OrdinalIgnoreCase))
                {
                    return true; // No active project, show buttons/non-active UI
                }
                return false; // No active project, don't show active indicator
            }

            if (projectId == null)
                return false;

            var match = activeProjectId == projectId;
            if (parameter is string str2 && str2.Equals("Invert", StringComparison.OrdinalIgnoreCase))
            {
                return !match;
            }
            return match;
        }

        private int? ExtractProjectId(object? value)
        {
            if (value == null)
                return null;

            if (value is int intVal)
                return intVal;

            // Handle nullable int by converting through the type system
            var valueType = value.GetType();
            if (valueType.IsGenericType && valueType.GetGenericTypeDefinition() == typeof(Nullable<>))
            {
                return (int?)value;
            }

            return null;
        }

        public IList<object?> ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
