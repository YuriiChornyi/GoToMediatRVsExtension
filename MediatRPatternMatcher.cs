using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VSIXExtention.Models;

namespace VSIXExtention
{
    public class MediatRPatternMatcher
    {
        private const string MediatRNamespace = "MediatR";
        private const string RequestInterface = "IRequest";
        private const string NotificationInterface = "INotification";
        private const string RequestHandlerInterface = "IRequestHandler";
        private const string NotificationHandlerInterface = "INotificationHandler";

        // File extensions to consider for handler searches
        private static readonly string[] CSharpFileExtensions = { ".cs" };

        // Simple session-only memory cache - only stores results when user navigates
        private static readonly ConcurrentDictionary<string, List<MediatRHandlerInfo>> _sessionCache = new ConcurrentDictionary<string, List<MediatRHandlerInfo>>();

        /// <summary>
        /// Clears the session cache
        /// </summary>
        public static void ClearCache()
        {
            _sessionCache.Clear();
            System.Diagnostics.Debug.WriteLine("MediatRNavigationExtension: Session cache cleared");
        }

        /// <summary>
        /// Clears cache for a specific request type symbol (both request and notification variants)
        /// </summary>
        public static void ClearCacheForRequestType(INamedTypeSymbol requestTypeSymbol)
        {
            var requestKey = GetCacheKey(requestTypeSymbol, false);
            var notificationKey = GetCacheKey(requestTypeSymbol, true);
            
            bool requestRemoved = _sessionCache.TryRemove(requestKey, out _);
            bool notificationRemoved = _sessionCache.TryRemove(notificationKey, out _);
            
            if (requestRemoved || notificationRemoved)
            {
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Cache invalidated for request type: {requestTypeSymbol.Name}");
            }
        }

        /// <summary>
        /// Gets a more specific cache key using the full type symbol information
        /// </summary>
        private static string GetCacheKey(INamedTypeSymbol requestTypeSymbol, bool isNotification)
        {
            var fullTypeName = requestTypeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var assemblyName = requestTypeSymbol.ContainingAssembly?.Name ?? "Unknown";
            return $"{fullTypeName}::{assemblyName}::{(isNotification ? "notification" : "request")}";
        }

        public static bool IsMediatRRequest(INamedTypeSymbol typeSymbol, SemanticModel semanticModel)
        {
            if (typeSymbol == null) return false;

            return typeSymbol.AllInterfaces.Any(i =>
                i.ContainingNamespace?.ToDisplayString() == MediatRNamespace &&
                (i.Name == RequestInterface || i.Name == NotificationInterface));
        }

        /// <summary>
        /// Gets the first MediatR request info (for backward compatibility)
        /// </summary>
        public static MediatRRequestInfo GetRequestInfo(INamedTypeSymbol typeSymbol, SemanticModel semanticModel)
        {
            var allRequestInfo = GetAllRequestInfo(typeSymbol, semanticModel);
            return allRequestInfo.FirstOrDefault();
        }

        /// <summary>
        /// Gets all MediatR request info - handles classes that implement both IRequest and INotification
        /// </summary>
        public static List<MediatRRequestInfo> GetAllRequestInfo(INamedTypeSymbol typeSymbol, SemanticModel semanticModel)
        {
            var requestInfoList = new List<MediatRRequestInfo>();

            if (!IsMediatRRequest(typeSymbol, semanticModel))
                return requestInfoList;

            foreach (var @interface in typeSymbol.AllInterfaces.Where(i => i.ContainingNamespace?.ToDisplayString() == MediatRNamespace))
            {
                if (@interface.Name == RequestInterface)
                {
                    var hasResponse = @interface.TypeArguments.Length > 0;
                    var responseTypeName = hasResponse ? @interface.TypeArguments[0].Name : null;

                    requestInfoList.Add(new MediatRRequestInfo
                    {
                        RequestTypeName = typeSymbol.Name,
                        ResponseTypeName = responseTypeName,
                        RequestSymbol = typeSymbol,
                        HasResponse = hasResponse,
                        IsNotification = false
                    });
                }
                else if (@interface.Name == NotificationInterface)
                {
                    requestInfoList.Add(new MediatRRequestInfo
                    {
                        RequestTypeName = typeSymbol.Name,
                        ResponseTypeName = null,
                        RequestSymbol = typeSymbol,
                        HasResponse = false,
                        IsNotification = true
                    });
                }
            }

            return requestInfoList;
        }

