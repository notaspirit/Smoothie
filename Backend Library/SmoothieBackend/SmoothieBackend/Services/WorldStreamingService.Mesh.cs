using System.Collections.Concurrent;
using SmoothieBackend.Helpers;
using SmoothieBackend.Models;
using WolvenKit.RED4.Archive.CR2W;

namespace SmoothieBackend.Services;

public partial class WorldStreamingService
{
    private readonly ConcurrentDictionary<string, BlenderMesh?> _loadedMeshes = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<NodeID, byte>> _activeMeshes = new();
    private readonly ConcurrentDictionary<string, BlenderMesh> _embeddedMeshes = new();
    
    private readonly BlockingWorkQueue<string> _meshLoadQueue = new(false);
    private readonly BlockingWorkQueue<string> _meshUnloadQueue = new(false);
    
    private readonly WorkQueue<string> _blenderMeshLoadQueue = new(false);
    private readonly WorkQueue<string> _blenderMeshUnloadQueue = new(false);

    #region Blender Mesh Queue
    
    private void ConsumeAddedMeshesQueue()
    {
        while (_blenderMeshLoadQueue.TryDequeue(out var meshPath))
        {
            if (!_loadedMeshes.TryGetValue(meshPath, out var mesh) || mesh is null)
            {
                _blenderMeshLoadQueue.Done(meshPath);
                continue;
            }

            _blenderMeshLoadQueue.Done(meshPath);
            _loadedMeshes[meshPath] = null;
            _streamResult.AddedMeshes.Add(mesh);
        }
    }
    
    private void ConsumeRemovedMeshesQueue()
    {
        while (_blenderMeshUnloadQueue.TryDequeue(out var meshPath))
        {
            if (_loadedMeshes.ContainsKey(meshPath) || _activeMeshes.ContainsKey(meshPath))
            {
                _blenderMeshUnloadQueue.Done(meshPath);
                continue;
            }
            
            _blenderMeshUnloadQueue.Done(meshPath);
            _streamResult.RemovedMeshes.Add(meshPath);
        }
    }
    
    #endregion

    #region Mesh IO

    private void LoadMeshFromQueue(CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            var meshPath = _meshLoadQueue.Dequeue(ct);

            if (_loadedMeshes.ContainsKey(meshPath) || !_activeMeshes.ContainsKey(meshPath))
            {
                _meshLoadQueue.Done(meshPath);
                continue;
            }

            BlenderMesh? bMesh;
            if (_embeddedMeshes.TryGetValue(meshPath, out var ebMesh))
                bMesh = ebMesh;
            else
                bMesh = _meshParser.Parse(meshPath);
            
            if (bMesh is null)
            {
                _meshLoadQueue.Done(meshPath);
                continue;
            }
            
            bMesh.Path = meshPath;
            
            if (_loadedMeshes.ContainsKey(meshPath) || !_activeMeshes.ContainsKey(meshPath))
            {
                _meshLoadQueue.Done(meshPath);
                continue;
            }
            
            _loadedMeshes.TryAdd(meshPath, bMesh);
            _meshLoadQueue.Done(meshPath);
            _blenderMeshLoadQueue.Enqueue(meshPath);
        }
    }
    
    private void UnloadMeshFromQueue(CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            var meshPath = _meshUnloadQueue.Dequeue(ct);

            if (!_loadedMeshes.ContainsKey(meshPath) || _activeMeshes.ContainsKey(meshPath))
            {
                _meshUnloadQueue.Done(meshPath);
                continue;
            }
            
            if (_loadedMeshes.TryRemove(meshPath, out _))
                _blenderMeshUnloadQueue.Enqueue(meshPath);
            _meshUnloadQueue.Done(meshPath);
        }
    }

    #endregion
}
