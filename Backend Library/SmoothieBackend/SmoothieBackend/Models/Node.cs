using SharpDX;
using WolvenKit.RED4.Types;
using Vector3 = SharpDX.Vector3;

namespace SmoothieBackend.Models;

public class Node
{
    public NodeID Id { get; set; }
    public BoundingSphere Position { get; set; }
    public EulerAngles Rotation { get; set; }
    public Vector3 Scale { get; set; }
    public bool IsStreaming { get; set; }
    public string? MeshPath { get; set; }
}