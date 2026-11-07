using GrimList.Application;
using System.Globalization;
using Microsoft.Maui.Controls;

namespace GrimList.Mobile;

// Presentation projections only; every edit still goes through ArmySession.
internal sealed class PresentationValue<T>(T value) : ObservableValue<T>(value)
{
    public void Bind(BindableObject target, BindableProperty property, Func<T, object?> project)
        => target.SetBinding(property, new Binding(nameof(Value), source: this, converter: new Projection(project)));
    private sealed class Projection(Func<T, object?> project) : IValueConverter
    {
        public object? Convert(object? value, Type type, object? parameter, CultureInfo culture) => value is T current ? project(current) : null;
        public object ConvertBack(object? value, Type type, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
