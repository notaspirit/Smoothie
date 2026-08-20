using System.Collections.Concurrent;
using SkiaSharp;
using SmoothieBackend.Components;
using SmoothieBackend.Models;
using SmoothieBackend.Parsers;

namespace SmoothieBackend.Services;

public partial class WorldStreamingService
{
    private readonly ConcurrentDictionary<MaterialID, BlenderTexture?> _loadedMaterials = new();
    private readonly ConcurrentDictionary<MaterialID, ConcurrentDictionary<string, byte>> _activeMaterials = new();
    
    private readonly BlockingWorkQueue<MaterialID> _materialLoadQueue = new(false);
    private readonly BlockingWorkQueue<MaterialID> _materialUnloadQueue = new(false);
    
    private readonly WorkQueue<MaterialID> _blenderMaterialLoadQueue = new(false);
    private readonly WorkQueue<MaterialID> _blenderMaterialUnloadQueue = new(false);
    
    private readonly EmbeddedFilesStore<DeferredDeserializedTexture> _embeddedMaterials = new();

    private void AddFallbackMaterial()
    {
        var matId = new MaterialID { AlbedoPath = "fallback" };
        var texture = _materialParser.GetFilledSquareBlenderTexture(SKColors.DeepPink);
        
        _loadedMaterials.TryAdd(matId, texture);
        
        var refs = _activeMaterials.GetOrAdd(matId, new ConcurrentDictionary<string, byte>());
        refs.TryAdd("fallback", 0);
        
        _blenderMaterialLoadQueue.Enqueue(matId);
    }
    
    #region Blender Material Queue

    private void ConsumeAddedMaterialsQueue()
    {
        while (_blenderMaterialLoadQueue.TryDequeue(out var materialId))
        {
            if (!_loadedMaterials.TryGetValue(materialId, out var mat) || mat is null)
            {
                _blenderMaterialLoadQueue.Done(materialId);
                continue;
            }
            
            _blenderMaterialLoadQueue.Done(materialId);
            _loadedMaterials[materialId] = null;
            _streamResult.AddedTextures.Add(mat);
        }
    }
    
    private void ConsumeRemovedMaterialsQueue()
    {
        while (_blenderMaterialUnloadQueue.TryDequeue(out var materialId))
        {
            if (_loadedMaterials.ContainsKey(materialId) || _activeMaterials.ContainsKey(materialId))
            {
                _blenderMaterialUnloadQueue.Done(materialId);
                continue;
            }
            
            _blenderMaterialUnloadQueue.Done(materialId);
            _streamResult.RemovedTextures.Add(materialId);
        }
    }
    
    #endregion
    
    #region Material IO

    private void LoadMaterialFromQueue(CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            var materialId = _materialLoadQueue.Dequeue(ct);
            
            if (_loadedMaterials.ContainsKey(materialId) || !_activeMaterials.ContainsKey(materialId))
            {
                _materialLoadQueue.Done(materialId);
                continue;
            }

            BlenderTexture? bTexture;
            if (materialId.AlbedoPath is not null)
            {
                var defTex = _embeddedMaterials.GetEmbeddedFile(materialId.AlbedoPath);
                if (defTex is null)
                    bTexture = _materialParser.GetXbmAsBlenderTexture(materialId);
                else
                {
                    MaterialParser.UncookDeferredTexture(defTex);

                    bTexture = defTex.Texture;
                }
            }
            else if (materialId.MlMaskPath is not null && materialId.MlSetupPath is not null)
                bTexture = _materialParser.BakeMultilayeredMaterial(materialId);
            else
            {
                Console.WriteLine("Material is not Multilayered and has no Albedo!");
                _materialLoadQueue.Done(materialId);
                continue;
            }
            
            if (bTexture is null)
            {
                _materialLoadQueue.Done(materialId);
                continue;
            }
            
            bTexture.Id = materialId;

            if (_loadedMaterials.ContainsKey(materialId) || !_activeMaterials.ContainsKey(materialId))
            {
                _materialLoadQueue.Done(materialId);
                continue;
            }

            _loadedMaterials.TryAdd(materialId, bTexture);
            _materialLoadQueue.Done(materialId);
            _blenderMaterialLoadQueue.Enqueue(materialId);
            
        }
    }

    private void UnloadMaterialFromQueue(CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            var materialId = _materialUnloadQueue.Dequeue(ct);
            
            if (!_loadedMaterials.ContainsKey(materialId) || _activeMaterials.ContainsKey(materialId))
            {
                _materialUnloadQueue.Done(materialId);
                continue;
            }
            
            if (_loadedMaterials.TryRemove(materialId, out _))
                _blenderMaterialUnloadQueue.Enqueue(materialId);
            
            _materialUnloadQueue.Done(materialId);
        }
    }
    
    #endregion
}