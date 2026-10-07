using System;
using System.Windows.Data;
using System.Globalization;
using ExplorerTabUtility.Localization;

namespace ExplorerTabUtility.UI.Converters;

/// <summary>
/// Converts an enum value to its localized display name (see <see cref="Loc.GetEnumName"/>).
/// Non-enum values are returned as their string representation.
/// </summary>
public class EnumDisplayNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            null => string.Empty,
            Enum enumValue => Loc.GetEnumName(enumValue),
            _ => value.ToString() ?? string.Empty
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}
