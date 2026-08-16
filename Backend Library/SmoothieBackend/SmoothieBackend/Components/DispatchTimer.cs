namespace SmoothieBackend.Components;

public class DispatchTimer
{
    private readonly PeriodicTimer _timer;
    
    public DispatchTimer(TimeSpan period)
    {
        _timer = new PeriodicTimer(period);
        Task.Run(Tick);
    }
    
    public event EventHandler OnTick;

    private async Task Tick()
    {
        await _timer.WaitForNextTickAsync();
        OnTick?.Invoke(this, EventArgs.Empty);
    }
}