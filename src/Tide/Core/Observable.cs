using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Tide.Core;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _run;
    private readonly Func<object?, bool>? _can;

    public RelayCommand(Action<object?> run, Func<object?, bool>? can = null)
    {
        _run = run;
        _can = can;
    }

    public RelayCommand(Action run, Func<object?, bool>? can = null) : this(_ => run(), can) { }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _can?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _run(parameter);
}

/// <summary>Fixed-size ring of download/upload samples (bytes per second) for the live graphs.</summary>
public sealed class RateSeries
{
    public RateSeries(int capacity)
    {
        Down = new double[capacity];
        Up = new double[capacity];
    }

    public double[] Down { get; }
    public double[] Up { get; }
    public int Capacity => Down.Length;
    /// <summary>Index of the oldest sample.</summary>
    public int Head { get; private set; }

    public event Action? Changed;

    public void Push(double down, double up)
    {
        Down[Head] = down;
        Up[Head] = up;
        Head = (Head + 1) % Capacity;
        Changed?.Invoke();
    }

    public double DownAt(int i) => Down[(Head + i) % Capacity];
    public double UpAt(int i) => Up[(Head + i) % Capacity];
}
