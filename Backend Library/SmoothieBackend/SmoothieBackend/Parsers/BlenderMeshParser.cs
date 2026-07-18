using SmoothieBackend.Models;
using WolvenKit.RED4.Types;

using Vector4 = SharpDX.Vector4;

namespace SmoothieBackend.Parsers;

public static class BlenderMeshParser
{
    private record MeshDataCount(uint NumVertices, uint NumIndices);
    
    public static BlenderMesh? Parse(CMesh redMesh)
    {
        if (redMesh is not  { RenderResourceBlob.Chunk: rendRenderMeshBlob rendBlob })
            return null;
        
        var lowestLod = DetermineLowestQualityLOD(rendBlob);
        
        var dataSize = CountMeshDataAtLOD(rendBlob, lowestLod);
        var bMesh = new BlenderMesh();
        bMesh.Vertices = new float[dataSize.NumVertices * 3];
        bMesh.Indices = new uint[dataSize.NumIndices];
        
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

            br.BaseStream.Position = rendBlob.Header.IndexBufferOffset + rendInfo.ChunkIndices.TeOffset;
            for (var indexIndex = 0; indexIndex < rendInfo.NumIndices; indexIndex++)
            {
                bMesh.Indices[globalIndexIndex] = (uint)(br.ReadUInt16() + indexOffset);
                globalIndexIndex++;
            }

            indexOffset += rendInfo.NumVertices;
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
        foreach (var rendInfo in rendBlob.Header.RenderChunkInfos)
        {
            if (rendInfo.LodMask != lodMask) 
                continue;

            numVerts += rendInfo.NumVertices;
            numIndices += rendInfo.NumIndices;
        }
        
        return new MeshDataCount(numVerts, numIndices);   
    }
}