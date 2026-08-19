using System.Reflection;
using System.Runtime.InteropServices;
using DirectXTexNet;
using SkiaSharp;
using WolvenKit.RED4.CR2W;

namespace SmoothieBackend.Extensions;

public static class RedImageExtensions
{
    private static readonly Lazy<Func<RedImage, ScratchImage>> GetInternalScratchImageAccessor = new (BuildScratchImageAccessor);
    public static ScratchImage GetScratchImage(this RedImage redImage) => GetInternalScratchImageAccessor.Value.Invoke(redImage);
    private static Func<RedImage, ScratchImage> BuildScratchImageAccessor()
    {
        var prop = typeof(RedImage).GetProperty("InternalScratchImage", BindingFlags.NonPublic | BindingFlags.Instance)
                   ?? throw new MissingMemberException("RedImage.InternalScratchImage not found - WolvenKit internals changed");
        var getter = prop.GetGetMethod(nonPublic: true)!;
        return (Func<RedImage, ScratchImage>)Delegate.CreateDelegate(typeof(Func<RedImage, ScratchImage>), getter);
    }

    private static SKImageInfo _commonImageInfo = new(512, 512, SKColorType.Rgba8888, SKAlphaType.Premul);
    private static SKSamplingOptions _commonSamplingOptions = new(SKFilterMode.Nearest, SKMipmapMode.None);
    
    public static SKBitmap GetSkBitmap(this RedImage redImage, bool flip, bool resize)
    {
        var scratch = redImage.GetScratchImage();
        var metadata = scratch.GetMetadata();

        var mip = 0;
        if (resize) 
            mip = metadata.SelectMip(512);
        var image = scratch.GetImage(mip, 0, 0);

        var colorType = metadata.Format switch
        {
            DXGI_FORMAT.R8G8B8A8_UNORM or DXGI_FORMAT.R8G8B8A8_UNORM_SRGB => SKColorType.Rgba8888,
            DXGI_FORMAT.B8G8R8A8_UNORM or DXGI_FORMAT.B8G8R8A8_UNORM_SRGB => SKColorType.Bgra8888,
            DXGI_FORMAT.R8_UNORM => SKColorType.Gray8,
            _ => SKColorType.Unknown
        };
        
        if (colorType == SKColorType.Unknown)
        {
            Console.WriteLine($"Unhandled format {metadata.Format}, falling back to PNG path");
            var png = redImage.GetPreview(flip);
            var bitmap  = SKBitmap.Decode(png);
            if (resize)
                return bitmap.TryResize(_commonImageInfo, _commonSamplingOptions);
            
            return bitmap;
        }
        
        var alphaType = colorType == SKColorType.Gray8 ? SKAlphaType.Opaque : SKAlphaType.Unpremul;
        var info = new SKImageInfo(image.Width, image.Height, colorType, alphaType);

        // copy pixels out — redImage/scratch get disposed when this method returns
        var pixelBuf = Marshal.AllocHGlobal(info.BytesSize);
        unsafe { Buffer.MemoryCopy((void*)image.Pixels, (void*)pixelBuf, info.BytesSize, info.BytesSize); }

        var raw = new SKBitmap();
        raw.InstallPixels(info, pixelBuf, (int)image.RowPitch, (addr, _) => Marshal.FreeHGlobal(addr));

        var result = raw;
        /*
        if (flip)
        {
            var flipped = new SKBitmap(info);
            using (var canvas = new SKCanvas(flipped))
            {
                canvas.Scale(1, -1);
                canvas.Translate(0, -info.Height);
                canvas.DrawBitmap(raw, 0, 0);
            }
            raw.Dispose();
            result = flipped;
        }
        */
        
        if (resize)
            return result.TryResize(_commonImageInfo, _commonSamplingOptions);
        return result;
    }
}