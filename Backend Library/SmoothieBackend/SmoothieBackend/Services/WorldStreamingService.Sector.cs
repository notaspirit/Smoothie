using System.Collections.Concurrent;
using SharpDX;
using SmoothieBackend.Extensions;
using SmoothieBackend.Helpers;
using SmoothieBackend.Models;
using WolvenKit.RED4.Archive.CR2W;
using WolvenKit.RED4.CR2W.Archive;
using WolvenKit.RED4.Types;

namespace SmoothieBackend.Services;

public partial class WorldStreamingService
{
    private readonly List<SectorDescriptor> _sectorDescriptors = new();
    private readonly ConcurrentDictionary<string, Node[]> _loadedSectors = new();
    
    private readonly ConcurrentDictionary<string, byte> _activeSectors = new();
    
    private readonly BlockingWorkQueue<string> _sectorLoadQueue = new(false);
    private readonly BlockingWorkQueue<string> _sectorUnloadQueue = new(false);
    
    private readonly BlockingWorkQueue<string> _processNodeStreamingDistances = new(true);
    
    private readonly WorkQueue<NodeID> _blenderNodeLoadQueue = new(false);
    private readonly WorkQueue<NodeID> _blenderNodeUnloadQueue = new(false);

    #region Blender Node Queue
    
    private void ConsumeAddedNodesQueue()
    {
        while (_blenderNodeLoadQueue.TryDequeue(out var nodeId))
        {
            if (!_loadedSectors.TryGetValue(nodeId.ParentSector, out var sector) ||
                nodeId.NodeDataIndex > sector.Length)
            {
                _blenderNodeLoadQueue.Done(nodeId);
                continue;
            }

            var node = sector[nodeId.NodeDataIndex];
            if (!node.IsStreaming)
            {
                _blenderNodeLoadQueue.Done(nodeId);
                continue;
            }
            
            _blenderNodeLoadQueue.Done(nodeId);
            _streamResult.AddedNodes.Add(node);
        }
    }
    
    private void ConsumeRemovedNodesQueue()
    {
        while (_blenderNodeUnloadQueue.TryDequeue(out var nodeId))
        {
            if (_loadedSectors.TryGetValue(nodeId.ParentSector, out var sector))
            {
                if (nodeId.NodeDataIndex < sector.Length && !sector[nodeId.NodeDataIndex].IsStreaming)
                {
                    _blenderNodeUnloadQueue.Done(nodeId);
                    _streamResult.RemovedNodes.Add(nodeId);
                    continue;
                }
                _blenderNodeUnloadQueue.Done(nodeId);
                continue;
            }

            _blenderNodeUnloadQueue.Done(nodeId);
            _streamResult.RemovedNodes.Add(nodeId);
        }
    }
    
    #endregion
    
    #region Check Streamingdistances
    private void CheckSectors()
    {
        var perThreadSectors = _sectorDescriptors.Count / 6;
        
        var tasks = new List<Task>();
        
        for (var i = 0; i < 6; i++)
        {
            var startIndex = i * perThreadSectors;
            var endIndex = startIndex + perThreadSectors;
            if (i == 6 - 1)
                endIndex = _sectorDescriptors.Count;
            tasks.Add(Task.Run(() => CheckSectorsInRange(startIndex, endIndex)));
        }
        
        Task.WaitAll(tasks);

        return;
        
        void CheckSectorsInRange(int startIndex, int endIndex)
        {
            for (var i = startIndex; i < endIndex; i++)
            {
                var sector = _sectorDescriptors[i];
                if (sector.BoundingBox.Contains(_streamingPoint) != ContainmentType.Disjoint)
                {
                    if (_activeSectors.TryAdd(sector.Path, 0))
                        _sectorLoadQueue.Enqueue(sector.Path);
                }
                else
                {
                    if (_activeSectors.TryRemove(sector.Path, out _))
                        _sectorUnloadQueue.Enqueue(sector.Path);
                }
            }
        }
    }

