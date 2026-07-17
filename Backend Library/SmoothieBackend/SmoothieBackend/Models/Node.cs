using SharpDX;

namespace SmoothieBackend.Models;

public class Node
{
    public NodeID Id { get; set; }
    public BoundingSphere Position { get; set; }
    public bool IsStreaming { get; set; }
}