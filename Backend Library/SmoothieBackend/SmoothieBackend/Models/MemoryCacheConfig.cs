namespace SmoothieBackend.Models;

public class MemoryCacheConfig
{
    public int MaxItems { get; set; } = 1000;
    public int FreeItemsThreshold { get; set; } = 100;
    public TimeSpan CacheTickSpan { get; set; } = TimeSpan.FromSeconds(1);
    public float CacheReductionPercentage { get; set; } = 0.2f;
}