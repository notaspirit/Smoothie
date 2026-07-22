using SmoothieBackend.Models;
using WolvenKit.Common;
using WolvenKit.RED4.CR2W;
using WolvenKit.RED4.Types;

using WolvenKit.Modkit.RED4.GeneralStructs;
using WolvenKit.Modkit.RED4.Tools;
using WolvenKit.RED4.Archive.CR2W;
using WolvenKit.RED4.CR2W.Archive;
using Vector4 = SharpDX.Vector4;

namespace SmoothieBackend.Parsers;

public class BlenderMeshParser
{
    private record MeshDataCount(uint NumVertices, uint NumIndices, uint NumSubMeshes, List<int> SubmesheIndicesAtLOD);
 
    private static byte[]? _fallbackImage = null;

    private readonly IArchiveManager _archiveManager;

    public BlenderMeshParser(IArchiveManager archiveManager)
    {
        _archiveManager = archiveManager;
    }
    
    public BlenderMesh? Parse(string path)
    {
        var meshFile = _archiveManager.GetCR2WFile(path);
        if (meshFile is not  { RootChunk: CMesh { RenderResourceBlob.Chunk: rendRenderMeshBlob rendBlob } redMesh })
            return null;

        _fallbackImage ??= GetFallbackImage();
        
        var meshMd = MeshMetadata.BuildMeshMetadata(redMesh, rendBlob);
        var bMesh = ParseGeometryData(redMesh, meshMd);
        
        if (bMesh is null)
            return null;
        
        if (!BuildMaterials(bMesh, meshMd, meshFile))
        {
            Console.WriteLine($"Failed to build materials for {path}!");
            return null;
        }
        
        return bMesh;
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
        ArgumentNullException.ThrowIfNull(_fallbackImage, "Fallback image not loaded!");
        
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
                    textures[chunkIndex] = _fallbackImage;
                    chunkIndex++;
                    continue;
                }
                
                if (GetMaterial(matEntry, meshFile) is not CMaterialInstance matInst)
                {
                    Console.WriteLine($"Material {matEntry.Name} is not CMaterialInstance!");
                    textures[chunkIndex] = _fallbackImage;
                    chunkIndex++;
                    continue;
                }

                if (matInst.BaseMaterial.DepotPath.GetString()?.Contains("metal_base") != true)
                {
                    textures[chunkIndex] = _fallbackImage;
                    chunkIndex++;
                    continue;
                }

                var baseColorValue = matInst.Values.FirstOrDefault(kvp => kvp.Key == "BaseColor");
                if (baseColorValue?.Value is not CResourceReference<ITexture> texRef)
                {
                    Console.WriteLine($"Material {matEntry.Name} does not have a BaseColor value!");
                    textures[chunkIndex] = _fallbackImage;
                    chunkIndex++;
                    continue;
                }
                
                var png = GetPngFromEmbeddedOrArchive(meshFile, texRef.DepotPath);
                if (png is null)
                {
                    Console.WriteLine($"Failed to get texture {texRef.DepotPath} from archive and embedded files!");
                    textures[chunkIndex] = _fallbackImage;
                    chunkIndex++;
                    continue;
                }
                
                textures[chunkIndex] = png;
                chunkIndex++;
            }
        }
        
        return true;
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
    
    private byte[] GetFallbackImage()
    {
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