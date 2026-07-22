using SharpDX;
using SmoothieBackend.Extensions;
using SmoothieBackend.Helpers;
using SmoothieBackend.Models;
using WolvenKit.Common;
using WolvenKit.RED4.Archive.Buffer;
using WolvenKit.RED4.Types;
using Quaternion = SharpDX.Quaternion;
using Vector3 = SharpDX.Vector3;

namespace SmoothieBackend.Parsers;

public class StreamingSectorParser
{
    private readonly IArchiveManager _archiveManager;
    
    public StreamingSectorParser(IArchiveManager archiveManager)
    {
        _archiveManager = archiveManager;
    }
    
    public Node[]? Parse(IArchiveManager archiveManager, string sectorPath)
    {
        var sectorFile = archiveManager.GetCR2WFile(sectorPath);
        if (sectorFile is not { RootChunk: worldStreamingSector { NodeData.Data: worldNodeDataBuffer nodeData } sector })
            return null;
        
        var outNodes = new Node[nodeData.Count];
            
        var i = 0;
        foreach (var node in nodeData)
        {
            string? meshPath = null;
            string? meshAppearance = null;
            BoundingSphere? nearAutoHide = null;
            Node[]? instances = null;

            switch (sector.Nodes[node.NodeIndex].Chunk)
            {
                case worldGenericProxyMeshNode proxyMeshNode:
                    nearAutoHide =
                        new BoundingSphere(node.Position.ToSDX().ToVector3(), proxyMeshNode.NearAutoHideDistance);
                    meshPath = proxyMeshNode.Mesh.DepotPath;
                    meshAppearance = proxyMeshNode.MeshAppearance;
                    break;
                case worldMeshNode meshNode:
                    meshPath = meshNode.Mesh.DepotPath;
                    meshAppearance = meshNode.MeshAppearance;
                    break;
                case worldTerrainMeshNode terrainMeshNode:
                    meshPath = terrainMeshNode.MeshRef.DepotPath;
                    meshAppearance = "default";
                    break;
                case worldInstancedMeshNode instancedMeshNode:
                    meshPath = instancedMeshNode.Mesh.DepotPath;
                    meshAppearance = instancedMeshNode.MeshAppearance;
                    
                    instances = new Node[instancedMeshNode.WorldTransformsBuffer.NumElements];
                    var transformsInstancedBuffer = instancedMeshNode.WorldTransformsBuffer.SharedDataBuffer.Chunk.Buffer.Data as WorldTransformsBuffer;
                    for (var ti = 0; ti < instancedMeshNode.WorldTransformsBuffer.NumElements; ti++)
                    {
                        var transform = transformsInstancedBuffer.Transforms[
                            (int)(ti + instancedMeshNode.WorldTransformsBuffer.StartIndex)];
                        instances[ti] = new Node
                        {
                            Id = new NodeID(sectorPath, i, ti),
                            Position = new BoundingSphere(transform.Translation.ToSDX(), 0),
                            Rotation = transform.Rotation.ToEulerAnglesRadian(),
                            Scale = transform.Scale.ToSDX(),
                            IsStreaming = false,
                            MeshPath = meshPath,
                            MeshAppearance = meshAppearance
                        };
                    }
                    break;
                case worldFoliageNode foliageNode:
                    var foliageBuffer = GetFoliageBuffer(foliageNode);
                    if (foliageBuffer is null)
                        break;
                    
                    meshPath = foliageNode.Mesh.DepotPath;
                    meshAppearance = foliageNode.MeshAppearance;
                    instances = new Node[foliageNode.PopulationSpanInfo.StancesCount];
                    for (var fti = 0; fti < foliageNode.PopulationSpanInfo.StancesCount; fti++)
                    {
                        var transform = foliageBuffer.Populations[(int)(fti + foliageNode.PopulationSpanInfo.StancesBegin)];
                        instances[fti] = new Node
                        {
                            Id = new NodeID(sectorPath, i, fti),
                            Position = new BoundingSphere(transform.Position.ToSDX(), 0),
                            Rotation = new WolvenKit.RED4.Types.Quaternion()
                            {
                                I = transform.Rotation.X,
                                J = transform.Rotation.Y,
                                K = transform.Rotation.Z,
                                R = transform.Rotation.W
                            }.ToEulerAnglesRadian(),
                            Scale = new Vector3(transform.Scale),
                            IsStreaming = false,
                            MeshPath = meshPath,
                            MeshAppearance = meshAppearance
                        };
                    }
                    break;
            }

                
            outNodes[i] = new Node
            {
                Id = new NodeID(sectorPath, i),
                Position = new BoundingSphere( node.Position.ToSDX().ToVector3(), node.UkFloat1),
                NearAutoHide = nearAutoHide,
                Scale = node.Scale.ToSDX(),
                Rotation = node.Orientation.ToEulerAnglesRadian(),
                IsStreaming = false,
                MeshPath = meshPath,
                MeshAppearance = meshAppearance,
                Instances = instances
            };
                
            i++;
        }
        
        return outNodes;

        FoliageBuffer? GetFoliageBuffer(worldFoliageNode foliageNode)
        {
            if (foliageNode.FoliageResource.Flags == InternalEnums.EImportFlags.Embedded)
            {
                var foliageResourceFile = sectorFile.EmbeddedFiles.FirstOrDefault(efile =>
                    efile.FileName == foliageNode.FoliageResource.DepotPath);
                
                if (foliageResourceFile?.Content is not worldFoliageCompiledResource wfcr)
                {
                    Console.WriteLine($"Embedded resource {foliageNode.FoliageResource.DepotPath} is not worldFoliageCompiledResource!");
                    return null;
                }
            
                if (wfcr.DataBuffer.Data is not FoliageBuffer foliagebuffer)
                {
                    Console.WriteLine("Failed to process worldFoliage resource, Data is not FoliageBuffer!");
                    return null;
                }

                return foliagebuffer;
            }
            
            var foliageCR2W = archiveManager.GetCR2WFile(foliageNode.FoliageResource.DepotPath);
            if (foliageCR2W is null)
            {
                Console.WriteLine($"Failed to get {foliageNode.FoliageResource.DepotPath} from archive files!");
                return null;
            }

            if (foliageCR2W.RootChunk is not worldFoliageCompiledResource { DataBuffer.Data: FoliageBuffer foliageBuffer })
            {
                Console.WriteLine($"Failed to get {foliageNode.FoliageResource.DepotPath} is not worldFoliageCompiledResource!");
                return null;
            }
            
            return foliageBuffer;
        }
    }
}