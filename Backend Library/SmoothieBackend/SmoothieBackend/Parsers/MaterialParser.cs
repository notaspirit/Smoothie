using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using SkiaSharp;
using SmoothieBackend.Components;
using SmoothieBackend.Models;
using WolvenKit.Common;
using WolvenKit.Core.Extensions;
using WolvenKit.Modkit.RED4;
using WolvenKit.RED4.Archive.CR2W;
using WolvenKit.RED4.CR2W;
using WolvenKit.RED4.Types;
using SmoothieBackend.Extensions;

namespace SmoothieBackend.Parsers;

public class MaterialParser
{
    private record FlatMaterial(string BaseMaterial, Dictionary<string, object> Properties);
    
    private readonly IArchiveManager _archiveManager;
    
    private BlenderTexture _fallbackColorImage;
    private BlenderTexture _fallbackMaskImage;
    
    private SKImageInfo _commonImageInfo = new(512, 512, SKColorType.Rgba8888, SKAlphaType.Premul);
    private SKSamplingOptions _commonSamplingOptions = new(SKFilterMode.Nearest, SKMipmapMode.None);
    
    private readonly MemoryCache<string, SKBitmap> _imageCache;
    private readonly MemoryCache<string, BlenderTexture> _blenderTextureCache;
    private readonly MemoryCache<string, SKBitmap[]> _mlmaskCache;
    private readonly MemoryCache<string, RedBaseClass> _cr2wCache;
    
    public MaterialParser(IArchiveManager archiveManager)
    {
        _archiveManager = archiveManager;
        
        _fallbackColorImage = GetFilledSquareBlenderTexture(SKColors.DeepPink);
        _fallbackMaskImage = GetFilledSquareBlenderTexture(SKColors.Black);
        
        var cacheConfig = new MemoryCacheConfig();
        
        cacheConfig.MaxItems = 5000;
        cacheConfig.CacheTickSpan = TimeSpan.FromSeconds(10);
        
        _imageCache = new MemoryCache<string, SKBitmap>(cacheConfig);
        _blenderTextureCache = new MemoryCache<string, BlenderTexture>(cacheConfig);
        _mlmaskCache = new MemoryCache<string, SKBitmap[]>(cacheConfig);
        _cr2wCache = new MemoryCache<string, RedBaseClass>(cacheConfig);
    }

    public void ClearCache()
    {
        _imageCache.Clear();
        _blenderTextureCache.Clear();
        _mlmaskCache.Clear();
        _cr2wCache.Clear();
    }
    
    public bool BakeMaterials(BlenderMesh bMesh, MeshMetadata meshMd, CR2WFile meshFile)
    {
        if (meshFile is not { RootChunk: CMesh { RenderResourceBlob.Chunk: rendRenderMeshBlob rendBlob } redMesh })
            return false;
        
        Dictionary<CName, BlenderTexture> processedMaterials = new();

        foreach (var meshApp in redMesh.Appearances)
        {
            if (meshApp.Chunk is null)
                continue;
            
            BakeMeshAppearance(bMesh, meshMd, meshFile, meshApp.Chunk, processedMaterials);
        }
        
        return true;
    }

    private void BakeMeshAppearance(BlenderMesh bMesh, MeshMetadata meshMd, CR2WFile meshFile,
        meshMeshAppearance meshApp, Dictionary<CName, BlenderTexture> processedMaterials)
    {
        if (meshFile is not { RootChunk: CMesh { RenderResourceBlob.Chunk: rendRenderMeshBlob rendBlob } redMesh })
            return;
        
        bMesh.Textures.TryAdd(meshApp.Name!, new BlenderTexture[meshMd.SubmeshesAtLod.Count]);
        var textures = bMesh.Textures[meshApp.Name!];
        var chunkIndex = 0;

        foreach (var chunkMat in meshMd.SubmeshesAtLod.Select(matSubmeshIndex => meshApp.ChunkMaterials[matSubmeshIndex]))
        {
            if (processedMaterials.TryGetValue(chunkMat, out var texture))
                textures[chunkIndex++] = texture;
            else
            {
                var text = HandleChunkMaterial(meshFile, chunkMat);
                processedMaterials.Add(chunkMat, text);
                textures[chunkIndex++] = text;
            }
        }
    }