        /// <summary>
        /// Checks if a type implements both IRequest and INotification
        /// </summary>
        public static bool ImplementsBothRequestAndNotification(INamedTypeSymbol typeSymbol, SemanticModel semanticModel)
        {
            if (typeSymbol == null) return false;

            var mediatrInterfaces = typeSymbol.AllInterfaces
                .Where(i => i.ContainingNamespace?.ToDisplayString() == MediatRNamespace)
                .ToList();

            bool hasRequest = mediatrInterfaces.Any(i => i.Name == RequestInterface);
            bool hasNotification = mediatrInterfaces.Any(i => i.Name == NotificationInterface);

            return hasRequest && hasNotification;
        }

        public static bool IsMediatRHandler(INamedTypeSymbol typeSymbol, SemanticModel semanticModel)
        {
            if (typeSymbol == null) return false;

            return typeSymbol.AllInterfaces.Any(i =>
                i.ContainingNamespace?.ToDisplayString() == MediatRNamespace &&
                (i.Name == RequestHandlerInterface || i.Name == NotificationHandlerInterface));
        }

        public static MediatRHandlerInfo GetHandlerInfo(INamedTypeSymbol typeSymbol, SemanticModel semanticModel)
        {
            if (!IsMediatRHandler(typeSymbol, semanticModel))
                return null;

            foreach (var @interface in typeSymbol.AllInterfaces.Where(i => i.ContainingNamespace?.ToDisplayString() == MediatRNamespace))
            {
                if (@interface.Name == RequestHandlerInterface && @interface.TypeArguments.Length >= 1)
                {
                    var requestTypeName = @interface.TypeArguments[0].Name;
                    var responseTypeName = @interface.TypeArguments.Length > 1 ? @interface.TypeArguments[1].Name : null;

                    return new MediatRHandlerInfo
                    {
                        HandlerTypeName = typeSymbol.Name,
                        RequestTypeName = requestTypeName,
                        ResponseTypeName = responseTypeName,
                        HandlerSymbol = typeSymbol,
                        Location = typeSymbol.Locations.FirstOrDefault(),
                        IsNotificationHandler = false
                    };
                }
                else if (@interface.Name == NotificationHandlerInterface && @interface.TypeArguments.Length >= 1)
                {
                    var requestTypeName = @interface.TypeArguments[0].Name;

                    return new MediatRHandlerInfo
                    {
                        HandlerTypeName = typeSymbol.Name,
                        RequestTypeName = requestTypeName,
                        ResponseTypeName = null,
                        HandlerSymbol = typeSymbol,
                        Location = typeSymbol.Locations.FirstOrDefault(),
                        IsNotificationHandler = true
                    };
                }
            }

            return null;
        }

        /// <summary>
        /// Finds all handlers (both request and notification) for a given type symbol
        /// </summary>
        public static async Task<List<MediatRHandlerInfo>> FindAllHandlersForTypeSymbol(Solution solution, INamedTypeSymbol typeSymbol, SemanticModel semanticModel)
        {
            var uniqueHandlers = new HashSet<MediatRHandlerInfo>();
            var allRequestInfo = GetAllRequestInfo(typeSymbol, semanticModel);

            foreach (var requestInfo in allRequestInfo)
            {
                List<MediatRHandlerInfo> handlers;
                
                if (requestInfo.IsNotification)
                {
                    handlers = await FindNotificationHandlersInSolutionBySymbol(solution, typeSymbol, true);
                }
                else
                {
                    handlers = await FindHandlersInSolutionBySymbol(solution, typeSymbol, false);
                }
                
                // Add handlers to HashSet to automatically deduplicate
                foreach (var handler in handlers)
                {
                    uniqueHandlers.Add(handler);
                }
            }

            var result = uniqueHandlers.ToList();
            System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: MediatRPatternMatcher: Found {result.Count} unique handlers after deduplication for {typeSymbol.Name}");
            return result;
        }

