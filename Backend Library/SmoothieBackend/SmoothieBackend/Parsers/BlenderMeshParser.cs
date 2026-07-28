using SmoothieBackend.Models;
using WolvenKit.Common;
using WolvenKit.Core.Extensions;
using WolvenKit.Modkit.RED4;
using WolvenKit.RED4.CR2W;
using WolvenKit.RED4.Types;
using WolvenKit.Modkit.RED4.GeneralStructs;
using WolvenKit.Modkit.RED4.Tools;
using WolvenKit.RED4.Archive.CR2W;
using SkiaSharp;
using Vector4 = SharpDX.Vector4;

namespace SmoothieBackend.Parsers;

public class BlenderMeshParser
{
    private record FlatMaterial(string BaseMaterial, Dictionary<string, object> Properties);
 
    private static byte[]? _fallbackColorImage = null;
    private static byte[]? _fallbackMaskImage = null;

    private readonly IArchiveManager _archiveManager;

    public BlenderMeshParser(IArchiveManager archiveManager)
    {
        _archiveManager = archiveManager;
    }
    
    public BlenderMesh? Parse(string path)
    {
        var meshFile = _archiveManager.GetCR2WFile(path);
        return meshFile is not null ? Parse(meshFile) : null;
    }

    public BlenderMesh? Parse(CR2WFile meshFile)
    {
        if (meshFile is not  { RootChunk: CMesh { RenderResourceBlob.Chunk: rendRenderMeshBlob rendBlob } redMesh })
            return null;
        
        _fallbackColorImage ??= GetFilledSquarePng(SKColors.DeepPink);
        _fallbackMaskImage ??= GetFilledSquarePng(SKColors.Black);
        
        var meshMd = MeshMetadata.BuildMeshMetadata(redMesh, rendBlob);
        var bMesh = ParseGeometryData(redMesh, meshMd);
        
        if (bMesh is null)
            return null;

        if (BuildMaterials(bMesh, meshMd, meshFile))
            return bMesh;
        
        Console.WriteLine($"Failed to build materials when parsing mesh.");
        return null;

    }
    
    private static BlenderMesh? ParseGeometryData(CMesh mesh, MeshMetadata meshMd)
    {
        if (mesh is not { RenderResourceBlob.Chunk: rendRenderMeshBlob rendBlob })
            return null;
        
        var wkitMeshInfo = MeshTools.GetMeshesinfo(rendBlob, mesh, "meshName?");
        
        var bMesh = new BlenderMesh();
        bMesh.Vertices = new float[meshMd.NumVertices * 3];
        bMesh.Indices = new uint[meshMd.NumIndices];
        bMesh.SubMeshIndexOffsets = new uint[meshMd.SubmeshesAtLod.Count];
        bMesh.UVs = new float[meshMd.NumVertices * 2];
        bMesh.Textures = new Dictionary<string, byte[][]>();
        
        using var ms = new MemoryStream(rendBlob.RenderBuffer.Buffer.GetBytes());
        var br = new BinaryReader(ms);

        var quantScale = new Vector4(rendBlob.Header.QuantizationScale.X,
            rendBlob.Header.QuantizationScale.Y,
            rendBlob.Header.QuantizationScale.Z,
            rendBlob.Header.QuantizationScale.W);
        var quantOffset = new Vector4(rendBlob.Header.QuantizationOffset.X,
            rendBlob.Header.QuantizationOffset.Y,
            rendBlob.Header.QuantizationOffset.Z,
            rendBlob.Header.QuantizationOffset.W);
        
        var globalVertIndex = 0;
        var globalIndexIndex = 0;
        var indexOffset = 0;
        var subMeshIndex = 0;
        
        var globalUvIndex = 0;
        
        foreach (var rendInfo in rendBlob.Header.RenderChunkInfos)
        {
            if (rendInfo.LodMask != meshMd.LowestLod)
                continue;

            for (var indexVertex = 0; indexVertex < rendInfo.NumVertices; indexVertex++)
            {
                br.BaseStream.Position = rendInfo.ChunkVertices.ByteOffsets[0] + (indexVertex * rendInfo.ChunkVertices.VertexLayout.SlotStrides[0]);

                bMesh.Vertices[globalVertIndex] = (br.ReadInt16() / 32767f * quantScale.X) + quantOffset.X;
                bMesh.Vertices[globalVertIndex + 1] = (br.ReadInt16() / 32767f * quantScale.Y) + quantOffset.Y;
                bMesh.Vertices[globalVertIndex + 2] = (br.ReadInt16() / 32767f * quantScale.Z) + quantOffset.Z;
                
                globalVertIndex += 3;
            }
            
            if (wkitMeshInfo.tex0Offsets[subMeshIndex] != 0)
            {
                for (var i = 0; i < rendInfo.NumVertices; i++)
                {
                    br.BaseStream.Position = wkitMeshInfo.tex0Offsets[subMeshIndex] + (i * 4);
                    bMesh.UVs[globalUvIndex] = Converters.hfconvert(br.ReadUInt16());
                    bMesh.UVs[globalUvIndex + 1] = Converters.hfconvert(br.ReadUInt16());
                    
                    globalUvIndex += 2;
                }
            }

            br.BaseStream.Position = rendBlob.Header.IndexBufferOffset + rendInfo.ChunkIndices.TeOffset;
            for (var indexIndex = 0; indexIndex < rendInfo.NumIndices; indexIndex++)
            {
                bMesh.Indices[globalIndexIndex] = (uint)(br.ReadUInt16() + indexOffset);
                globalIndexIndex++;
            }

            bMesh.SubMeshIndexOffsets[subMeshIndex] = (uint)indexOffset;
            
            indexOffset += rendInfo.NumVertices;
            subMeshIndex++;
        }
        
        return bMesh;
    }