    private BlenderTexture HandleChunkMaterial(CR2WFile meshFile, CName chunkName)
    {
        if (meshFile is not { RootChunk: CMesh { RenderResourceBlob.Chunk: rendRenderMeshBlob rendBlob } redMesh })
            return _fallbackColorImage;
        
        var flatMat = GetFlattenedMaterial(meshFile, chunkName);
        if (flatMat is null)
            return _fallbackColorImage;
        
        if (flatMat.BaseMaterial.Contains("metal_base"))
        {
            return HandleMetalBaseMaterial(flatMat, meshFile) ?? _fallbackColorImage;
        }
        
        if (flatMat.BaseMaterial.Contains("multilayered"))
        {
            return HandleMultilayeredMaterial(flatMat, meshFile) ?? _fallbackColorImage;
        }
        
        return _fallbackColorImage;
    }
    
    private BlenderTexture? HandleMetalBaseMaterial(FlatMaterial flatMat, CR2WFile meshFile)
    {
        var baseColorValue = flatMat.Properties.FirstOrDefault(kvp => kvp.Key == "BaseColor").Value;
        if (baseColorValue is CResourceReference<ITexture> texRef)
        {
            return GetPngFromEmbeddedOrArchive(meshFile, texRef.DepotPath.GetString() ?? "");
        }
        
        Console.WriteLine($"Material with base material {flatMat.BaseMaterial} does not have a BaseColor value!");
        Console.WriteLine($"Material properties: {string.Join("\n", flatMat.Properties.Select(kvp => $"{kvp.Key}: {kvp.Value}"))}");
        return null;
    }
    
    private BlenderTexture? HandleMultilayeredMaterial(FlatMaterial flatMat, CR2WFile meshFile)
    {
        if (!flatMat.Properties.TryGetValue("MultilayerSetup", out var rawMls) ||
            rawMls is not CResourceReference<Multilayer_Setup> mls)
        {
            Console.WriteLine($"Material based on {flatMat.BaseMaterial} has no MultilayerSetup!");
            Console.WriteLine($"Available properties: {string.Join("\n", flatMat.Properties)}");
            return null;
        }
        
        if (!flatMat.Properties.TryGetValue("MultilayerMask", out var rawMlm) ||
            rawMlm is not CResourceReference<Multilayer_Mask> mlm)
        {
            Console.WriteLine($"Material based on {flatMat.BaseMaterial} has no Mask!");
            return null;
        }
        
        var setup = GetEmbeddedOrArchiveRootChunk(meshFile, mls.DepotPath.GetString() ?? "");
        if (setup is not Multilayer_Setup setupChunk)
        {
            Console.WriteLine("Material Setup is not Multilayer_Setup!");
            return null;
        }
        var masks = GetMlMaskAsSkBitmapFromArchive(mlm.DepotPath.GetString() ?? "");
        if (masks is null)
        {
            Console.WriteLine("Failed to load Multilayer Mask!");
            return null;
        }
        
        var baked = BakeMultiLayerSetup(setupChunk, masks);

        return baked;
    }
    
    private BlenderTexture BakeMultiLayerSetup(Multilayer_Setup setup, SKBitmap[] maskPngs)
    {
        var imageInfo = new SKImageInfo(512, 512);
        
        using var completeSurface = SKSurface.Create(imageInfo);
        var completeCanvas = completeSurface.Canvas;
        
        for (var i = 0; i < setup.Layers.Count; i++)
        {
            if (i >= maskPngs.Length)
                continue;
            
            var setupLayer = setup.Layers[i];
            var maskLayer = maskPngs[i];
            
            
            var mltFile = GetArchiveRootChunk(setupLayer.Material.DepotPath.GetString() ?? "");
            if (mltFile is not Multilayer_LayerTemplate mlt)
            {
                Console.WriteLine($"Failed to load material template {setupLayer.Material}!");
                continue;
            }

            var colorBitmap = GetXbmAsSkBitmapFromArchive(mlt.ColorTexture.DepotPath.GetString() ?? "");
            if (colorBitmap is null)
            {
                Console.WriteLine($"Failed to load color bitmap from {mlt.ColorTexture}");
                continue;
            }
            
            var color = GetMlLayerColor(mlt, setupLayer);

            using var surface = SKSurface.Create(imageInfo);
            var canvas = surface.Canvas;

            using var colorPaint = new SKPaint();
            colorPaint.ColorFilter = SKColorFilter.CreateBlendMode(color, SKBlendMode.Modulate);

            canvas.DrawBitmap(colorBitmap, 0, 0, colorPaint);

            using var maskPaint = new SKPaint();
            maskPaint.BlendMode = SKBlendMode.DstIn;
            maskPaint.ColorFilter = SKColorFilter.CreateLumaColor();

            canvas.DrawBitmap(maskLayer, 0, 0, maskPaint);

            using var masked = surface.Snapshot();
            completeCanvas.DrawImage(masked, 0, 0);
        }
        
        var savedCanvas = completeSurface.Snapshot().GetBlenderTexture();
        return savedCanvas;
    }

