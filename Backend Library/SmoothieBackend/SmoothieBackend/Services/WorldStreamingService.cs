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

    private const int ThreadCount = 6;
    
    private IArchiveManager _archiveManager;
    private IHashService _hashService;
    private Red4ParserService _parserService;
    private ILoggerService _dummyLogger;
    private IHookService _hookService;
    private IProgressService<double> _progressService;
    
    private Vector3 _cameraPosition;
    private readonly List<SectorDescriptor> _sectorDescriptors = new();
    
    private readonly ConcurrentQueue<string> _sectorLoadQueue = new();
    private readonly ConcurrentQueue<string> _sectorUnloadQueue = new();
    private readonly ConcurrentDictionary<string, byte> _activeSectors = new();
    
    private readonly ConcurrentQueue<Node> _nodeLoadQueue = new();
    private readonly ConcurrentQueue<string> _nodeUnloadQueue = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, byte>> _activeNodes = new();
    
    private bool _isStreaming = false;
    
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

        for (var i = 0; i < ThreadCount; i++)
        {
            Task.Run(LoadSectorFromQueue);
            Task.Run(UnloadSectorFromQueue);
        }
    }

    public void StopStreaming()
    {
        _isStreaming = false;
    }
    
    public void Tick(Vector3 cameraPosition)
    {
        if (_cameraPosition.Equals(cameraPosition) || !_isStreaming)
            return;
        
        _cameraPosition = cameraPosition;
        Console.WriteLine("Streaming tick with camera: " + _cameraPosition.X + ", " + _cameraPosition.Y + ", " + _cameraPosition.Z + "");
        Console.WriteLine("Sector descriptors: " + _sectorDescriptors.Count);
        CheckSectors();
    }

    public IEnumerable<Node> GetLoadNodesQueue(int count)
    {
        var i = 0;
        while (i < count && _nodeLoadQueue.TryDequeue(out var node))
        {
            if (!_activeNodes.TryGetValue(node.Id.Split(' ')[0], out var nodes))
                continue;
            
            if (!nodes.ContainsKey(int.Parse(node.Id.Split(' ')[1])))
                continue;
            
            yield return node;
            i++;
        }
    }
    
    public IEnumerable<string> GetUnloadNodesQueue(int count)
    {
        var i = 0;
        while (i < count && _nodeUnloadQueue.TryDequeue(out var nodeId))
        {
            if (!_activeNodes.TryGetValue(nodeId.Split(' ')[0], out var nodes) || !nodes.ContainsKey(int.Parse(nodeId.Split(' ')[1])))
                yield return nodeId;
            else
                continue;
            
            i++;
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
                    if (_activeSectors.ContainsKey(sector.Path))
                        continue;
                    
                    if (_activeSectors.TryAdd(sector.Path, 0))
                        _sectorLoadQueue.Enqueue(sector.Path);
                }
                else
                {
                    if (!_activeSectors.ContainsKey(sector.Path))
                        continue;
                    
                    if (_activeSectors.TryRemove(sector.Path, out _))
                        _sectorUnloadQueue.Enqueue(sector.Path);
                }
            }
        }
    }

    private async Task LoadSectorFromQueue()
    {
        while (_isStreaming)
        {
            if (!_sectorLoadQueue.TryDequeue(out var sectorPath))
            {
                await Task.Delay(10);
                continue;
            }
            
            var sectorFile = _archiveManager.GetCR2WFile(sectorPath);
            if (sectorFile is not { RootChunk: worldStreamingSector { NodeData.Data: worldNodeDataBuffer nodeData } })
                continue;
            
            var i = 0;
            foreach (var node in nodeData)
            {
                var id = $"{sectorPath} {i}";

                var nodeList = _activeNodes.GetOrAdd(sectorPath, _ => new ConcurrentDictionary<int, byte>());
                if (nodeList.TryAdd(i, 0))
                    _nodeLoadQueue.Enqueue(new Node
                    {
                        Id = id,
                        Position = node.Position.ToSDX().ToVector3()
                    });
                
                i++;
            }
        }
    }

    private async Task UnloadSectorFromQueue()
    {
        while (_isStreaming)
        {
            if (!_sectorUnloadQueue.TryDequeue(out var sectorPath))
            {
                await Task.Delay(10);
                continue;
            }

            if (_activeNodes.TryRemove(sectorPath, out var nodeList))
            {
                foreach (var index in nodeList.Keys)
                {
                    _nodeUnloadQueue.Enqueue($"{sectorPath} {index}");
                }
            }
            
            _activeSectors.TryRemove(sectorPath, out _);
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