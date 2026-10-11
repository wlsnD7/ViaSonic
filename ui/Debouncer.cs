using Microsoft.UI.Xaml;

namespace ViaTTS;

/// <summary>滑块拖动等高频变化合并成一次保存。</summary>
public sealed class Debouncer(int ms = 300)
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(ms) };
    private Action? _action;

    public void Run(Action action)
    {
        _action = action;
        _timer.Stop();
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void OnTick(object? sender, object e)
    {
        _timer.Stop();
        _action?.Invoke();
    }
}
