using System.Collections.Concurrent;
using System.Diagnostics;

namespace SmoothieBackend.Components;

public class TrackedStopWatch
{
    private readonly string _name;
    private readonly Stopwatch _stopwatch;
    private readonly ConcurrentDictionary<string, ConcurrentBag<TimeSpan>> _trackedTimes;
    
    public TrackedStopWatch(string name, ConcurrentDictionary<string, ConcurrentBag<TimeSpan>> trackedTimes)
    {
        _name = name;
        _stopwatch = Stopwatch.StartNew();
        _trackedTimes = trackedTimes;
    }

    public void Stop(bool saveResult = true)
    {
        _stopwatch.Stop();
        
        if (!saveResult)
            return;
        
        _trackedTimes.GetOrAdd(_name, _ => [])
            .Add(_stopwatch.Elapsed);
    }
}