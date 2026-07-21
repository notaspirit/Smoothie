namespace SmoothieBackend.Models;

public class BlenderMesh
{
    public string Path { get; set; }
    public float[] Vertices { get; set; }
    public float[] UVs { get; set; }
    public uint[] Indices { get; set; }
    public uint[] SubMeshIndexOffsets { get; set; }
    public Dictionary<string, byte[][]> Textures { get; set; }
}