    private SKColor GetMlLayerColor(Multilayer_LayerTemplate mlt, Multilayer_Layer layer)
    {
        var colorId = layer.ColorScale;
        var cs = mlt.Overrides.ColorScale.FirstOrDefault(cs => cs.N == colorId);
        if (cs is null || cs.V.Count != 3)
            return SKColors.Gray;
        
        return new SKColor(GetFloatColorAsByte(cs.V[0]), GetFloatColorAsByte(cs.V[1]), GetFloatColorAsByte(cs.V[2]), 255);
    }
    
    private byte GetFloatColorAsByte(float color) => (byte)(color * 255);

    private FlatMaterial? GetFlattenedMaterial(CR2WFile meshFile, CName matName)
    {
        if (meshFile is not { RootChunk: CMesh { RenderResourceBlob.Chunk: rendRenderMeshBlob rendBlob } redMesh })
            return null;
        
        var matEntry = redMesh.MaterialEntries.FirstOrDefault(mt => mt.Name == matName);
        if (matEntry is null)
        {
            Console.WriteLine($"Material {matName} not found!");
            return null;
        }

        if (GetMaterial(matEntry, meshFile) is not CMaterialInstance matInst)
        {
            Console.WriteLine($"Material {matEntry.Name} is not CMaterialInstance!");
            return null;
        }
        
        return GetFlattenedMaterial(matInst, matInst.BaseMaterial.DepotPath.GetString() ?? "");
    }
    
    private FlatMaterial GetFlattenedMaterial(IMaterial material, string basePath)
    {
        var baseMaterial = basePath;
        Dictionary<string, object> properties = new();
        
        var currentMat = material;
        
        while (true)
        {
            switch (currentMat)
            {
                case CMaterialInstance matInstance:
                {
                    foreach (var kvp in matInstance.Values)
                    {
                        string? name = kvp.Key;
                        if (name is not null)
                            properties.TryAdd(name, kvp.Value);
                    }
                    
                    if (matInstance.BaseMaterial.DepotPath.GetString() is not { } baseMaterialPath)
                    {
                        goto breakOuter;
                    }
                    
                    var baseMatRc = _archiveManager.GetCR2WFile(baseMaterialPath)?.RootChunk;
                    
                    if (baseMatRc is not IMaterial baseMat)
                    {
                        goto breakOuter;
                    }

                    currentMat = baseMat;
                    baseMaterial = baseMaterialPath;
                    break;
                }
                case CMaterialTemplate matTemplate:
                {
                    var values = matTemplate.Parameters[2];
                    foreach (var matParam in values)
                    {
                        if (matParam.Chunk is null)
                            continue;

                        string? name = matParam.Chunk.ParameterName;
                        object? value = matParam.Chunk switch
                        {
                            CMaterialParameterColor mpc => mpc.Color,
                            CMaterialParameterCpuNameU64 mpcnu => mpcnu.Name,
                            CMaterialParameterCube mpcu => mpcu.Texture,
                            CMaterialParameterDynamicTexture mpdt => mpdt.Texture,
                            CMaterialParameterFoliageParameters mpfp => mpfp.FoliageProfile,
                            CMaterialParameterGradient mpg => mpg.Gradient,
                            CMaterialParameterHairParameters mphp => mphp.HairProfile,
                            CMaterialParameterMultilayerMask mpml => mpml.Mask,
                            CMaterialParameterMultilayerSetup mpms => mpms.Setup,
                            CMaterialParameterScalar mps => mps.Scalar,
                            CMaterialParameterSkinParameters mpsp => mpsp.SkinProfile,
                            CMaterialParameterStructBuffer => null,
                            CMaterialParameterTerrainSetup mpts => mpts.Setup,
                            CMaterialParameterTexture mpt => mpt.Texture,
                            CMaterialParameterTextureArray mpta => mpta.Texture,
                            CMaterialParameterVector mpv => mpv.Vector,
                            _ => null
                        };
                        
                        if (name is not null && value is not null)
                            properties.TryAdd(name, value);
                    }
                    
                    goto breakOuter;
                }
                default:
                {
                    Console.WriteLine($"Material {currentMat} is not a material instance or material template!");
                    goto breakOuter;
                }
            }
            
            continue;
            
            breakOuter:
            break;
        }
        
        return new FlatMaterial(baseMaterial, properties);
    }
    
