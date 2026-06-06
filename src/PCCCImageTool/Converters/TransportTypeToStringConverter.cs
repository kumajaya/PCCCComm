using System;
using System.Globalization;
using Avalonia.Data.Converters;
using PCCCImageTool.Models;

namespace PCCCImageTool.Converters;

public class TransportTypeToStringConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is TransportType type)
        {
            return type == TransportType.Df1Serial ? "DF1 Serial" : "EtherNet/IP";
        }
        return value?.ToString();
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}