        /// <summary>
        /// Finds handlers in solution using type symbol for more specific caching
        /// </summary>
        public static async Task<List<MediatRHandlerInfo>> FindHandlersInSolutionBySymbol(Solution solution, INamedTypeSymbol requestTypeSymbol, bool isNotification)
        {
            // Check cache first using the more specific key
            var cacheKey = GetCacheKey(requestTypeSymbol, isNotification);
            if (_sessionCache.TryGetValue(cacheKey, out var cachedHandlers))
            {
                // Don't return empty cached results - they indicate stale cache
                if (cachedHandlers.Count > 0)
                {
                    System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: MediatRPatternMatcher: Cache hit for {(isNotification ? "notification" : "request")} handlers: {requestTypeSymbol.Name}");
                    return cachedHandlers;
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: MediatRPatternMatcher: Ignoring empty cache result for: {requestTypeSymbol.Name}");
                }
            }

            var handlers = new List<MediatRHandlerInfo>();
            var requestTypeName = requestTypeSymbol.Name;

            // Use parallel processing for better performance
            var projectTasks = solution.Projects
                .Where(p => p.SupportsCompilation && p.HasDocuments) // Filter early
                .Select(async project =>
                {
                    var compilation = await project.GetCompilationAsync();
                    return compilation != null ? 
                        (isNotification ? 
                            await FindNotificationHandlersInProject(compilation, requestTypeName) : 
                            await FindHandlersInProject(compilation, requestTypeName)) : 
                        new List<MediatRHandlerInfo>();
                });

            var projectResults = await Task.WhenAll(projectTasks);
            
            foreach (var projectHandlers in projectResults)
            {
                handlers.AddRange(projectHandlers);
            }

            // Only cache non-empty results to avoid cache invalidation bug
            if (handlers.Count > 0)
            {
                _sessionCache.TryAdd(cacheKey, handlers);
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: MediatRPatternMatcher: Cached {handlers.Count} {(isNotification ? "notification" : "request")} handler(s) for: {requestTypeSymbol.Name}");
                
                // Log handler details for debugging
                foreach (var handler in handlers)
                {
                    System.Diagnostics.Debug.WriteLine($"  - Found {(isNotification ? "notification" : "request")} handler: {handler.HandlerTypeName} at {handler.Location?.SourceTree?.FilePath}");
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: MediatRPatternMatcher: No handlers found for {requestTypeSymbol.Name} - not caching empty result");
            }

            return handlers;
        }

        /// <summary>
        /// Convenience method for finding notification handlers by symbol
        /// </summary>
        public static async Task<List<MediatRHandlerInfo>> FindNotificationHandlersInSolutionBySymbol(Solution solution, INamedTypeSymbol requestTypeSymbol, bool isNotification)
        {
            return await FindHandlersInSolutionBySymbol(solution, requestTypeSymbol, isNotification);
        }

        private static async Task<List<MediatRHandlerInfo>> FindHandlersInProject(Compilation compilation, string requestTypeName)
        {
            var handlers = new List<MediatRHandlerInfo>();

            // Filter syntax trees early - only process C# files
            var relevantTrees = compilation.SyntaxTrees
                .Where(tree => tree.FilePath != null && CSharpFileExtensions.Any(ext => tree.FilePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase)));

            foreach (var syntaxTree in relevantTrees)
            {
                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = await syntaxTree.GetRootAsync();

                var typeDeclarations = root.DescendantNodes().OfType<TypeDeclarationSyntax>();

                foreach (var typeDecl in typeDeclarations)
                {
                    var typeSymbol = semanticModel.GetDeclaredSymbol(typeDecl) as INamedTypeSymbol;
                    if (typeSymbol == null) continue;

                    var handlerInfo = GetHandlerInfo(typeSymbol, semanticModel);
                    if (handlerInfo?.RequestTypeName == requestTypeName && !handlerInfo.IsNotificationHandler)
                    {
                        handlers.Add(handlerInfo);
                    }
                }
            }

            return handlers;
        }

        private static async Task<List<MediatRHandlerInfo>> FindNotificationHandlersInProject(Compilation compilation, string notificationTypeName)
        {
            var handlers = new List<MediatRHandlerInfo>();

            // Filter syntax trees early - only process C# files
            var relevantTrees = compilation.SyntaxTrees
                .Where(tree => tree.FilePath != null && CSharpFileExtensions.Any(ext => tree.FilePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase)));

            foreach (var syntaxTree in relevantTrees)
            {
                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = await syntaxTree.GetRootAsync();

                var typeDeclarations = root.DescendantNodes().OfType<TypeDeclarationSyntax>();

                foreach (var typeDecl in typeDeclarations)
                {
                    var typeSymbol = semanticModel.GetDeclaredSymbol(typeDecl) as INamedTypeSymbol;
                    if (typeSymbol == null) continue;

                    var handlerInfo = GetHandlerInfo(typeSymbol, semanticModel);
                    if (handlerInfo != null && handlerInfo.IsNotificationHandler && handlerInfo.RequestTypeName == notificationTypeName)
                    {
                        handlers.Add(handlerInfo);
                    }
                }
            }

            return handlers;
        }
    }
}