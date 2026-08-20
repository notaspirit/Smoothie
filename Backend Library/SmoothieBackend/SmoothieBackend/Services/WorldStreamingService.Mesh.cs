using System.Collections.Concurrent;
using SmoothieBackend.Components;
using SmoothieBackend.Extensions;
using SmoothieBackend.Helpers;
using SmoothieBackend.Models;
using WolvenKit.RED4.Archive.CR2W;
using WolvenKit.RED4.CR2W;
using WolvenKit.RED4.Types;

namespace SmoothieBackend.Services;

public partial class WorldStreamingService
{
    private readonly ConcurrentDictionary<string, BlenderMesh?> _loadedMeshes = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<NodeID, byte>> _activeMeshes = new();
    private readonly EmbeddedFilesStore<BlenderMesh> _embeddedMeshes = new();
    
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

            var bMesh = _embeddedMeshes.GetEmbeddedFile(meshPath);

            if (bMesh is null)
            {
                var cr2W = _archiveManager.GetCR2WFile(meshPath);
                if (cr2W is not null)
                {
                    _embeddedMaterials.AddEmbeddedFiles(cr2W, meshPath, ProcessEmbeddedTexture);
                    (bMesh, var mats) = _meshParser.Parse(cr2W);
                    if (mats is not null)
                        foreach (var mat in mats)
                        {
                            var refs = _activeMaterials.GetOrAdd(mat, new ConcurrentDictionary<string, byte>());
                            refs.TryAdd(meshPath, 0);
                            if (refs.Count == 1)
                                _materialLoadQueue.Enqueue(mat);
                        }
                }
            }
            
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

    private DeferredDeserializedTexture? ProcessEmbeddedTexture(RedBaseClass redBase)
    {
        if (redBase is not CBitmapTexture texture)
            return null;

        return new DeferredDeserializedTexture { Raw = texture };
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
            
            if (_loadedMeshes.TryRemove(meshPath, out var mesh))
            {
                _blenderMeshUnloadQueue.Enqueue(meshPath);
                _embeddedMaterials.RemoveEmbeddedFiles(meshPath);
                
                if (mesh is not null)
                    foreach (var mat in mesh.Textures.SelectMany(app => app.Value))
                    {
                        if (!_activeMaterials.TryGetValue(mat, out var refs)) 
                            continue;
                            
                        refs.TryRemove(meshPath, out _);
                            
                        if (!refs.IsEmpty)
                            continue;
                            
                        _activeMaterials.TryRemove(mat, out _);
                        _materialUnloadQueue.Enqueue(mat);
                    }
            }
            _meshUnloadQueue.Done(meshPath);
        }
    }

    #endregion
}
