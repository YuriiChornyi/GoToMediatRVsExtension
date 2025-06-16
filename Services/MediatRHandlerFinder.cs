using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using VSIXExtention.Interfaces;
using VSIXExtention.DI;

namespace VSIXExtention.Services
{
    public class MediatRHandlerFinder : IMediatRHandlerFinder
    {
        public MediatRHandlerFinder()
        {
        }

        public async Task<List<MediatRPatternMatcher.MediatRHandlerInfo>> FindHandlersAsync(INamedTypeSymbol requestTypeSymbol)
        {
            try
            {
                // Get services from ServiceLocator (solution-scoped)
                var workspaceService = ServiceLocator.TryGetService<IWorkspaceService>();
                var cacheService = ServiceLocator.TryGetService<IMediatRCacheService>();
                
                if (workspaceService == null)
                {
                    System.Diagnostics.Debug.WriteLine("MediatRNavigationExtension: MediatRHandlerFinder: No workspace service available - solution may not be open");
                    return new List<MediatRPatternMatcher.MediatRHandlerInfo>();
                }

                var workspace = workspaceService.GetWorkspace();
                if (workspace?.CurrentSolution == null)
                    return new List<MediatRPatternMatcher.MediatRHandlerInfo>();

                // Get the first request info (for backward compatibility)
                var requestInfo = MediatRPatternMatcher.GetRequestInfo(requestTypeSymbol, null);
                if (requestInfo == null)
                    return new List<MediatRPatternMatcher.MediatRHandlerInfo>();

                // Try cache first (if available)
                if (cacheService != null)
                {
                    var cachedHandlers = cacheService.GetCachedHandlers(requestInfo.RequestTypeName);
                    if (cachedHandlers != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: MediatRHandlerFinder: Found {cachedHandlers.Count} cached handlers for {requestInfo.RequestTypeName}");
                        return cachedHandlers;
                    }
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

                // Cache the results (if cache service is available)
                cacheService?.CacheHandlers(requestInfo.RequestTypeName, handlers);

                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: MediatRHandlerFinder: Found {handlers.Count} handlers for {requestInfo.RequestTypeName}");
                return handlers;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: MediatRHandlerFinder: Error finding handlers: {ex.Message}");
                return new List<MediatRPatternMatcher.MediatRHandlerInfo>();
            }
        }

        public async Task<List<MediatRPatternMatcher.MediatRHandlerInfo>> FindAllHandlersAsync(INamedTypeSymbol requestTypeSymbol, SemanticModel semanticModel)
        {
            try
            {
                // Get services from ServiceLocator (solution-scoped)
                var workspaceService = ServiceLocator.TryGetService<IWorkspaceService>();
                var cacheService = ServiceLocator.TryGetService<IMediatRCacheService>();
                
                if (workspaceService == null)
                {
                    System.Diagnostics.Debug.WriteLine("MediatRNavigationExtension: MediatRHandlerFinder: No workspace service available - solution may not be open");
                    return new List<MediatRPatternMatcher.MediatRHandlerInfo>();
                }

                var workspace = workspaceService.GetWorkspace();
                
                if (workspace?.CurrentSolution == null)
                    return new List<MediatRPatternMatcher.MediatRHandlerInfo>();

                // Use the new method that finds all handlers (both request and notification)
                var allHandlers = await MediatRPatternMatcher.FindAllHandlersForTypeSymbol(workspace.CurrentSolution, requestTypeSymbol, semanticModel);

                // Get all request info to cache individual handler types
                var allRequestInfo = MediatRPatternMatcher.GetAllRequestInfo(requestTypeSymbol, semanticModel);
                
                // Group handlers by type and cache them individually (if cache service is available)
                if (cacheService != null)
                {
                    foreach (var requestInfo in allRequestInfo)
                    {
                        var handlersForThisType = allHandlers
                            .Where(h => h.RequestTypeName == requestInfo.RequestTypeName && h.IsNotificationHandler == requestInfo.IsNotification)
                            .ToList();
                        
                        if (handlersForThisType.Any())
                        {
                            cacheService.CacheHandlers(requestInfo.RequestTypeName, handlersForThisType);
                        }
                    }
                }

                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: MediatRHandlerFinder: Found {allHandlers.Count} total handlers for {requestTypeSymbol.Name}");
                
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
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: MediatRHandlerFinder: Error finding all handlers: {ex.Message}");
                return new List<MediatRPatternMatcher.MediatRHandlerInfo>();
            }
        }
    }
} 