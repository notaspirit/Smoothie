using SharpDX;

namespace SmoothieBackend.Models;

public class SectorDescriptor
{
    public string Path { get; set; }
    public BoundingBox BoundingBox { get; set; }
}