using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;

namespace WorkLedger_Client.Converters
{
    public class ActiveTaskToTagConverter : IMultiValueConverter
    {
        public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values.Count >= 2 && values[0] is int activeTaskId && values[1] is int taskId)
            {
                if (activeTaskId == taskId)
                {
                    return "Active";
                }
            }
            return null;
        }
    }
}
