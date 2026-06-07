using System;
using System.Globalization;
using Avalonia.Data.Converters;
using PCCCImageTool.Models;

namespace PCCCImageTool.Converters;

public class EnumToBooleanConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null || parameter == null) return false;
        
        // parameter is expected to be string like "Df1FullDuplex", "Df1HalfDuplex" or "Eip"
        string paramStr = parameter.ToString()!;
        if (Enum.TryParse(typeof(TransportType), paramStr, true, out var enumValue))
        {
            return value.Equals(enumValue);
        }
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool boolValue && boolValue && parameter != null)
        {
            string paramStr = parameter.ToString()!;
            if (Enum.TryParse(typeof(TransportType), paramStr, true, out var enumValue))
                return enumValue;
        }
        return null;
    }
}
