using System.Collections.Concurrent;
using SmoothieBackend.Components;
using SmoothieBackend.Models;
using SmoothieBackend.Parsers;
using WolvenKit;
using WolvenKit.Common;
using WolvenKit.Common.Services;
using WolvenKit.Core.Interfaces;
using WolvenKit.Core.Services;
using WolvenKit.Modkit.RED4;
using WolvenKit.RED4.CR2W;
using WolvenKit.RED4.CR2W.Archive;
using WolvenKit.RED4.Types;
using Vector3 = SharpDX.Vector3;

namespace SmoothieBackend.Services;

public partial class WorldStreamingService
{
    private const string BlockPath = @"base\worlds\03_night_city\_compiled\default\blocks\all.streamingblock";
    private const string GameExe = @"E:\Games\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe";

    private const int ThreadCount = 18;
    
    private readonly IArchiveManager _archiveManager;
    private readonly IHashService _hashService;
    private readonly Red4ParserService _parserService;
    private readonly ILoggerService _dummyLogger;
    private readonly IHookService _hookService;
    private readonly IProgressService<double> _progressService;
    private readonly ModTools _modTools;

    private readonly MaterialParser _materialParser;
    private readonly BlenderMeshParser _meshParser;
    private readonly StreamingSectorParser _sectorParser;
    
    private Vector3 _streamingPoint;
    
    private bool _isStreaming = false;
    private CancellationTokenSource? _cts = null;
    
    private bool _doneStreaming = false;
    private StreamResult? _streamResult;
    
    private readonly PeriodicTimer _statsLoggerTimer;

    private const int MaxNodes = 100;
    private int _nodeCount;
    
    public WorldStreamingService()
    {
        _streamingPoint = Vector3.Zero;
        
        _hashService = new HashService();
        _hookService = new HookService();
        _dummyLogger = new SerilogWrapper();
        _parserService = new Red4ParserService(_hashService, _dummyLogger, _hookService);
        _progressService = new ProgressService<double>();

        _archiveManager = new ArchiveManager(_hashService, _parserService, _dummyLogger, _progressService);
        _archiveManager.Initialize(new FileInfo(GameExe));

        _modTools = new ModTools(_dummyLogger, _progressService, _hashService, _parserService, _archiveManager, _hookService);

        _materialParser = new MaterialParser(_archiveManager, _modTools);
        _meshParser = new BlenderMeshParser(_archiveManager, _materialParser);
        _sectorParser = new StreamingSectorParser(_archiveManager);
        
        LoadSectorDescriptors(BlockPath);
        AddFallbackMaterial();

        _statsLoggerTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
    }
    
    public StreamResult? GetStreamResult()
    {
        if (!_doneStreaming || _streamResult is null)
            return null;
        
        _doneStreaming = false;
        var result = _streamResult;
        _streamResult = null;
        return result;
    }

    public void StreamInBackground(Vector3 streamingPoint)
    {
        _streamingPoint = streamingPoint;
        _doneStreaming = false;
        _streamResult = new StreamResult();

        _ = Task.Run(StreamInBackgroundInternal);
    }

    private async Task StreamInBackgroundInternal()
    {
        _ = Task.Run(LogStats);
        
        StartStreaming();
        
        CheckSectors();
        
        foreach (var sector in _loadedSectors.Keys)
            _processNodeStreamingDistances.Enqueue(sector);

        while (_isStreaming)
        {
            await Task.Delay(500);

            if (_sectorLoadQueue.Count != 0 ||
                _sectorUnloadQueue.Count != 0 ||
                _meshLoadQueue.Count != 0 ||
                _meshUnloadQueue.Count != 0 ||
                _processNodeStreamingDistances.Count != 0 ||
                _materialLoadQueue.Count != 0 ||
                _materialUnloadQueue.Count != 0) 
                continue;

            var consumeTasks = new List<Task>
            {
                Task.Run(ConsumeAddedMeshesQueue),
                Task.Run(ConsumeRemovedMeshesQueue),
                Task.Run(ConsumeAddedNodesQueue),
                Task.Run(ConsumeRemovedNodesQueue),
                Task.Run(ConsumeAddedMaterialsQueue),
                Task.Run(ConsumeRemovedMaterialsQueue)
            };
                
            await Task.WhenAll(consumeTasks);
            
            StopStreaming();
            
            _materialParser.ClearCache();
            
            _doneStreaming = true;
            return;
        }
    } 
    
    public void StartStreaming()
    {
        _nodeCount = 0;
        
        _isStreaming = true;
        _cts = new CancellationTokenSource();

        for (var i = 0; i < ThreadCount; i++)
        {
            Task.Run(() => LoadSectorFromQueue(_cts.Token));
            Task.Run(() => UnloadSectorFromQueue(_cts.Token));
            Task.Run(() => ProcessNodeStreamingDistances(_cts.Token));
            Task.Run(() => LoadMeshFromQueue(_cts.Token));
            Task.Run(() => UnloadMeshFromQueue(_cts.Token));
            Task.Run(() => LoadMaterialFromQueue(_cts.Token));
            Task.Run(() => UnloadMaterialFromQueue(_cts.Token));
        }
        
        for (var i = 0; i < ThreadCount * 10; i++)
        {
            Task.Run(() => LoadMaterialFromQueue(_cts.Token));
            Task.Run(() => LoadMeshFromQueue(_cts.Token));
        }
    }

    public void StopStreaming()
    {
        _isStreaming = false;
        _cts?.Cancel();
    }

    private async Task LogStats()
    {
        while (_isStreaming && await _statsLoggerTimer.WaitForNextTickAsync())
        {
            Console.WriteLine($"Stats:\n" +
                              $"Sector Descriptors: {_sectorDescriptors.Count}\n" +
                              $"Active Sectors: {_activeSectors.Count}\n" +
                              $"Loaded Sectors: {_loadedSectors.Count}\n" +
                              $"\n" +
                              $"Sector Load Queue: {_sectorLoadQueue.Count}\n" +
                              $"Sector Unload Queue: {_sectorUnloadQueue.Count}\n" +
                              $"\n" +
                              $"Node Distances Queue: {_processNodeStreamingDistances.Count}\n" +
                              $"\n" +
                              $"Blender Node Load Queue: {_blenderNodeLoadQueue.Count}\n" +
                              $"Blender Node Unload Queue: {_blenderNodeUnloadQueue.Count}\n" +
                              $"\n" +
                              $"Active Meshes: {_activeMeshes.Count}\n" +
                              $"Loaded Meshes: {_loadedMeshes.Count}\n" +
                              $"\n" +
                              $"Mesh Load Queue: {_meshLoadQueue.Count}\n" +
                              $"Mesh Unload Queue: {_meshUnloadQueue.Count}\n" +
                              $"\n" +
                              $"Blender Mesh Load Queue: {_blenderMeshLoadQueue.Count}\n" +
                              $"Blender Mesh Unload Queue: {_blenderMeshUnloadQueue.Count}\n" +
                              $"\n" +
                              $"Active Materials: {_activeMaterials.Count}\n" +
                              $"Loaded Materials: {_loadedMaterials.Count}\n" +
                              $"\n" +
                              $"Material Load Queue: {_materialLoadQueue.Count}\n" +
                              $"Material Unload Queue: {_materialUnloadQueue.Count}");
        }
    }
}
