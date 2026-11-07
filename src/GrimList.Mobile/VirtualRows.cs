using System.Globalization;
using Microsoft.Maui.Controls;

namespace GrimList.Mobile;

internal static class VirtualRows
{
    // CollectionView owns realization/recycling. ItemsSource contains data only;
    // never store prebuilt Views in it, which defeats deferred row construction.
    internal static DataTemplate Template<T>(Func<T, View> render, string path = ".") => new(() =>
    {
        var host = new ContentView();
        host.SetBinding(ContentView.ContentProperty, new Binding(path, converter: new Projection<T>(render)));
        return host;
    });
    private sealed class Projection<T>(Func<T, View> render) : IValueConverter
    {
        public object? Convert(object? value, Type type, object? parameter, CultureInfo culture) => value is T row ? render(row) : null;
        public object ConvertBack(object? value, Type type, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
