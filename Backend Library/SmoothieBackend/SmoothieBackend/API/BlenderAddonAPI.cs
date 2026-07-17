using System.Diagnostics;
using SharpDX;
using SmoothieBackend.Models;
using SmoothieBackend.Services;

namespace SmoothieBackend.API;

public static class BlenderAddonAPI
{
    private static WorldStreamingService _worldStreamingService;
    
    public static void Initialize()
    {
        Debugger.Launch();
        _worldStreamingService = new WorldStreamingService();
    }
    
    public static int GetInt()
    {
        var random = new Random().Next();
        return random;
    }

    public static void OnStreamingTick(float cameraX, float cameraY, float cameraZ)
    {
        _worldStreamingService.Tick(new Vector3(cameraX, cameraY, cameraZ));
    }
    
    public static void StartStreaming()
    {
        _worldStreamingService.StartStreaming();
    }
    
    public static void StopStreaming()
    {
        _worldStreamingService.StopStreaming();
    }

    public static IEnumerable<Node> GetLoadNodesQueue(int count)
    {
        return _worldStreamingService.GetLoadNodesQueue(count);
    }
    
    public static IEnumerable<NodeID> GetUnloadNodesQueue(int count)
    {
        return _worldStreamingService.GetUnloadNodesQueue(count);
    }
}