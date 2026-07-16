using SharpDX;
using SmoothieBackend.Models;
using WolvenKit.RED4.Types;

using SVector3 = SharpDX.Vector3;
using WVector3 = WolvenKit.RED4.Types.Vector3;

using SVector4 = SharpDX.Vector4;
using WVector4 = WolvenKit.RED4.Types.Vector4;

namespace SmoothieBackend.Extensions;

public static class WolvenKitToSharpDX
{
    public static BoundingBox ToSDX(this Box b) => new BoundingBox(b.Min.ToSDX().ToVector3(), b.Max.ToSDX().ToVector3());
    
    public static SVector3 ToSDX(this WVector3 v) => new SVector3(v.X, v.Y, v.Z);
    public static SVector4 ToSDX(this WVector4 v) => new SVector4(v.X, v.Y, v.Z, v.W);
}