    private void ProcessNodeStreamingDistances(CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            var sectorPath = _processNodeStreamingDistances.Dequeue(ct);
            
            if (!_loadedSectors.TryGetValue(sectorPath, out var sector))
            {
                _processNodeStreamingDistances.Done(sectorPath);
                continue;
            }
            
            foreach (var node in sector)
            {
                if (node.MeshPath is null /* ||
                    node.NodeType.IsAssignableTo(typeof(worldPrefabProxyMeshNode)) */)
                {
                    node.IsStreaming = false;
                    continue;
                }
                
                var previous = node.IsStreaming;
                node.IsStreaming = node.Position.Contains(ref _streamingPoint) != ContainmentType.Disjoint;
                
                if (node is { IsStreaming: true, NearAutoHide: not null })
                    node.IsStreaming = node.NearAutoHide?.Contains(ref _streamingPoint) == ContainmentType.Disjoint;
                
                if (previous != node.IsStreaming && node.IsStreaming)
                {
                    if (node.MeshPath is not null)
                    {
                        // if (_nodeCount++ > MaxNodes)
                        //    break;
                        
                        var refs = _activeMeshes.GetOrAdd(node.MeshPath, new ConcurrentDictionary<NodeID, byte>());
                        refs.TryAdd(node.Id, 0);
                        if (refs.Count == 1)
                            _meshLoadQueue.Enqueue(node.MeshPath);
                    }
                    _blenderNodeLoadQueue.Enqueue(node.Id);
                }
                else if (previous != node.IsStreaming && !node.IsStreaming)
                {
                    if (node.MeshPath is not null && _activeMeshes.TryGetValue(node.MeshPath, out var refs))
                    {
                        refs.TryRemove(node.Id, out _);
                        if (refs.IsEmpty)
                        {
                            _activeMeshes.TryRemove(node.MeshPath, out _);
                            _meshUnloadQueue.Enqueue(node.MeshPath);
                        }
                    }
                    
                    _blenderNodeUnloadQueue.Enqueue(node.Id);
                }
            }
            
            _processNodeStreamingDistances.Done(sectorPath);
        }
    }
    
    #endregion
    
    #region Sector IO

    private void LoadSectorFromQueue(CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            var sectorPath = _sectorLoadQueue.Dequeue(ct);
            
            if (_loadedSectors.ContainsKey(sectorPath) || !_activeSectors.ContainsKey(sectorPath))
            {
                _sectorLoadQueue.Done(sectorPath);
                continue;
            }
            
            var sectorFile = _archiveManager.GetCR2WFile(sectorPath);
            if (sectorFile is null)
            {
                _sectorLoadQueue.Done(sectorPath);
                continue;
            }
            
            _embeddedMeshes.AddEmbeddedFiles(sectorFile, sectorPath, ProcessEmbeddedMesh);
            
            var nodes = _sectorParser.Parse(_archiveManager, sectorPath, sectorFile);

            if (nodes is null)
            {
                _sectorLoadQueue.Done(sectorPath);
                continue;
            }

            _loadedSectors.TryAdd(sectorPath, nodes);
            _sectorLoadQueue.Done(sectorPath);
            _processNodeStreamingDistances.Enqueue(sectorPath);
        }
    }

    private BlenderMesh? ProcessEmbeddedMesh(RedBaseClass redBase)
    {
        if (redBase is not CMesh mesh)
            return null;

        var (bmesh, mats) = _meshParser.Parse(new CR2WFile { RootChunk = mesh });
        
        if (mats is not null)
            foreach(var mat in mats)
                _materialLoadQueue.Enqueue(mat);
            
        return bmesh;
    }

    private async Task UnloadSectorFromQueue(CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            var sectorPath = _sectorUnloadQueue.Dequeue(ct);
            
            if (_activeSectors.ContainsKey(sectorPath) || !_loadedSectors.TryRemove(sectorPath, out var sector))
                continue;

            foreach (var node in sector)
                if (node.IsStreaming)
                    _blenderNodeUnloadQueue.Enqueue(node.Id);
            
            _embeddedMeshes.RemoveEmbeddedFiles(sectorPath);
            
            _sectorUnloadQueue.Done(sectorPath);
        }
    }

    private void LoadSectorDescriptors(string blockPath)
    {
        var file = _archiveManager.GetCR2WFile(blockPath);
        
        if (file is not { RootChunk: worldStreamingBlock block })
            return;

        foreach (var desc in block.Descriptors)
        {
            _sectorDescriptors.Add(new SectorDescriptor()
            {
                Path = desc.Data.DepotPath,
                BoundingBox = desc.StreamingBox.ToSDX()
            });
        }
    }
    
    #endregion
}
