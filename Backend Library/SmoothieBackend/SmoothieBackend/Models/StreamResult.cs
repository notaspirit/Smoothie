using System.Collections.Concurrent;

namespace SmoothieBackend.Models;

public class StreamResult
{
    public ConcurrentBag<BlenderMesh> AddedMeshes { get; set; } = [];
    public ConcurrentBag<string> RemovedMeshes { get; set; } = [];
    
    public ConcurrentBag<Node> AddedNodes { get; set; } = [];
    public ConcurrentBag<NodeID> RemovedNodes { get; set; } = [];
}