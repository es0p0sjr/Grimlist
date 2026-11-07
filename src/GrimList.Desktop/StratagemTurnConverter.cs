using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using GrimList.Core;

namespace GrimList.Desktop;

public sealed class StratagemTurnConverter : IValueConverter
{
    private static SolidColorBrush Brush(string colour) { var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(colour)!; brush.Freeze(); return brush; }
    private static readonly Brush Green = Brush("#276442"), Blue = Brush("#245C91"), Red = Brush("#913E43"), Neutral = Brush("#485154");
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var turn = StratagemTiming.FromText(value as string ?? "");
        if (parameter as string == "Label") return turn switch
        {
            StratagemTurn.Either => "Either turn", StratagemTurn.Yours => "Your turn", StratagemTurn.Opponents => "Opponent's turn", _ => "See timing below"
        };
        return turn switch { StratagemTurn.Either => Green, StratagemTurn.Yours => Blue, StratagemTurn.Opponents => Red, _ => Neutral };
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
