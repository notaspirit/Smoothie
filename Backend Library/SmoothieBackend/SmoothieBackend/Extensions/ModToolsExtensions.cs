using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using WolvenKit.Modkit.RED4;
using WolvenKit.RED4.CR2W;
using WolvenKit.RED4.Types;

namespace SmoothieBackend.Extensions;
public static class ModToolsExtensions
{
    private static readonly Lazy<MethodInfo> GetRedImagesInfo = new Lazy<MethodInfo>(typeof(ModTools).GetMethod("GetRedImages", BindingFlags.NonPublic | BindingFlags.Static));

    public static IEnumerable<RedImage> GetRedImages(this ModTools modTools, rendRenderMultilayerMaskBlobPC value) => (IEnumerable<RedImage>)GetRedImagesInfo.Value.Invoke(modTools, [value]);
}

