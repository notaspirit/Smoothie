namespace SmoothieBackend.Models;

public record struct NodeID
{
    public string ParentSector { get; set; }
    public int NodeDataIndex { get; set; }
    public int? InstanceIndex { get; set; }
    
    public NodeID(string parentSector, int nodeDataIndex, int? instanceIndex = null)
    {
        ParentSector = parentSector;
        NodeDataIndex = nodeDataIndex;
        InstanceIndex = instanceIndex;
    }

    public override string ToString()
    {
        if (InstanceIndex is not null)
            return $"{ParentSector}_{NodeDataIndex}_{InstanceIndex}";

        return $"{ParentSector}_{NodeDataIndex}";
    }
}