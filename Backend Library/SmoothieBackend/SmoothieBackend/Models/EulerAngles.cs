namespace SmoothieBackend.Models;

public record struct EulerAngles
{
    public float Pitch { get; set; }
    public float Yaw { get; set; }
    public float Roll { get; set; }
}