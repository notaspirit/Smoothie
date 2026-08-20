using System.Collections.Concurrent;
using SmoothieBackend.Models;
using WolvenKit.RED4.Archive.CR2W;
using WolvenKit.RED4.Types;

namespace SmoothieBackend.Components;

public class EmbeddedFilesStore<T>
{
    private readonly Dictionary<string, T> _embeddedFilesStore = new();
    private readonly Dictionary<string, List<string>> _embeddedFilesSources = new();
    private readonly Lock _lock = new();
    
    public T? GetEmbeddedFile(string fileName)
    {
        lock (_lock)
        {
            _embeddedFilesStore.TryGetValue(fileName, out var file);
            return file;
        }
    }
    
    public void AddEmbeddedFiles(CR2WFile file, string filePath, Func<RedBaseClass, T?> factory)
    {
        lock (_lock)
        {
            _embeddedFilesSources[filePath] = [];
            var files = _embeddedFilesSources[filePath];
            foreach (var embeddedFile in file.EmbeddedFiles)
            {
                var value = factory(embeddedFile.Content);
                if (value is null)
                    continue;

                if (_embeddedFilesStore.TryAdd(embeddedFile.FileName!, value))
                    files.Add(embeddedFile.FileName!);
            }
        }
    }

    public void RemoveEmbeddedFiles(string filePath)
    {
        lock (_lock)
        {
            if (!_embeddedFilesSources.TryGetValue(filePath, out var files))
                return;
            
            foreach (var embeddedFile in files)
                _embeddedFilesStore.Remove(embeddedFile, out _);
        }
    }
}