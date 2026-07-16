using System.Diagnostics;

namespace SmoothieBackend.API;

public static class BlenderAddonAPI
{
    public static void Initialize()
    {
        Debugger.Launch();
    }
    
    public static int GetInt()
    {
        var random = new Random().Next();
        return random;
    }
}