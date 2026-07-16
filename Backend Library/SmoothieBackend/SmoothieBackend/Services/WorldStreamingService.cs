using SharpDX;
using SmoothieBackend.Extensions;
using SmoothieBackend.Models;
using WolvenKit;
using WolvenKit.Common;
using WolvenKit.Common.Services;
using WolvenKit.Core.Interfaces;
using WolvenKit.Core.Services;
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
    private List<SectorDescriptor> _sectorDescriptors = new();
    
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
        
    }

    public void StopStreaming()
    {
        
    }
    
    public void Tick(Vector3 cameraPosition)
    {
        if (_cameraPosition.Equals(cameraPosition))
            return;
        
        _cameraPosition = cameraPosition;
        Console.WriteLine("Streaming tick with camera: " + _cameraPosition.X + ", " + _cameraPosition.Y + ", " + _cameraPosition.Z + "");
        Console.WriteLine("Sector descriptors: " + _sectorDescriptors.Count);
        CheckSectors();
    }

    private void CheckSectors()
    {
        var perThreadSectors = _sectorDescriptors.Count / ThreadCount;

        for (var i = 0; i < ThreadCount; i++)
        {
            var startIndex = i * perThreadSectors;
            var endIndex = startIndex + perThreadSectors;
            if (i == ThreadCount - 1)
                endIndex = _sectorDescriptors.Count;
            Task.Run(() => CheckSectorsInRange(startIndex, endIndex));
        }

        return;
        
        void CheckSectorsInRange(int startIndex, int endIndex)
        {
            for (var i = startIndex; i < endIndex; i++)
            {
                var sector = _sectorDescriptors[i];
                if (sector.BoundingBox.Contains(_cameraPosition) != ContainmentType.Disjoint)
                    Console.WriteLine("Streaming sector: " + sector.Path);
            }
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