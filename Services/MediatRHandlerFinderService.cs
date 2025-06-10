using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using VSIXExtention.Interfaces;

namespace VSIXExtention.Services
{
    public class MediatRHandlerFinderService : IMediatRHandlerFinder
    {
        private readonly IWorkspaceService _workspaceService;
        private readonly IMediatRCacheService _cacheService;

        public MediatRHandlerFinderService(IWorkspaceService workspaceService, IMediatRCacheService cacheService)
        {
            _workspaceService = workspaceService ?? throw new ArgumentNullException(nameof(workspaceService));
            _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
        }

        public async Task<List<MediatRPatternMatcher.MediatRHandlerInfo>> FindHandlersAsync(INamedTypeSymbol requestTypeSymbol)
        {
            try
            {
                var workspace = _workspaceService.GetWorkspace();
                if (workspace?.CurrentSolution == null)
                    return new List<MediatRPatternMatcher.MediatRHandlerInfo>();

                // Get the first request info (for backward compatibility)
                var requestInfo = MediatRPatternMatcher.GetRequestInfo(requestTypeSymbol, null);
                if (requestInfo == null)
                    return new List<MediatRPatternMatcher.MediatRHandlerInfo>();

                // Try cache first
                var cachedHandlers = _cacheService.GetCachedHandlers(requestInfo.RequestTypeName);
                if (cachedHandlers != null)
                {
                    System.Diagnostics.Debug.WriteLine($"MediatR Handler Finder: Found {cachedHandlers.Count} cached handlers for {requestInfo.RequestTypeName}");
                    return cachedHandlers;
                }

                // Find handlers using the pattern matcher
                List<MediatRPatternMatcher.MediatRHandlerInfo> handlers;
                if (requestInfo.IsNotification)
                {
                    handlers = await MediatRPatternMatcher.FindNotificationHandlersInSolution(workspace.CurrentSolution, requestInfo.RequestTypeName);
                }
                else
                {
                    handlers = await MediatRPatternMatcher.FindHandlersInSolution(workspace.CurrentSolution, requestInfo.RequestTypeName);
                }

                // Cache the results
                _cacheService.CacheHandlers(requestInfo.RequestTypeName, handlers);

                System.Diagnostics.Debug.WriteLine($"MediatR Handler Finder: Found {handlers.Count} handlers for {requestInfo.RequestTypeName}");
                return handlers;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatR Handler Finder: Error finding handlers: {ex.Message}");
                return new List<MediatRPatternMatcher.MediatRHandlerInfo>();
            }
        }

        public async Task<List<MediatRPatternMatcher.MediatRHandlerInfo>> FindAllHandlersAsync(INamedTypeSymbol requestTypeSymbol, SemanticModel semanticModel)
        {
            try
            {
                var workspace = _workspaceService.GetWorkspace();
                if (workspace?.CurrentSolution == null)
                    return new List<MediatRPatternMatcher.MediatRHandlerInfo>();

                // Use the new method that finds all handlers (both request and notification)
                var allHandlers = await MediatRPatternMatcher.FindAllHandlersForTypeSymbol(workspace.CurrentSolution, requestTypeSymbol, semanticModel);

                // Get all request info to cache individual handler types
                var allRequestInfo = MediatRPatternMatcher.GetAllRequestInfo(requestTypeSymbol, semanticModel);
                
                // Group handlers by type and cache them individually
                foreach (var requestInfo in allRequestInfo)
                {
                    var handlersForThisType = allHandlers
                        .Where(h => h.RequestTypeName == requestInfo.RequestTypeName && h.IsNotificationHandler == requestInfo.IsNotification)
                        .ToList();
                    
                    if (handlersForThisType.Any())
                    {
                        _cacheService.CacheHandlers(requestInfo.RequestTypeName, handlersForThisType);
                    }
                }

                System.Diagnostics.Debug.WriteLine($"MediatR Handler Finder: Found {allHandlers.Count} total handlers for {requestTypeSymbol.Name}");
                
                // Log details about what was found
                var requestHandlers = allHandlers.Where(h => !h.IsNotificationHandler).ToList();
                var notificationHandlers = allHandlers.Where(h => h.IsNotificationHandler).ToList();
                
                if (requestHandlers.Any())
                {
                    System.Diagnostics.Debug.WriteLine($"  - {requestHandlers.Count} request handler(s): {string.Join(", ", requestHandlers.Select(h => h.HandlerTypeName))}");
                }
                
                if (notificationHandlers.Any())
                {
                    System.Diagnostics.Debug.WriteLine($"  - {notificationHandlers.Count} notification handler(s): {string.Join(", ", notificationHandlers.Select(h => h.HandlerTypeName))}");
                }

                return allHandlers;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatR Handler Finder: Error finding all handlers: {ex.Message}");
                return new List<MediatRPatternMatcher.MediatRHandlerInfo>();
            }
        }
    }
} 