using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VSIXExtention.Interfaces;
using System.Linq;

namespace VSIXExtention.Services
{
    public class MediatRCacheService : IMediatRCacheService, IDisposable
    {
        private MediatRCacheManager _cacheManager;
        private bool _disposed = false;

        public MediatRCacheService()
        {
        }

        public async Task InitializeAsync(Solution solution)
        {
            try
            {
                if (_cacheManager != null)
                {
                    _cacheManager.Dispose();
                }

                _cacheManager = new MediatRCacheManager();

                // Handle null or empty solution file path
                string solutionPath = solution?.FilePath;
                if (string.IsNullOrEmpty(solutionPath))
                {
                    // Generate a fallback path using solution ID or first project name
                    string solutionName = "UnknownSolution";
                    
                    if (solution != null)
                    {
                        // Try to get a meaningful name from the first project
                        var firstProject = solution.Projects.FirstOrDefault();
                        if (firstProject != null && !string.IsNullOrEmpty(firstProject.Name))
                        {
                            solutionName = firstProject.Name;
                        }
                        else
                        {
                            // Fall back to solution ID
                            solutionName = solution.Id.ToString();
                        }
                    }
                    
                    var tempDir = System.IO.Path.GetTempPath();
                    solutionPath = System.IO.Path.Combine(tempDir, $"MediatRTemp_{solutionName}.sln");
                    
                    System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: CacheService: Using fallback solution path: {solutionPath}");
                }

                await _cacheManager.InitializeAsync(solutionPath);

                System.Diagnostics.Debug.WriteLine("MediatRNavigationExtension: CacheService: Initialized for new solution");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: CacheService: Error during initialization: {ex.Message}");
                
                // Create a minimal cache manager even if initialization fails
                if (_cacheManager == null)
                {
                    _cacheManager = new MediatRCacheManager();
                    System.Diagnostics.Debug.WriteLine("MediatRNavigationExtension: CacheService: Created minimal cache manager after initialization failure");
                }
            }
        }

        public List<MediatRPatternMatcher.MediatRHandlerInfo> GetCachedHandlers(string requestTypeName)
        {
            if (_cacheManager == null)
            {
                System.Diagnostics.Debug.WriteLine("MediatRNavigationExtension: CacheService: CacheManager is NULL");

                return new List<MediatRPatternMatcher.MediatRHandlerInfo>();
            }
            else
            {
                return _cacheManager?.GetCachedHandlers(requestTypeName);
            }
        }

        public void CacheHandlers(string requestTypeName, List<MediatRPatternMatcher.MediatRHandlerInfo> handlers)
        {
            if (_cacheManager == null)
            {
                System.Diagnostics.Debug.WriteLine("MediatRNavigationExtension: CacheService: CacheManager is NULL");
            }
            else
            {
                _cacheManager?.CacheHandlers(requestTypeName, handlers);
            }
        }

        public void InvalidateHandlersForRequestType(string requestTypeName)
        {
            _cacheManager?.InvalidateHandlersForRequestType(requestTypeName);
        }

        public void ClearCache()
        {
            _cacheManager?.ClearCache();
        }

        public async Task SaveCacheAsync()
        {
            if (_cacheManager != null)
            {
                await _cacheManager.SaveCacheAsync();
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                try
                {
                    // Dispose cache manager
                    _cacheManager?.Dispose();
                    _cacheManager = null;

                    System.Diagnostics.Debug.WriteLine("MediatRNavigationExtension: CacheService: Disposed successfully");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: CacheService: Error during disposal: {ex.Message}");
                }
                finally
                {
                    _disposed = true;
                }
            }
        }
    }
}