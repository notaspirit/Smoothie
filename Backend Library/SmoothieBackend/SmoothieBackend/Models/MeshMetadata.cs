using WolvenKit.Modkit.RED4.GeneralStructs;
using WolvenKit.Modkit.RED4.Tools;
using WolvenKit.RED4.Types;

namespace SmoothieBackend.Models;

public class MeshMetadata
{
    public uint LowestLod { get; set; }
    public List<int> SubmeshesAtLod { get; set; } = [];
    public uint NumVertices { get; set; }
    public uint NumIndices { get; set; }
    public MeshesInfo MeshesInfo { get; set; } = new MeshesInfo(0);

    public static MeshMetadata BuildMeshMetadata(CMesh mesh, rendRenderMeshBlob rendBlob)
    {
        var md = new MeshMetadata();
        md.LowestLod = DetermineLowestQualityLOD(rendBlob);
        (md.NumVertices, md.NumIndices, md.SubmeshesAtLod) = CountMeshDataAtLOD(rendBlob, md.LowestLod);
        md.MeshesInfo = MeshTools.GetMeshesinfo(rendBlob, mesh, "meshName?");
        return md;
    }
    
    private static uint DetermineLowestQualityLOD(rendRenderMeshBlob rendBlob)
    {
        uint lowestLod = 1;
        var rendInfos = rendBlob.Header.RenderChunkInfos;
        foreach (var rendInfo in rendInfos)
        {
            if (rendInfo.LodMask > lowestLod)
                lowestLod = rendInfo.LodMask;
        }
        
        return lowestLod;
    }
    
    private static (uint numVerts, uint numIndicies, List<int> submeshesAtLod)
        CountMeshDataAtLOD(rendRenderMeshBlob rendBlob, uint lodMask)
    {
        uint numVerts = 0;
        uint numIndices = 0;
        var submeshIndicesAtLod = new List<int>();

        var i = 0;
        foreach (var rendInfo in rendBlob.Header.RenderChunkInfos)
        {
            if (rendInfo.LodMask != lodMask)
            {
                i++;
                continue;
            }

            submeshIndicesAtLod.Add(i);
            
            numVerts += rendInfo.NumVertices;
            numIndices += rendInfo.NumIndices;
            i++;
        }
        
        return (numVerts, numIndices, submeshIndicesAtLod);   
    }
}