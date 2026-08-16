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
    
    private readonly ConcurrentDictionary<string, ConcurrentBag<TimeSpan>> _debugTimes = new(); 
    

    public BlenderMeshParser(IArchiveManager archiveManager, MaterialParser materialParser)
    {
        _archiveManager = archiveManager;
        _materialParser = materialParser;
    }

    public void LogDebugTimes()
    {
        var averageTimes = _debugTimes.ToDictionary(kvp => kvp.Key,
            kvp => kvp.Value.Count > 0 ? kvp.Value.Average(t => t.TotalMilliseconds) : 0);

        Log("e2e mesh parse", "", 0);

        return;
        
        void Log(string name, string parentName, int indent)
        {
            if (!averageTimes.TryGetValue(name, out var time) || time == 0) return;

            var indentStr = new string(' ', indent * 2);
            var pctStr = "";
            if (!string.IsNullOrEmpty(parentName) && averageTimes.TryGetValue(parentName, out var parentTime) && parentTime > 0)
            {
                var pct = (time / parentTime) * 100;
                pctStr = $" ({pct:F2}%)";
            }

            Console.WriteLine($"{indentStr}{name}: {time:F2}ms{pctStr}");

            // Define children for each node
            switch (name)
            {
                case "e2e mesh parse":
                    Log("parse geometry", name, indent + 1);
                    Log("e2e material chunk", name, indent + 1);
                    break;
                case "e2e material chunk":
                    Log("get material", name, indent + 1);
                    Log("get flat material", name, indent + 1);
                    Log("e2e get multilayered material", name, indent + 1);
                    Log("e2e get metal base material", name, indent + 1);
                    break;
                case "e2e get multilayered material":
                    Log("convert mask layer to png", name, indent + 1);
                    Log("bake multilayered material", name, indent + 1);
                    Log("save canvas", name, indent + 1);
                    break;
                case "bake multilayered material":
                    Log("e2e mls layer", name, indent + 1);
                    break;
                case "e2e mls layer":
                    Log("get template", name, indent + 1);
                    Log("get png", name, indent + 1);
                    Log("decode color bitmap in layer", name, indent + 1);
                    Log("apply mask to layer", name, indent + 1);
                    Log("draw to main canvas", name, indent + 1);
                    break;
            }
        }
    }
    
    public BlenderMesh? Parse(string path)
    {
        var meshFile = _archiveManager.GetCR2WFile(path);
        return meshFile is not null ? Parse(meshFile) : null;
    }

    public BlenderMesh? Parse(CR2WFile meshFile)
    {
        var meshSw = new TrackedStopWatch("e2e mesh parse", _debugTimes);
        
        if (meshFile is not  { RootChunk: CMesh { RenderResourceBlob.Chunk: rendRenderMeshBlob rendBlob } redMesh })
        {
            meshSw.Stop(false);
            return null;
        }
        
        var geoSw = new TrackedStopWatch("parse geometry", _debugTimes);
        var meshMd = MeshMetadata.BuildMeshMetadata(redMesh, rendBlob);
        var bMesh = ParseGeometryData(redMesh, meshMd);
        
        if (bMesh is null)
        {
            meshSw.Stop(false);
            geoSw.Stop(false);
            return null;
        }
        
        geoSw.Stop();
        
        if (!_materialParser.BakeMaterials(bMesh, meshMd, meshFile))
        {
            meshSw.Stop(false);
            return null;
        }
        
        meshSw.Stop();
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
        bMesh.Textures = new Dictionary<string, BlenderTexture[]>();
        
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