    private bool BuildMaterials(BlenderMesh bMesh, MeshMetadata meshMd, CR2WFile meshFile)
    { 
        ArgumentNullException.ThrowIfNull(_fallbackColorImage, "Fallback image not loaded!");
        
        if (meshFile is not { RootChunk: CMesh { RenderResourceBlob.Chunk: rendRenderMeshBlob rendBlob } redMesh })
            return false;
        
        foreach (var meshApp in redMesh.Appearances)
        {
            if (meshApp.Chunk is null)
                continue;
            
            bMesh.Textures.TryAdd(meshApp.Chunk.Name!, new byte[meshMd.SubmeshesAtLod.Count][]);
            var textures = bMesh.Textures[meshApp.Chunk.Name!];
            var chunkIndex = 0;
            foreach (var matSubmeshIndex in meshMd.SubmeshesAtLod)
            {
                var chunkMat = meshApp.Chunk.ChunkMaterials[matSubmeshIndex];
                
                var matEntry = redMesh.MaterialEntries.FirstOrDefault(mt => mt.Name == chunkMat);
                if (matEntry == null)
                {
                    Console.WriteLine($"Material {chunkMat} not found!");
                    textures[chunkIndex] = _fallbackColorImage;
                    chunkIndex++;
                    continue;
                }
                
                if (GetMaterial(matEntry, meshFile) is not CMaterialInstance matInst)
                {
                    Console.WriteLine($"Material {matEntry.Name} is not CMaterialInstance!");
                    textures[chunkIndex] = _fallbackColorImage;
                    chunkIndex++;
                    continue;
                }
                
                var flatMat = GetFlattenedMaterial(matInst, matInst.BaseMaterial.DepotPath.GetString() ?? "");
                
                byte[]? texture = null;
                
                if (flatMat.BaseMaterial.Contains("metal_base"))
                {
                    texture = HandleMetalBaseMaterial(flatMat, meshFile);
                }
                else if (flatMat.BaseMaterial.Contains("multilayered"))
                {
                    texture = HandleMultilayeredMaterial(flatMat, meshFile);
                }

                
                if (texture is not null)
                    textures[chunkIndex] = texture;
                else
                    textures[chunkIndex] = _fallbackColorImage;
                
                chunkIndex++;
            }
        }
        
        return true;
    }

    private byte[]? HandleMultilayeredMaterial(FlatMaterial flatMat, CR2WFile meshFile)
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
        
        var mask = GetEmbeddedOrArchiveRootChunk(meshFile, mlm.DepotPath.GetString() ?? "");
        if (mask is not Multilayer_Mask maskChunk)
        {
            Console.WriteLine("Material Mask is not Multilayer_Mask!");
            return null;
        }
        
        var setup = GetEmbeddedOrArchiveRootChunk(meshFile, mls.DepotPath.GetString() ?? "");
        if (setup is not Multilayer_Setup setupChunk)
        {
            Console.WriteLine("Material Setup is not Multilayer_Setup!");
            return null;
        }
        
        var maskPngs = ConvertMaskLayerToPng(maskChunk);
        
