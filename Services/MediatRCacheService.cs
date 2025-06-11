using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VSIXExtention.Interfaces;

namespace VSIXExtention.Services
{
    public class MediatRCacheService : IMediatRCacheService, IDisposable
    {
        private readonly IWorkspaceService _workspaceService;
        private MediatRCacheManager _cacheManager;
        private bool _disposed = false;

        public MediatRCacheService(IWorkspaceService workspaceService)
        {
            _workspaceService = workspaceService ?? throw new ArgumentNullException(nameof(workspaceService));

            // Subscribe to workspace changes to handle solution changes
            _workspaceService.WorkspaceChanged += OnWorkspaceChanged;
        }

        public async Task InitializeAsync(Solution solution)
        {
            if (_cacheManager != null)
            {
                _cacheManager.Dispose();
            }

            _cacheManager = new MediatRCacheManager();
            await _cacheManager.InitializeAsync(solution);

            System.Diagnostics.Debug.WriteLine("MediatR Cache Service: Initialized for new solution");
        }

        public List<MediatRPatternMatcher.MediatRHandlerInfo> GetCachedHandlers(string requestTypeName)
        {
            if (_cacheManager == null)
            {
                EnsureCacheInitialized();
            }

            return _cacheManager?.GetCachedHandlers(requestTypeName);
        }

        public void CacheHandlers(string requestTypeName, List<MediatRPatternMatcher.MediatRHandlerInfo> handlers)
        {
            if (_cacheManager == null)
            {
                EnsureCacheInitialized();
            }

            _cacheManager?.CacheHandlers(requestTypeName, handlers);
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

        private void EnsureCacheInitialized()
        {
            try
            {
                var workspace = _workspaceService.GetWorkspace();
                if (workspace?.CurrentSolution != null)
                {
                    _ = Task.Run(async () => await InitializeAsync(workspace.CurrentSolution));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatR Cache Service: Error initializing cache: {ex.Message}");
            }
        }

        private async void OnWorkspaceChanged(object sender, WorkspaceChangeEventArgs e)
        {
            try
            {
                switch (e.Kind)
                {
                    case WorkspaceChangeKind.SolutionAdded:
                        System.Diagnostics.Debug.WriteLine("MediatR Cache Service: New solution loaded, reinitializing cache");
                        await InitializeAsync(e.NewSolution);
                        break;

                    case WorkspaceChangeKind.SolutionCleared:
                    case WorkspaceChangeKind.SolutionRemoved:
                    case WorkspaceChangeKind.SolutionReloaded:
                        System.Diagnostics.Debug.WriteLine("MediatR Cache Service: Solution closing, saving cache");
                        await SaveCacheAsync();
                        break;

                    case WorkspaceChangeKind.DocumentAdded:
                    case WorkspaceChangeKind.DocumentRemoved:
                        var document = e.NewSolution?.GetDocument(e.DocumentId) ?? e.OldSolution?.GetDocument(e.DocumentId);

                        if (document?.FilePath == null) break;

                        if (document.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                        {
                            System.Diagnostics.Debug.WriteLine("MediatR Cache Service: Relevant document changed, clearing cache");
                            ClearCache();
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatR Cache Service: Error handling workspace change: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                try
                {
                    // Unsubscribe from workspace events
                    _workspaceService.WorkspaceChanged -= OnWorkspaceChanged;

                    // Dispose cache manager
                    _cacheManager?.Dispose();
                    _cacheManager = null;

                    System.Diagnostics.Debug.WriteLine("MediatR Cache Service: Disposed successfully");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"MediatR Cache Service: Error during disposal: {ex.Message}");
                }
                finally
                {
                    _disposed = true;
                }
            }
        }
    }
}