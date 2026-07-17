namespace SmoothieBackend.Models;

public record struct NodeID
{
    public string ParentSector { get; set; }
    public int Index { get; set; }
    
    public NodeID(string parentSector, int index)
    {
        ParentSector = parentSector;
        Index = index;
    }
}