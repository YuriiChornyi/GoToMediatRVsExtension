using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace VSIXExtention
{
    public class MediatRPatternMatcher
    {
        public class MediatRRequestInfo
        {
            public string RequestTypeName { get; set; }
            public string ResponseTypeName { get; set; }
            public INamedTypeSymbol RequestSymbol { get; set; }
            public bool HasResponse { get; set; }
            public bool IsNotification { get; set; }
        }

        public class MediatRHandlerInfo
        {
            public string HandlerTypeName { get; set; }
            public string RequestTypeName { get; set; }
            public string ResponseTypeName { get; set; }
            public INamedTypeSymbol HandlerSymbol { get; set; }
            public Location Location { get; set; }
            public bool IsNotificationHandler { get; set; }
        }

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
        /// Gets a simple cache key for the request type
        /// </summary>
        private static string GetCacheKey(string requestTypeName, bool isNotification)
        {
            return $"{requestTypeName}::{(isNotification ? "notification" : "request")}";
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
            var allHandlers = new List<MediatRHandlerInfo>();
            var allRequestInfo = GetAllRequestInfo(typeSymbol, semanticModel);

            foreach (var requestInfo in allRequestInfo)
            {
                List<MediatRHandlerInfo> handlers;
                
                if (requestInfo.IsNotification)
                {
                    handlers = await FindNotificationHandlersInSolution(solution, requestInfo.RequestTypeName);
                }
                else
                {
                    handlers = await FindHandlersInSolution(solution, requestInfo.RequestTypeName);
                }
                
                allHandlers.AddRange(handlers);
            }

            return allHandlers;
        }

        public static async Task<List<MediatRHandlerInfo>> FindHandlersInSolution(Solution solution, string requestTypeName)
        {
            // Check cache first
            var cacheKey = GetCacheKey(requestTypeName, false);
            if (_sessionCache.TryGetValue(cacheKey, out var cachedHandlers))
            {
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: MediatRPatternMatcher: Cache hit for request handlers: {requestTypeName}");
                return cachedHandlers;
            }

            var handlers = new List<MediatRHandlerInfo>();

            // Use parallel processing for better performance
            var projectTasks = solution.Projects
                .Where(p => p.SupportsCompilation && p.HasDocuments) // Filter early
                .Select(async project =>
                {
                    var compilation = await project.GetCompilationAsync();
                    return compilation != null ? await FindHandlersInProject(compilation, requestTypeName) : new List<MediatRHandlerInfo>();
                });

            var projectResults = await Task.WhenAll(projectTasks);
            
            foreach (var projectHandlers in projectResults)
            {
                handlers.AddRange(projectHandlers);
            }

            // Cache the results
            _sessionCache.TryAdd(cacheKey, handlers);
            System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: MediatRPatternMatcher: Cached {handlers.Count} request handler(s) for: {requestTypeName}");

            return handlers;
        }

        public static async Task<List<MediatRHandlerInfo>> FindNotificationHandlersInSolution(Solution solution, string notificationTypeName)
        {
            // Check cache first
            var cacheKey = GetCacheKey(notificationTypeName, true);
            if (_sessionCache.TryGetValue(cacheKey, out var cachedHandlers))
            {
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: MediatRPatternMatcher: Cache hit for notification handlers: {notificationTypeName}");
                return cachedHandlers;
            }

            var handlers = new List<MediatRHandlerInfo>();

            // Use parallel processing for better performance
            var projectTasks = solution.Projects
                .Where(p => p.SupportsCompilation && p.HasDocuments) // Filter early
                .Select(async project =>
                {
                    var compilation = await project.GetCompilationAsync();
                    return compilation != null ? await FindNotificationHandlersInProject(compilation, notificationTypeName) : new List<MediatRHandlerInfo>();
                });

            var projectResults = await Task.WhenAll(projectTasks);
            
            foreach (var projectHandlers in projectResults)
            {
                handlers.AddRange(projectHandlers);
            }

            // Cache the results
            _sessionCache.TryAdd(cacheKey, handlers);
            System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: MediatRPatternMatcher: Cached {handlers.Count} notification handler(s) for: {notificationTypeName}");

            return handlers;
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