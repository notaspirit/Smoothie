using WolvenKit.RED4.Types;

namespace SmoothieBackend.Models;

public class DeferredDeserializedTexture
{
    public CBitmapTexture Raw { get; set; }
    public BlenderTexture? Texture { get; set; }
}