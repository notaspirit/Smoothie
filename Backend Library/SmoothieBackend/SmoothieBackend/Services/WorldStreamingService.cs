using System.Collections.Concurrent;
using SharpDX;
using SmoothieBackend.Extensions;
using SmoothieBackend.Models;
using WolvenKit;
using WolvenKit.Common;
using WolvenKit.Common.Services;
using WolvenKit.Core.Interfaces;
using WolvenKit.Core.Services;
using WolvenKit.RED4.Archive.Buffer;
using WolvenKit.RED4.CR2W;
using WolvenKit.RED4.CR2W.Archive;
using WolvenKit.RED4.Types;
using Vector3 = SharpDX.Vector3;

namespace SmoothieBackend.Services;

public class WorldStreamingService
{
    private const string BlockPath = @"base\worlds\03_night_city\_compiled\default\blocks\all.streamingblock";
    private const string GameExe = @"E:\Games\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe";

    private const int ThreadCount = 18;
    
    private IArchiveManager _archiveManager;
    private IHashService _hashService;
    private Red4ParserService _parserService;
    private ILoggerService _dummyLogger;
    private IHookService _hookService;
    private IProgressService<double> _progressService;
    
    private Vector3 _cameraPosition;
    private readonly List<SectorDescriptor> _sectorDescriptors = new();
    private readonly ConcurrentDictionary<string, Node[]> _loadedSectors = new();
    
    private readonly ConcurrentDictionary<string, byte> _activeSectors = new();
    
    private readonly BlockingWorkQueue<string> _sectorLoadQueue = new(false);
    private readonly BlockingWorkQueue<string> _sectorUnloadQueue = new(false);
    
    private readonly BlockingWorkQueue<string> _processNodeStreamingDistances = new(true);
    
    private readonly WorkQueue<NodeID> _blenderNodeLoadQueue = new(false);
    private readonly WorkQueue<NodeID> _blenderNodeUnloadQueue = new(false);
    
    private bool _isStreaming = false;
    private CancellationTokenSource? _cts = null;
    
    public WorldStreamingService()
    {
        _cameraPosition = Vector3.Zero;
        
        _hashService = new HashService();
        _hookService = new HookService();
        _dummyLogger = new SerilogWrapper();
        _parserService = new Red4ParserService(_hashService, _dummyLogger, _hookService);
        _progressService = new ProgressService<double>();
        
        _archiveManager = new ArchiveManager(_hashService, _parserService, _dummyLogger, _progressService);
        _archiveManager.Initialize(new FileInfo(GameExe));
        
        LoadSectorDescriptors(BlockPath);
    }
    
    public void StartStreaming()
    {
        _isStreaming = true;
        _cts = new CancellationTokenSource();

        for (var i = 0; i < ThreadCount; i++)
        {
            Task.Run(() => LoadSectorFromQueue(_cts.Token));
            Task.Run(() => UnloadSectorFromQueue(_cts.Token));
            Task.Run(() => ProcessNodeStreamingDistances(_cts.Token));
        }
    }

    public void StopStreaming()
    {
        _isStreaming = false;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
    
    public void Tick(Vector3 cameraPosition)
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
                          $"Blender Load Queue: {_blenderNodeLoadQueue.Count}\n" +
                          $"Blender Unload Queue: {_blenderNodeUnloadQueue.Count}");
        
        if (_cameraPosition.Equals(cameraPosition) || !_isStreaming)
            return;
        
        _cameraPosition = cameraPosition;
        Console.WriteLine("Streaming tick with camera: " + _cameraPosition.X + ", " + _cameraPosition.Y + ", " + _cameraPosition.Z + "");
        CheckSectors();
        
        foreach (var sector in _loadedSectors.Keys)
            _processNodeStreamingDistances.Enqueue(sector);
    }

    public IEnumerable<Node> GetLoadNodesQueue(int count)
    {
        var i = 0;
        while (i < count && _blenderNodeLoadQueue.TryDequeue(out var nodeId))
        {
            if (!_loadedSectors.TryGetValue(nodeId.ParentSector, out var sector) || nodeId.Index > sector.Length)
            {
                _blenderNodeLoadQueue.Done(nodeId);
                continue;
            }

            var node = sector[nodeId.Index];
            if (!node.IsStreaming)
            {
                _blenderNodeLoadQueue.Done(nodeId);
                continue;
            }
            
            i++;
            _blenderNodeLoadQueue.Done(nodeId);
            yield return node;
        }
    }
    
    public IEnumerable<NodeID> GetUnloadNodesQueue(int count)
    {
        var i = 0;
        while (i < count && _blenderNodeUnloadQueue.TryDequeue(out var nodeId))
        {
            if (_loadedSectors.TryGetValue(nodeId.ParentSector, out var sector))
            {
                if (nodeId.Index < sector.Length && !sector[nodeId.Index].IsStreaming)
                {
                    i++;
                    _blenderNodeUnloadQueue.Done(nodeId);
                    yield return nodeId;
                }
                else
                {
                    _blenderNodeUnloadQueue.Done(nodeId);
                    continue;
                }
            }
            
            i++;
            _blenderNodeUnloadQueue.Done(nodeId);
            yield return nodeId;
        }
    }

    private void CheckSectors()
    {
        var perThreadSectors = _sectorDescriptors.Count / ThreadCount;
        
        var tasks = new List<Task>();
        
        for (var i = 0; i < ThreadCount; i++)
        {
            var startIndex = i * perThreadSectors;
            var endIndex = startIndex + perThreadSectors;
            if (i == ThreadCount - 1)
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
                if (sector.BoundingBox.Contains(_cameraPosition) != ContainmentType.Disjoint)
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
                continue;
            
            foreach (var node in sector)
            {
                var previous = node.IsStreaming;
                node.IsStreaming = node.Position.Contains(ref _cameraPosition) != ContainmentType.Disjoint;
                if (previous != node.IsStreaming && node.IsStreaming)
                    _blenderNodeLoadQueue.Enqueue(node.Id);
                else if (previous != node.IsStreaming && !node.IsStreaming)
                    _blenderNodeUnloadQueue.Enqueue(node.Id);
            }
            
            _processNodeStreamingDistances.Done(sectorPath);
        }
    }

    private void LoadSectorFromQueue(CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            var sectorPath = _sectorLoadQueue.Dequeue(ct);
            
            if (_loadedSectors.ContainsKey(sectorPath) || !_activeSectors.ContainsKey(sectorPath))
                continue;
            
            var sectorFile = _archiveManager.GetCR2WFile(sectorPath);
            if (sectorFile is not { RootChunk: worldStreamingSector { NodeData.Data: worldNodeDataBuffer nodeData } })
                continue;
            
            // double-check before adding, since loading can take a while
            if (_loadedSectors.ContainsKey(sectorPath) || !_activeSectors.ContainsKey(sectorPath))
                continue;
            
            var nodeList = new Node[nodeData.Count];
            
            var i = 0;
            foreach (var node in nodeData)
            {
                nodeList[i] = new Node
                {
                    Id = new NodeID(sectorPath, i),
                    Position = new BoundingSphere( node.Position.ToSDX().ToVector3(), node.UkFloat1),
                    IsStreaming = false
                };
                
                i++;
            }
            
            _loadedSectors.TryAdd(sectorPath, nodeList);
            _sectorLoadQueue.Done(sectorPath);
            _processNodeStreamingDistances.Enqueue(sectorPath);
        }
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
}