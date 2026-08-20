using System.Collections.Concurrent;
using SmoothieBackend.Models;
using WolvenKit.Common;
using WolvenKit.RED4.Types;
using WolvenKit.Modkit.RED4.GeneralStructs;
using WolvenKit.Modkit.RED4.Tools;
using WolvenKit.RED4.Archive.CR2W;
using SmoothieBackend.Components;
using Vector4 = SharpDX.Vector4;

namespace SmoothieBackend.Parsers;

public class BlenderMeshParser
{
    private readonly IArchiveManager _archiveManager;
    private readonly MaterialParser _materialParser;

    public BlenderMeshParser(IArchiveManager archiveManager, MaterialParser materialParser)
    {
        _archiveManager = archiveManager;
        _materialParser = materialParser;
    }
    
    public (BlenderMesh?, HashSet<MaterialID>?) Parse(string path) 
    {
        var meshFile = _archiveManager.GetCR2WFile(path);
        return meshFile is not null ? Parse(meshFile) : (null, null);
    }

    public (BlenderMesh?, HashSet<MaterialID>?) Parse(CR2WFile meshFile)
    {
        if (meshFile is not  { RootChunk: CMesh { RenderResourceBlob.Chunk: rendRenderMeshBlob rendBlob } redMesh })
            return (null, null);
        
        var meshMd = MeshMetadata.BuildMeshMetadata(redMesh, rendBlob);
        var bMesh = ParseGeometryData(redMesh, meshMd);
        
        if (bMesh is null)
            return (null, null);

        var materialIds = _materialParser.ParseMaterials(bMesh, meshMd, meshFile);
        
        return (bMesh, materialIds);
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
        bMesh.Textures = new Dictionary<string, MaterialID[]>();
        
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
}