        return BakeMultiLayerSetup(setupChunk, maskPngs);
    }

    private byte[] BakeMultiLayerSetup(Multilayer_Setup setup, byte[][] maskPngs)
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
            var mltFile = _archiveManager.GetCR2WFile(setupLayer.Material.DepotPath);
            if (mltFile is not { RootChunk: Multilayer_LayerTemplate mlt })
            {
                Console.WriteLine($"Failed to load material template {setupLayer.Material}!");
                continue;
            }

            var basePng = GetPngFromArchive(mlt.ColorTexture.DepotPath!);
            if (basePng is null)
            {
                Console.WriteLine($"Failed to load base png from {mlt.ColorTexture}");
                continue;
            }
            
            using var layerColorBitmapRaw = SKBitmap.Decode(basePng);
            using var layerBitmap = SKBitmap.Decode(maskLayer);
            using var layerColorBitmapResized =
                layerColorBitmapRaw.Resize(new SKImageInfo(512, 512), SKSamplingOptions.Default);
            
            using var surface = SKSurface.Create(imageInfo);
            var canvas = surface.Canvas;
            
            canvas.DrawBitmap(layerColorBitmapResized, 0, 0);
            
            using var maskPaint = new SKPaint();
            maskPaint.BlendMode = SKBlendMode.DstIn;
            maskPaint.ColorFilter = SKColorFilter.CreateLumaColor();
            
            canvas.DrawBitmap(layerBitmap, 0, 0, maskPaint);
            
            using var masked = surface.Snapshot();
            
            completeCanvas.DrawImage(masked, 0, 0);
        }
        
        return completeSurface.Snapshot().Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }
    
    private byte[][] ConvertMaskLayerToPng(Multilayer_Mask mlm)
    {
        ModTools.ConvertMultilayerMaskToDdsStreams(mlm, out var mlmLayerStreams);

        byte[][] pngLayers = new byte[mlmLayerStreams.Count][];
        
        foreach (var layer in mlmLayerStreams)
        {
            if (RedImage.LoadFromDDSMemory(layer.ToByteArray()) is not { } img)
                throw new Exception("Failed to load DDS!");
            
            pngLayers[mlmLayerStreams.IndexOf(layer)] = img.GetPreview(true);
            layer.Dispose();
        }
        
        return pngLayers;
    }
    
    private byte[]? HandleMetalBaseMaterial(FlatMaterial flatMat, CR2WFile meshFile)
    {
        var baseColorValue = flatMat.Properties.FirstOrDefault(kvp => kvp.Key == "BaseColor").Value;
        if (baseColorValue is CResourceReference<ITexture> texRef)
            return GetPngFromEmbeddedOrArchive(meshFile, texRef.DepotPath.GetString() ?? "");
        
        
        Console.WriteLine($"Material with base material {flatMat.BaseMaterial} does not have a BaseColor value!");
        Console.WriteLine($"Material properties: {string.Join("\n", flatMat.Properties.Select(kvp => $"{kvp.Key}: {kvp.Value}"))}");
        return null;
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

    private byte[]? GetPngFromEmbeddedOrArchive(CR2WFile parent, string path)
    {
        var xbmRC = GetEmbeddedOrArchiveRootChunk(parent, path);
        if (xbmRC is not CBitmapTexture xbm)
            return null;
        
        return RedImage.FromXBM(xbm).GetPreview(true);
    }
    
    private byte[]? GetPngFromArchive(string path)
    {
        var archiveFile = _archiveManager.GetCR2WFile(path);
        if (archiveFile?.RootChunk is not CBitmapTexture xbm)
            return null;
        
        return RedImage.FromXBM(xbm).GetPreview(true);
    }
    
    private byte[] GetFilledSquarePng(SKColor color, int width = 2, int height = 2)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(color);
        
        return SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Png, 100).ToArray();
        
        var file = _archiveManager.GetCR2WFile(@"base\vehicles\special\av_zetatech_bombus\entities\meshes\textures\av_zetatech_bombus__ext02_nanny_dislpay_pink_b.xbm");
        if (file is not { RootChunk: CBitmapTexture xbm })
            throw new Exception("Failed to get fallback image!");
        
        return RedImage.FromXBM(xbm).GetPreview(false);
    }

    private RedBaseClass? GetEmbeddedOrArchiveRootChunk(CR2WFile parent, string path)
    {
        var embeddedFile = parent.EmbeddedFiles.FirstOrDefault(efile => efile.FileName == path);
        if (embeddedFile is not null)
            return embeddedFile.Content;
        
        var archiveFile = _archiveManager.GetCR2WFile(path);
        return archiveFile?.RootChunk;
    }
}