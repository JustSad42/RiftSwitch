using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace RiftSwitch;

/// <summary>true → Collapsed, false → Visible.</summary>
sealed class InverseVisibility : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>null or empty text → Collapsed.</summary>
sealed class NullToVisibility : IValueConverter
{
    public object Convert(object? value, Type t, object p, CultureInfo c) =>
        value == null || value is string { Length: 0 } ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}
