using XXHash3NET;

namespace SmoothieBackend.Models;

public record struct MaterialID
{
    public string? MlSetupPath { get; set; }
    public string? MlMaskPath { get; set; }
    public string? AlbedoPath { get; set; }

    public override string ToString() => XXHash64.Compute($"{MlSetupPath}{MlMaskPath}{AlbedoPath}").ToString();
}