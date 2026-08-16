using System.Runtime.InteropServices;
using SkiaSharp;
using SmoothieBackend.Models;

namespace SmoothieBackend.Extensions;

public static class SkiaSharpExtensions
{
    public static BlenderTexture GetBlenderTexture(this SKBitmap bitmap)
    {
        return new BlenderTexture
        {
            Height = bitmap.Height,
            Width = bitmap.Width,
            PixelData = GetPixels(bitmap)
        };
    }

    private static byte[] GetPixels(SKBitmap bitmap)
    {
        var pixelsPtr = bitmap.GetPixels(out var length);
        var bytes = new byte[(int)length];
        Marshal.Copy(pixelsPtr, bytes, 0, (int)length);
        return bytes;
    }
    
    public static BlenderTexture GetBlenderTexture(this SKImage image)
    {
        return new BlenderTexture
        {
            Height = image.Height,
            Width = image.Width,
            PixelData = GetPixels(image)
        };
    }

    private static byte[] GetPixels(SKImage image)
    {
        using var pixmap = new SKPixmap();
        
        if (image.PeekPixels(pixmap))
        {
            using (pixmap)
            {
                if (pixmap.ColorType == SKColorType.Rgba8888)
                {
                    return pixmap.GetPixelSpan().ToArray();
                }
            }
        }
        
        var info = new SKImageInfo(
            image.Width,
            image.Height,
            SKColorType.Rgba8888,
            SKAlphaType.Premul);

        var pixels = new byte[info.BytesSize];

        unsafe
        {
            fixed (byte* ptr = pixels)
            {
                if (!image.ReadPixels(info, (IntPtr)ptr, info.RowBytes, 0, 0))
                {
                    throw new Exception("Failed to read pixels from SKImage");
                }
            }
        }
        
        return pixels;
    }
}