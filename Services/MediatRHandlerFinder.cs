using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using VSIXExtention.Models;

namespace VSIXExtention.Services
{
    public class MediatRHandlerFinder
    {
        private readonly WorkspaceService _workspaceService;

        public MediatRHandlerFinder(WorkspaceService workspaceService)
        {
            _workspaceService = workspaceService;
        }

        public async Task<List<MediatRHandlerInfo>> FindAllHandlersAsync(INamedTypeSymbol requestTypeSymbol, SemanticModel semanticModel)
        {
            try
            {
                var workspace = _workspaceService.GetWorkspace();
                
                if (workspace?.CurrentSolution == null)
                    return new List<MediatRHandlerInfo>();

                // Use the new method that finds all handlers (both request and notification)
                var allHandlers = await MediatRPatternMatcher.FindAllHandlersForTypeSymbol(workspace.CurrentSolution, requestTypeSymbol, semanticModel);

                // Get all request info to cache individual handler types
                var allRequestInfo = MediatRPatternMatcher.GetAllRequestInfo(requestTypeSymbol, semanticModel);

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
                return new List<MediatRHandlerInfo>();
            }
        }
    }
} 