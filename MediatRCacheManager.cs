using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VSIXExtention
{
    public class CachedHandlerInfo
    {
        public MediatRPatternMatcher.MediatRHandlerInfo Handler { get; set; }
        public DateTime LastUsed { get; set; }
        public DateTime LastValidated { get; set; }

        public CachedHandlerInfo(MediatRPatternMatcher.MediatRHandlerInfo handler)
        {
            Handler = handler;
            LastUsed = DateTime.Now;
            LastValidated = DateTime.Now;
        }
    }

    public class MediatRCacheManager : IDisposable
    {
        private readonly ConcurrentDictionary<string, List<CachedHandlerInfo>> _handlerCache
            = new ConcurrentDictionary<string, List<CachedHandlerInfo>>();

        private readonly ConcurrentDictionary<string, DateTime> _recentlyUsedRequestTypes
            = new ConcurrentDictionary<string, DateTime>();

        private readonly TimeSpan _recentUsageWindow = TimeSpan.FromMinutes(10);

        // Persistent cache storage
        private string _currentSolutionPath;
        private string _cacheFilePath;
        private readonly SemaphoreSlim _cacheFileSemaphore = new SemaphoreSlim(1, 1);
        private bool _cacheLoaded = false;
        private Timer _periodicSaveTimer;
        private bool _cacheIsDirty = false;
        private readonly TimeSpan _periodicSaveInterval = TimeSpan.FromMinutes(2);
        private bool _disposed = false;
        private bool _manuallyCleared = false; // Track if cache was manually cleared

        public MediatRCacheManager()
        {
        }

        public bool IsCacheLoaded => _cacheLoaded;
        public bool IsCacheDirty => _cacheIsDirty;
        public string CurrentSolutionPath => _currentSolutionPath;

        public async Task InitializeAsync(string solutionPath)
        {
            _currentSolutionPath = solutionPath;
            _cacheFilePath = GetCacheFilePath(_currentSolutionPath);

            await LoadCacheFromFileAsync();
            _cacheLoaded = true;

            StartPeriodicSaveTimer();
            System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Cache: Initialized for solution {_currentSolutionPath}");
        }

        public List<MediatRPatternMatcher.MediatRHandlerInfo> GetCachedHandlers(string requestTypeName)
        {
            if (_handlerCache.TryGetValue(requestTypeName, out var cachedHandlers))
            {
                // Track usage for smart rebuilding
                _recentlyUsedRequestTypes.AddOrUpdate(requestTypeName, DateTime.Now, (_, __) => DateTime.Now);

                // Update last used time for these handlers
                var now = DateTime.Now;
                foreach (var cachedHandler in cachedHandlers)
                {
                    cachedHandler.LastUsed = now;
                }

                return cachedHandlers.Select(ch => ch.Handler).ToList();
            }
            return null;
        }

        public void CacheHandlers(string requestTypeName, List<MediatRPatternMatcher.MediatRHandlerInfo> handlers)
        {
            if (handlers?.Any() == true)
            {
                var cachedHandlers = handlers.Select(h => new CachedHandlerInfo(h)).ToList();
                _handlerCache.AddOrUpdate(requestTypeName, cachedHandlers, (_, __) => cachedHandlers);
                _recentlyUsedRequestTypes.AddOrUpdate(requestTypeName, DateTime.Now, (_, __) => DateTime.Now);
                _cacheIsDirty = true;
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Cache: Cached {handlers.Count} handlers for {requestTypeName}");
            }
        }

        public void UpdateCacheWithHandlers(List<MediatRPatternMatcher.MediatRHandlerInfo> newHandlers)
        {
            if (newHandlers?.Any() != true) return;

            var groupedHandlers = newHandlers.GroupBy(h => h.RequestTypeName).ToList();

            foreach (var group in groupedHandlers)
            {
                var requestTypeName = group.Key;
                var handlers = group.ToList();
                var newCachedHandlers = handlers.Select(h => new CachedHandlerInfo(h)).ToList();

                _handlerCache.AddOrUpdate(requestTypeName, newCachedHandlers, (key, existingHandlers) =>
                {
                    // Remove existing handlers with same type name, add new ones
                    var updatedHandlers = existingHandlers
                        .Where(existing => !handlers.Any(newHandler => newHandler.HandlerTypeName == existing.Handler.HandlerTypeName))
                        .Concat(newCachedHandlers)
                        .ToList();

                    System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Cache: Updated cache for {requestTypeName}, now has {updatedHandlers.Count} handlers");
                    return updatedHandlers;
                });
            }

            _cacheIsDirty = true;
        }

        public void ClearCache()
        {
            _handlerCache.Clear();
            _cacheIsDirty = true;
            _manuallyCleared = true; // Mark that cache was manually cleared
            System.Diagnostics.Debug.WriteLine("MediatRNavigationExtension: Cache: Cache cleared");
        }

        public void InvalidateHandlersForRequestType(string requestTypeName)
        {
            if (_handlerCache.TryRemove(requestTypeName, out var removedHandlers))
            {
                _cacheIsDirty = true;
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Cache: Invalidated cache for {requestTypeName} (removed {removedHandlers.Count} handlers)");
            }
        }

        public List<string> GetRecentlyUsedRequestTypes()
        {
            var cutoff = DateTime.Now - _recentUsageWindow;
            return _recentlyUsedRequestTypes
                .Where(kvp => kvp.Value >= cutoff)
                .Select(kvp => kvp.Key)
                .ToList();
        }

        public async Task SaveCacheAsync()
        {
            if (!_cacheLoaded || string.IsNullOrEmpty(_cacheFilePath)) return;

            try
            {
                await SaveCacheToFileAsync();
                _cacheIsDirty = false;
                System.Diagnostics.Debug.WriteLine("MediatRNavigationExtension: Cache: Manual save completed");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Cache: Error in manual save: {ex.Message}");
            }
        }

        private string GetCacheFilePath(string solutionPath)
        {
            try
            {
                // Handle null or empty solution path
                if (string.IsNullOrEmpty(solutionPath))
                {
                    var tempDir = System.IO.Path.GetTempPath();
                    var cacheName = $"MediatRExtension_NoSolution_{DateTime.Now.Ticks}.cache";
                    var fallbackPath = System.IO.Path.Combine(tempDir, cacheName);
                    System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Cache: Using fallback cache path: {fallbackPath}");
                    return fallbackPath;
                }

                var solutionDir = System.IO.Path.GetDirectoryName(solutionPath);
                var solutionName = System.IO.Path.GetFileNameWithoutExtension(solutionPath);

                // Handle case where solution directory or name might be null/empty
                if (string.IsNullOrEmpty(solutionDir) || string.IsNullOrEmpty(solutionName))
                {
                    var tempDir = System.IO.Path.GetTempPath();
                    var safeSolutionName = !string.IsNullOrEmpty(solutionName) ? solutionName : "UnknownSolution";
                    var fallbackPath = System.IO.Path.Combine(tempDir, $"MediatRExtension_{safeSolutionName}_{solutionPath.GetHashCode()}.cache");
                    System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Cache: Using temp directory for cache: {fallbackPath}");
                    return fallbackPath;
                }

                var vsDir = System.IO.Path.Combine(solutionDir, ".vs");

                if (System.IO.Directory.Exists(vsDir))
                {
                    return System.IO.Path.Combine(vsDir, $"MediatRExtension_{solutionName}.cache");
                }
                else
                {
                    var tempDir = System.IO.Path.GetTempPath();
                    return System.IO.Path.Combine(tempDir, $"MediatRExtension_{solutionName}_{solutionPath.GetHashCode()}.cache");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Cache: Error creating cache file path: {ex.Message}");

                // Final fallback - use temp directory with timestamp
                try
                {
                    var tempDir = System.IO.Path.GetTempPath();
                    var fallbackName = $"MediatRExtension_Fallback_{DateTime.Now.Ticks}.cache";
                    var fallbackPath = System.IO.Path.Combine(tempDir, fallbackName);
                    System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Cache: Using final fallback cache path: {fallbackPath}");
                    return fallbackPath;
                }
                catch
                {
                    return null;
                }
            }
        }

        private async Task LoadCacheFromFileAsync()
        {
            try
            {
                if (string.IsNullOrEmpty(_cacheFilePath))
                {
                    System.Diagnostics.Debug.WriteLine("MediatRNavigationExtension: Cache: Cache file path is null/empty, skipping file operations");
                    return;
                }

                if (!System.IO.File.Exists(_cacheFilePath))
                {
                    System.Diagnostics.Debug.WriteLine("MediatRNavigationExtension: Cache: No cache file found, creating empty cache file");
                    await CreateEmptyCacheFileAsync();
                    return;
                }

                await _cacheFileSemaphore.WaitAsync();
                try
                {
                    var json = System.IO.File.ReadAllText(_cacheFilePath);
                    var cacheData = JsonConvert.DeserializeObject<PersistentCacheData>(json);

                    if (cacheData?.CacheVersion == "1.0" && cacheData.SolutionPath == _currentSolutionPath)
                    {
                        // Restore handler cache
                        foreach (var kvp in cacheData.HandlerCache)
                        {
                            var handlers = kvp.Value.Select(ConvertFromSerializable).Where(h => h != null).ToList();
                            if (handlers.Any())
                            {
                                var cachedHandlers = handlers.Select(h => new CachedHandlerInfo(h)).ToList();
                                _handlerCache.TryAdd(kvp.Key, cachedHandlers);
                            }
                        }

                        // Restore recently used types (filter by recency)
                        var cutoff = DateTime.Now - _recentUsageWindow;
                        foreach (var kvp in cacheData.RecentlyUsedRequestTypes)
                        {
                            if (kvp.Value >= cutoff)
                            {
                                _recentlyUsedRequestTypes.TryAdd(kvp.Key, kvp.Value);
                            }
                        }

                        System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Cache: Loaded cache with {_handlerCache.Count} handler types and {_recentlyUsedRequestTypes.Count} recent types");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("MediatRNavigationExtension: Cache: Cache file is invalid or for different solution, recreating cache file");
                        // Clear memory cache and recreate file for invalid cache
                        _handlerCache.Clear();
                        _recentlyUsedRequestTypes.Clear();
                    }
                }
                finally
                {
                    _cacheFileSemaphore.Release();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Cache: Error loading cache from file: {ex.Message}, will create fresh cache file when needed");
                // Don't create empty cache file immediately on error - let it be created when first needed
                // Clear any partial data that might have been loaded
                _handlerCache.Clear();
                _recentlyUsedRequestTypes.Clear();
            }
        }

        private async Task CreateEmptyCacheFileAsync()
        {
            try
            {
                if (string.IsNullOrEmpty(_cacheFilePath))
                {
                    System.Diagnostics.Debug.WriteLine("MediatRNavigationExtension: Cache: Cannot create empty cache file - cache file path is null/empty");
                    return;
                }

                var emptyCacheData = new PersistentCacheData
                {
                    SolutionPath = _currentSolutionPath,
                    LastModified = DateTime.Now,
                    CacheVersion = "1.0",
                    HandlerCache = new Dictionary<string, List<SerializableHandlerInfo>>(),
                    RecentlyUsedRequestTypes = new Dictionary<string, DateTime>()
                };

                var json = JsonConvert.SerializeObject(emptyCacheData, Formatting.Indented);

                await _cacheFileSemaphore.WaitAsync();
                try
                {
                    // Ensure the directory exists
                    var directory = System.IO.Path.GetDirectoryName(_cacheFilePath);
                    if (!string.IsNullOrEmpty(directory) && !System.IO.Directory.Exists(directory))
                    {
                        System.IO.Directory.CreateDirectory(directory);
                    }

                    System.IO.File.WriteAllText(_cacheFilePath, json);
                    System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Cache: Created empty cache file at {_cacheFilePath}");
                }
                finally
                {
                    _cacheFileSemaphore.Release();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Cache: Error creating empty cache file: {ex.Message}");
                // Don't rethrow - this is not a critical failure
            }
        }

        private async Task SaveCacheToFileAsync()
        {
            try
            {
                if (string.IsNullOrEmpty(_cacheFilePath))
                {
                    System.Diagnostics.Debug.WriteLine("MediatRNavigationExtension: Cache: Cannot save cache - cache file path is null/empty");
                    return;
                }

                var cacheData = new PersistentCacheData
                {
                    SolutionPath = _currentSolutionPath,
                    LastModified = DateTime.Now,
                    CacheVersion = "1.0"
                };

                // Convert handler cache to serializable format, but only save recent ones
                var recentRequestTypes = GetRecentlyUsedRequestTypes();
                foreach (var kvp in _handlerCache.ToList())
                {
                    // Only save handlers for recently used request types
                    if (recentRequestTypes.Contains(kvp.Key))
                    {
                        var serializableHandlers = kvp.Value.Select(cachedHandler => ConvertToSerializable(cachedHandler.Handler)).Where(h => h != null).ToList();
                        if (serializableHandlers.Any())
                        {
                            cacheData.HandlerCache[kvp.Key] = serializableHandlers;
                        }
                    }
                }

                // Copy recently used types
                foreach (var kvp in _recentlyUsedRequestTypes.ToList())
                {
                    cacheData.RecentlyUsedRequestTypes[kvp.Key] = kvp.Value;
                }

                // Only save if we have meaningful data or if the file doesn't exist
                if (cacheData.HandlerCache.Count > 0 || cacheData.RecentlyUsedRequestTypes.Count > 0 || !System.IO.File.Exists(_cacheFilePath))
                {
                    var json = JsonConvert.SerializeObject(cacheData);

                    await _cacheFileSemaphore.WaitAsync();

                    try
                    {
                        // Ensure the directory exists
                        var directory = System.IO.Path.GetDirectoryName(_cacheFilePath);
                        if (!string.IsNullOrEmpty(directory) && !System.IO.Directory.Exists(directory))
                        {
                            System.IO.Directory.CreateDirectory(directory);
                        }

                        System.IO.File.WriteAllText(_cacheFilePath, json);
                    }
                    finally
                    {
                        _cacheFileSemaphore.Release();
                    }

                    System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Cache: Saved cache with {cacheData.HandlerCache.Count} handler types and {cacheData.RecentlyUsedRequestTypes.Count} recent types to {_cacheFilePath}");
                    System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Cache: In-memory cache has {_handlerCache.Count} types: [{string.Join(", ", _handlerCache.Keys)}]");
                    System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Cache: Recent types: [{string.Join(", ", recentRequestTypes)}]");
                    System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Cache: Saved types: [{string.Join(", ", cacheData.HandlerCache.Keys)}]");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("MediatRNavigationExtension: Cache: Skipped saving - no meaningful data and file exists");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Cache: Error saving cache to file: {ex.Message}");
            }
        }

        private SerializableHandlerInfo ConvertToSerializable(MediatRPatternMatcher.MediatRHandlerInfo handler)
        {
            try
            {
                var lineSpan = handler.Location?.GetLineSpan();
                return new SerializableHandlerInfo
                {
                    HandlerTypeName = handler.HandlerTypeName,
                    RequestTypeName = handler.RequestTypeName,
                    ResponseTypeName = handler.ResponseTypeName,
                    FilePath = handler.Location?.SourceTree?.FilePath,
                    LineNumber = lineSpan?.StartLinePosition.Line ?? -1,
                    ColumnNumber = lineSpan?.StartLinePosition.Character ?? -1,
                    IsNotificationHandler = handler.IsNotificationHandler
                };
            }
            catch
            {
                return null;
            }
        }

        private MediatRPatternMatcher.MediatRHandlerInfo ConvertFromSerializable(SerializableHandlerInfo serializable)
        {
            try
            {
                if (string.IsNullOrEmpty(serializable.FilePath) || !System.IO.File.Exists(serializable.FilePath))
                    return null;

                // Create a location from the file path for display purposes
                Location location = null;
                try
                {
                    var syntaxTree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText("", path: serializable.FilePath);
                    if (serializable.LineNumber >= 0)
                    {
                        var position = new Microsoft.CodeAnalysis.Text.LinePosition(serializable.LineNumber, serializable.ColumnNumber);
                        var span = new Microsoft.CodeAnalysis.Text.TextSpan(0, 0);
                        location = Microsoft.CodeAnalysis.Location.Create(syntaxTree, span);
                    }
                }
                catch
                {
                    // If we can't create a proper location, create a simple one just for the file path
                    var dummySyntaxTree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText("", path: serializable.FilePath);
                    location = Microsoft.CodeAnalysis.Location.Create(dummySyntaxTree, new Microsoft.CodeAnalysis.Text.TextSpan(0, 0));
                }

                return new MediatRPatternMatcher.MediatRHandlerInfo
                {
                    HandlerTypeName = serializable.HandlerTypeName,
                    RequestTypeName = serializable.RequestTypeName,
                    ResponseTypeName = serializable.ResponseTypeName,
                    Location = location,
                    IsNotificationHandler = serializable.IsNotificationHandler
                };
            }
            catch
            {
                return null;
            }
        }

        private void StartPeriodicSaveTimer()
        {
            _periodicSaveTimer?.Dispose();
            _periodicSaveTimer = new Timer(PeriodicSaveCallback, null, _periodicSaveInterval, _periodicSaveInterval);
            System.Diagnostics.Debug.WriteLine("MediatRNavigationExtension: Cache: Started periodic cache save timer");
        }

        private void PeriodicSaveCallback(object state)
        {
            try
            {
                var hasChanges = false;
                var now = DateTime.Now;
                var validationCutoff = now.AddDays(-7);

                // First, validate old handlers that haven't been checked recently
                foreach (var kvp in _handlerCache.ToList())
                {
                    var requestTypeName = kvp.Key;
                    var cachedHandlers = kvp.Value.ToList();
                    var handlersToRemove = new List<CachedHandlerInfo>();

                    foreach (var cachedHandler in cachedHandlers)
                    {
                        // If handler hasn't been validated in 7 days, check if it still exists
                        if (cachedHandler.LastValidated < validationCutoff)
                        {
                            if (DoesHandlerStillExist(cachedHandler.Handler))
                            {
                                // Handler still exists, update validation time
                                cachedHandler.LastValidated = now;
                                hasChanges = true;
                                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Cache: Validated handler {cachedHandler.Handler.HandlerTypeName} for {requestTypeName}");
                            }
                            else
                            {
                                // Handler no longer exists, mark for removal
                                handlersToRemove.Add(cachedHandler);
                                hasChanges = true;
                                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Cache: Handler {cachedHandler.Handler.HandlerTypeName} for {requestTypeName} no longer exists, removing from cache");
                            }
                        }
                    }

                    // Remove stale handlers
                    if (handlersToRemove.Any())
                    {
                        var updatedHandlers = cachedHandlers.Except(handlersToRemove).ToList();
                        if (updatedHandlers.Any())
                        {
                            _handlerCache.TryUpdate(requestTypeName, updatedHandlers, cachedHandlers);
                        }
                        else
                        {
                            // No handlers left for this request type, remove the entire entry
                            _handlerCache.TryRemove(requestTypeName, out _);
                        }
                    }
                }

                // Save cache if we made changes or if it was already dirty
                if (hasChanges)
                {
                    _cacheIsDirty = true;
                }

                if (_cacheIsDirty && _cacheLoaded)
                {
                    _ = Task.Run(SaveCacheToFileAsync);
                    _cacheIsDirty = false;
                    System.Diagnostics.Debug.WriteLine("MediatRNavigationExtension: Cache: Periodic cache save and validation completed");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Cache: Error in periodic save and validation: {ex.Message}");
            }
        }

        private bool DoesHandlerStillExist(MediatRPatternMatcher.MediatRHandlerInfo handler)
        {
            try
            {
                // Quick check: does the file still exist?
                var filePath = handler.Location?.SourceTree?.FilePath;
                if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
                {
                    return false;
                }

                // Additional validation could be added here if needed
                // For now, file existence is sufficient for periodic validation
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                try
                {
                    // Stop periodic save timer
                    _periodicSaveTimer?.Dispose();
                    _periodicSaveTimer = null;

                    // Save cache synchronously before disposing, but only if it wasn't manually cleared
                    if (_cacheLoaded && !_manuallyCleared)
                    {
                        try
                        {
                            SaveCacheToFileAsync().Wait(TimeSpan.FromSeconds(5));
                            System.Diagnostics.Debug.WriteLine("MediatRNavigationExtension: Cache: Cache saved during disposal");
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Cache: Error saving cache during disposal: {ex.Message}");
                        }
                    }
                    else if (_manuallyCleared)
                    {
                        System.Diagnostics.Debug.WriteLine("MediatRNavigationExtension: Cache: Skipping disposal save - cache was manually cleared");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Cache: Error during disposal: {ex.Message}");
                }
                finally
                {
                    _disposed = true;
                    _handlerCache.Clear();
                    _cacheFileSemaphore?.Dispose();
                }
            }
        }
    }

    [Serializable]
    public class PersistentCacheData
    {
        public string SolutionPath { get; set; }
        public DateTime LastModified { get; set; }
        public Dictionary<string, List<SerializableHandlerInfo>> HandlerCache { get; set; } = new Dictionary<string, List<SerializableHandlerInfo>>();
        public Dictionary<string, DateTime> RecentlyUsedRequestTypes { get; set; } = new Dictionary<string, DateTime>();
        public string CacheVersion { get; set; } = "1.0";
    }

    [Serializable]
    public class SerializableHandlerInfo
    {
        public string HandlerTypeName { get; set; }
        public string RequestTypeName { get; set; }
        public string ResponseTypeName { get; set; }
        public string FilePath { get; set; }
        public int LineNumber { get; set; }
        public int ColumnNumber { get; set; }
        public bool IsNotificationHandler { get; set; }
    }
}