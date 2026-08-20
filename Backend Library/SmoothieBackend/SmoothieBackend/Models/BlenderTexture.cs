namespace SmoothieBackend.Models;

public class BlenderTexture
{
    public MaterialID Id { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public byte[] PixelData { get; set; } = [];
}
