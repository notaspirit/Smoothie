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
    
    public override string ToString() => $"{ParentSector}_{Index}";
}