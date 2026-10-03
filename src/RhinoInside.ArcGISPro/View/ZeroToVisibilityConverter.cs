using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace RhinoInside.ArcGISPro
{
    /// <summary>Visible when a count is zero -- for "nothing here yet" hints under empty lists.</summary>
    public sealed class ZeroToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var n = value is int i ? i : System.Convert.ToInt32(value ?? 0, CultureInfo.InvariantCulture);
            return n == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
