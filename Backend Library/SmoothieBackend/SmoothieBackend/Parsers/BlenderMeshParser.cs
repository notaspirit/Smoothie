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

public static class BlenderMeshParser
{
    private record MeshDataCount(uint NumVertices, uint NumIndices, uint NumSubMeshes, List<int> SubmesheIndicesAtLOD);
 
    private static byte[]? _fallbackImage = null;
    
    public static BlenderMesh? Parse(IArchiveManager archiveManager, CR2WFile meshFile)
    {
        if (meshFile is not  { RootChunk: CMesh { RenderResourceBlob.Chunk: rendRenderMeshBlob rendBlob } redMesh })
            return null;

        _fallbackImage ??= GetFallbackImage(archiveManager);
        
        var lowestLod = DetermineLowestQualityLOD(rendBlob);
        
        var dataSize = CountMeshDataAtLOD(rendBlob, lowestLod);
        var bMesh = new BlenderMesh();
        bMesh.Vertices = new float[dataSize.NumVertices * 3];
        bMesh.Indices = new uint[dataSize.NumIndices];
        bMesh.SubMeshIndexOffsets = new uint[dataSize.NumSubMeshes];
        bMesh.UVs = new float[dataSize.NumVertices * 2];
        bMesh.Textures = new Dictionary<string, byte[][]>();

        var wkitMeshInfo = MeshTools.GetMeshesinfo(rendBlob, redMesh, "meshName?");
        
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
        
        var globalUVIndex = 0;
        
        foreach (var rendInfo in rendBlob.Header.RenderChunkInfos)
        {
            if (rendInfo.LodMask != lowestLod) 
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
                    bMesh.UVs[globalUVIndex] = Converters.hfconvert(br.ReadUInt16());
                    bMesh.UVs[globalUVIndex + 1] = Converters.hfconvert(br.ReadUInt16());
                    
                    globalUVIndex += 2;
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

        foreach (var meshApp in redMesh.Appearances)
        {
            if (meshApp.Chunk is null)
                continue;
            
            bMesh.Textures.TryAdd(meshApp.Chunk.Name!, new byte[dataSize.NumSubMeshes][]);
            var textures = bMesh.Textures[meshApp.Chunk.Name!];
            var chunkIndex = 0;
            foreach (var matSubmeshIndex in dataSize.SubmesheIndicesAtLOD)
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

                IMaterial mat;

                if (matEntry.IsLocalInstance)
                {
                    if (matEntry.Index < redMesh.LocalMaterialBuffer.Materials.Count)
                        mat = redMesh.LocalMaterialBuffer.Materials[matEntry.Index];
                    else if (matEntry.Index < redMesh.PreloadLocalMaterialInstances.Count)
                        mat = redMesh.PreloadLocalMaterialInstances[matEntry.Index ]!;
                    else
                    {
                        Console.WriteLine($"Local Material {matEntry.Index} not found!");
                        textures[chunkIndex] = _fallbackImage;
                        chunkIndex++;
                        continue;
                    }
                }
                else
                {
                    CResourceReference<IMaterial>? matRef = null;
                    CResourceAsyncReference<IMaterial>? asyncMatRef = null;
                    if (matEntry.Index < redMesh.ExternalMaterials.Count)
                        asyncMatRef = redMesh.ExternalMaterials[matEntry.Index];
                    else if (matEntry.Index < redMesh.PreloadExternalMaterials.Count)
                        matRef = redMesh.PreloadExternalMaterials[matEntry.Index]!;
                    else
                    {
                        Console.WriteLine($"External Material {matEntry.Index} not found!");
                        textures[chunkIndex] = _fallbackImage;
                        chunkIndex++;
                        continue;
                    }
                    
                    var matRefPath = matRef?.DepotPath ?? asyncMatRef?.DepotPath ?? ""; 
                    
                    mat = (IMaterial)GetEmbeddedORArchiveRootChunk(archiveManager, meshFile, matRefPath);
                }

                if (mat is not CMaterialInstance matInst)
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
                
                var xbmRC = GetEmbeddedORArchiveRootChunk(archiveManager, meshFile, texRef.DepotPath);
                if (xbmRC is not CBitmapTexture xbm)
                {
                    Console.WriteLine($"Failed to get texture {texRef.DepotPath} from archive and embedded files!");
                    textures[chunkIndex] = _fallbackImage;
                    chunkIndex++;
                    continue;
                }

                var ri = RedImage.FromXBM(xbm);
                try
                {
                    textures[chunkIndex] = ri.GetPreview(true);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Failed to get preview for {texRef.DepotPath}:");
                    Console.WriteLine(e);
                    textures[chunkIndex] = _fallbackImage;
                }
                
                chunkIndex++;
            }
        }
        
        return bMesh;
    }
    
    private static int DetermineLowestQualityLOD(rendRenderMeshBlob rendBlob)
    {
        var lowestLod = 1;
        var rendInfos = rendBlob.Header.RenderChunkInfos;
        foreach (var rendInfo in rendInfos)
        {
            if (rendInfo.LodMask > lowestLod)
                lowestLod = rendInfo.LodMask;
        }
        
        return lowestLod;
    }
    
    private static MeshDataCount CountMeshDataAtLOD(rendRenderMeshBlob rendBlob, int lodMask)
    {
        uint numVerts = 0;
        uint numIndices = 0;
        uint numSubMeshes = 0;
        var submesheIndicesAtLOD = new List<int>();

        var i = 0;
        foreach (var rendInfo in rendBlob.Header.RenderChunkInfos)
        {
            if (rendInfo.LodMask != lodMask)
            {
                i++;
                continue;
            }

            submesheIndicesAtLOD.Add(i);
            
            numVerts += rendInfo.NumVertices;
            numIndices += rendInfo.NumIndices;
            numSubMeshes++;
            i++;
        }
        
        return new MeshDataCount(numVerts, numIndices, numSubMeshes, submesheIndicesAtLOD);   
    }

    private static byte[] GetFallbackImage(IArchiveManager archiveManager)
    {
        var file = archiveManager.GetCR2WFile(@"base\vehicles\special\av_zetatech_bombus\entities\meshes\textures\av_zetatech_bombus__ext02_nanny_dislpay_pink_b.xbm");
        if (file is not { RootChunk: CBitmapTexture xbm })
            throw new Exception("Failed to get fallback image!");
        
        return RedImage.FromXBM(xbm).GetPreview(false);
    }

    private static RedBaseClass? GetEmbeddedORArchiveRootChunk(IArchiveManager archiveManager, CR2WFile parent, string path)
    {
        var embeddedFile = parent.EmbeddedFiles.FirstOrDefault(efile => efile.FileName == path);
        if (embeddedFile is not null)
            return embeddedFile.Content;
        
        var archiveFile = archiveManager.GetCR2WFile(path);
        if (archiveFile is not null)
            return archiveFile.RootChunk;
        
        return null;
    }
}