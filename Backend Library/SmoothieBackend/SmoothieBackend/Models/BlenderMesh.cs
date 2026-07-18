namespace SmoothieBackend.Models;

public class BlenderMesh
{
    public string Path { get; set; }
    public float[] Vertices { get; set; }
    public uint[] Indices { get; set; }
}