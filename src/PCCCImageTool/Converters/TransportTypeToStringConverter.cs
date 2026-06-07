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
            return type switch
            {
                TransportType.Df1FullDuplex => "DF1 Full Duplex",
                TransportType.Df1HalfDuplex => "DF1 Half Duplex",
                TransportType.Eip => "EtherNet/IP",
                _ => value.ToString()
            };
        }
        return value?.ToString();
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