    private IMaterial? GetMaterial(CMeshMaterialEntry matEntry, CR2WFile meshFile)
    {
        if (meshFile is not { RootChunk: CMesh { RenderResourceBlob.Chunk: rendRenderMeshBlob rendBlob } redMesh })
            return null;
        
        if (matEntry.IsLocalInstance)
        {
            if (matEntry.Index < redMesh.LocalMaterialBuffer.Materials.Count)
                return redMesh.LocalMaterialBuffer.Materials[matEntry.Index];
            
            if (matEntry.Index < redMesh.PreloadLocalMaterialInstances.Count)
                return redMesh.PreloadLocalMaterialInstances[matEntry.Index ];

            Console.WriteLine($"Local Material {matEntry.Index} not found!");
            return null;
        }

        CResourceReference<IMaterial>? matRef = null;
        CResourceAsyncReference<IMaterial>? asyncMatRef = null;
        if (matEntry.Index < redMesh.ExternalMaterials.Count)
            asyncMatRef = redMesh.ExternalMaterials[matEntry.Index];
        else if (matEntry.Index < redMesh.PreloadExternalMaterials.Count)
            matRef = redMesh.PreloadExternalMaterials[matEntry.Index];
        else
        {
            Console.WriteLine($"External Material {matEntry.Index} not found!");
            return null;
        }
                
        var matRefPath = matRef?.DepotPath ?? asyncMatRef?.DepotPath ?? ""; 
                
        return (IMaterial)GetEmbeddedOrArchiveRootChunk(meshFile, matRefPath);
    }
    
    private RedBaseClass? GetEmbeddedOrArchiveRootChunk(CR2WFile parent, string path)
    {
        if (_cr2wCache.TryGetValue(path, out var cached))
            return cached;
        
        var embeddedFile = parent.EmbeddedFiles.FirstOrDefault(efile => efile.FileName == path);
        if (embeddedFile is not null)
        {
            _cr2wCache.TryAdd(path, embeddedFile.Content);
            return embeddedFile.Content;
        }
        
        return GetArchiveRootChunk(path);
    }

    private RedBaseClass? GetArchiveRootChunk(string path)
    {
        if (_cr2wCache.TryGetValue(path, out var cached))
            return cached;
        
        var archiveFile = _archiveManager.GetCR2WFile(path);
        var rootChunk = archiveFile?.RootChunk;
        if (rootChunk is not null)
            _cr2wCache.TryAdd(path, rootChunk);
        
        return rootChunk;
    }
    
    private BlenderTexture? GetPngFromEmbeddedOrArchive(CR2WFile parent, string path)
    {
        if (_blenderTextureCache.TryGetValue(path, out var cached))
            return cached;
        
        var xbmRc = GetEmbeddedOrArchiveRootChunk(parent, path);
        if (xbmRc is not CBitmapTexture xbm)
            return null;
        
        var texture = GetSkBitmap(RedImage.FromXBM(xbm)).GetBlenderTexture();
        
        _blenderTextureCache.TryAdd(path, texture);
        
        return texture;
    }
    
    private SKBitmap? GetXbmAsSkBitmapFromArchive(string path)
    {
        if (_imageCache.TryGetValue(path, out var cached))
            return cached;
        
        if (GetArchiveRootChunk(path) is not CBitmapTexture xbm)
            return null;

        var png = RedImage.FromXBM(xbm).GetPreview(true);
        
        using var bitmap = SKBitmap.Decode(png);
        var bitmapResized =
            bitmap.Resize(_commonImageInfo,_commonSamplingOptions);
        
        _imageCache.TryAdd(path, bitmapResized);
        
        return bitmapResized;
    }

    private SKBitmap[]? GetMlMaskAsSkBitmapFromArchive(string path)
    {
        if (_mlmaskCache.TryGetValue(path, out var cached))
            return cached;
        
        if (GetArchiveRootChunk(path) is not Multilayer_Mask mask)
            return null;
        
        ModTools.ConvertMultilayerMaskToDdsStreams(mask, out var mlmLayerStreams);

        var bitmaps = new SKBitmap[mlmLayerStreams.Count];
        
        foreach (var layer in mlmLayerStreams)
        {
            if (RedImage.LoadFromDDSMemory(layer.ToByteArray()) is not { } img)
                throw new Exception("Failed to load DDS!");
            
            bitmaps[mlmLayerStreams.IndexOf(layer)] = GetSkBitmap(img);
            layer.Dispose();
        }
        
        _mlmaskCache.TryAdd(path, bitmaps);
        
        return bitmaps;
    }

    private SKBitmap GetSkBitmap(RedImage rimage)
    {
        var png = rimage.GetPreview(true);
        using var bitmap = SKBitmap.Decode(png);
        return bitmap.Resize(_commonImageInfo,_commonSamplingOptions);
    }
    
    private BlenderTexture GetFilledSquareBlenderTexture(SKColor color, int width = 2, int height = 2)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(color);

        return bitmap.GetBlenderTexture();
    }
}