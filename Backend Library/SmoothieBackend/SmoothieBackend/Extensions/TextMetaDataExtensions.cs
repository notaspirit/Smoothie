using DirectXTexNet;

namespace SmoothieBackend.Extensions;

public static class TextMetaDataExtensions
{
    public static int SelectMip(this TexMetadata metadata, int target)
    {
        int selected = 0;
        for (int mip = 0; mip < metadata.MipLevels; mip++)
        {
            int w = Math.Max(1, metadata.Width >> mip);
            int h = Math.Max(1, metadata.Height >> mip);
            if (w < target || h < target)
                break; // previous mip was the last one still big enough
            selected = mip;
        }
        return selected;
    }
}