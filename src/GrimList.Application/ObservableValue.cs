using System.ComponentModel;

namespace GrimList.Application;

/// <summary>A stable presentation object, never an independent command/history store.</summary>
public class ObservableValue<T>(T value) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public T Value { get; private set; } = value;
    public void Update(T value)
    {
        if (EqualityComparer<T>.Default.Equals(Value, value)) return;
        Value = value; PropertyChanged?.Invoke(this, new(nameof(Value)));
    }
}
