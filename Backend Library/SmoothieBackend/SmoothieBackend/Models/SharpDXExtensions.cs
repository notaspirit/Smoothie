using SharpDX;

namespace SmoothieBackend.Models;

public static class SharpDXExtensions
{
    public static Vector3 ToVector3(this Vector4 v) => new(v.X, v.Y, v.Z);
}