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
    
    public static void StreamInBackground(Vector3 streamingPoint)
    {
        _worldStreamingService.StreamInBackground(streamingPoint);
    }
    
    public static StreamResult? GetStreamResult()
    {
        return _worldStreamingService.GetStreamResult();
    }
}