using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VSIXExtension.Models;

namespace VSIXExtension
{
    public class MediatRPatternMatcher
    {
        private const string MediatRNamespace = "MediatR";
        private const string RequestInterface = "IRequest";
        private const string NotificationInterface = "INotification";
        private const string RequestHandlerInterface = "IRequestHandler";
        private const string NotificationHandlerInterface = "INotificationHandler";
        private const string StreamRequestHandlerInterface = "IStreamRequestHandler";
        private const string RequestExceptionHandlerInterface = "IRequestExceptionHandler";
        private const string RequestExceptionActionInterface = "IRequestExceptionAction";

        // File extensions to consider for handler searches
        private static readonly string[] CSharpFileExtensions = { ".cs" };

        // (Removed caching: previously used a session-only cache for handlers)

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
                (i.Name == RequestHandlerInterface ||
                 i.Name == NotificationHandlerInterface ||
                 i.Name == StreamRequestHandlerInterface ||
                 i.Name == RequestExceptionHandlerInterface ||
                 i.Name == RequestExceptionActionInterface));
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

                    var handlerLocation = GetHandlerMethodLocation(typeSymbol, @interface, "Handle");

                    return new MediatRHandlerInfo
                    {
                        HandlerTypeName = typeSymbol.Name,
                        RequestTypeName = requestTypeName,
                        ResponseTypeName = responseTypeName,
                        RequestTypeSymbol = @interface.TypeArguments[0] as INamedTypeSymbol,
                        HandlerSymbol = typeSymbol,
                        Location = handlerLocation ?? typeSymbol.Locations.FirstOrDefault(),
                        IsNotificationHandler = false,
                        HandlerType = MediatRHandlerType.RequestHandler,
                        IsStreamHandler = false,
                        IsExceptionHandler = false
                    };
                }
                else if (@interface.Name == NotificationHandlerInterface && @interface.TypeArguments.Length >= 1)
                {
                    var requestTypeName = @interface.TypeArguments[0].Name;
                    var handlerLocation = GetHandlerMethodLocation(typeSymbol, @interface, "Handle");

                    return new MediatRHandlerInfo
                    {
                        HandlerTypeName = typeSymbol.Name,
                        RequestTypeName = requestTypeName,
                        ResponseTypeName = null,
                        RequestTypeSymbol = @interface.TypeArguments[0] as INamedTypeSymbol,
                        HandlerSymbol = typeSymbol,
                        Location = handlerLocation ?? typeSymbol.Locations.FirstOrDefault(),
                        IsNotificationHandler = true,
                        HandlerType = MediatRHandlerType.NotificationHandler,
                        IsStreamHandler = false,
                        IsExceptionHandler = false
                    };
                }
                else if (@interface.Name == StreamRequestHandlerInterface && @interface.TypeArguments.Length >= 2)
                {
                    var requestTypeName = @interface.TypeArguments[0].Name;
                    var responseTypeName = @interface.TypeArguments[1].Name;
                    var handlerLocation = GetHandlerMethodLocation(typeSymbol, @interface, "Handle");

                    return new MediatRHandlerInfo
                    {
                        HandlerTypeName = typeSymbol.Name,
                        RequestTypeName = requestTypeName,
                        ResponseTypeName = responseTypeName,
                        RequestTypeSymbol = @interface.TypeArguments[0] as INamedTypeSymbol,
                        HandlerSymbol = typeSymbol,
                        Location = handlerLocation ?? typeSymbol.Locations.FirstOrDefault(),
                        IsNotificationHandler = false,
                        HandlerType = MediatRHandlerType.StreamRequestHandler,
                        IsStreamHandler = true,
                        IsExceptionHandler = false
                    };
                }
                else if (@interface.Name == RequestExceptionHandlerInterface && @interface.TypeArguments.Length >= 3)
                {
                    var requestTypeName = @interface.TypeArguments[0].Name;
                    var responseTypeName = @interface.TypeArguments[1].Name;
                    var exceptionTypeName = @interface.TypeArguments[2].Name;
                    var handlerLocation = GetHandlerMethodLocation(typeSymbol, @interface, "Handle");

                    return new MediatRHandlerInfo
                    {
                        HandlerTypeName = typeSymbol.Name,
                        RequestTypeName = requestTypeName,
                        ResponseTypeName = responseTypeName,
                        RequestTypeSymbol = @interface.TypeArguments[0] as INamedTypeSymbol,
                        HandlerSymbol = typeSymbol,
                        Location = handlerLocation ?? typeSymbol.Locations.FirstOrDefault(),
                        IsNotificationHandler = false,
                        HandlerType = MediatRHandlerType.RequestExceptionHandler,
                        IsStreamHandler = false,
                        IsExceptionHandler = true,
                        ExceptionTypeName = exceptionTypeName,
                        ExceptionTypeSymbol = @interface.TypeArguments[2] as INamedTypeSymbol
                    };
                }
                else if (@interface.Name == RequestExceptionActionInterface && @interface.TypeArguments.Length >= 2)
                {
                    var requestTypeName = @interface.TypeArguments[0].Name;
                    var exceptionTypeName = @interface.TypeArguments[1].Name;
                    var handlerLocation = GetHandlerMethodLocation(typeSymbol, @interface, "Execute");

                    return new MediatRHandlerInfo
                    {
                        HandlerTypeName = typeSymbol.Name,
                        RequestTypeName = requestTypeName,
                        ResponseTypeName = null,
                        RequestTypeSymbol = @interface.TypeArguments[0] as INamedTypeSymbol,
                        HandlerSymbol = typeSymbol,
                        Location = handlerLocation ?? typeSymbol.Locations.FirstOrDefault(),
                        IsNotificationHandler = false,
                        HandlerType = MediatRHandlerType.RequestExceptionAction,
                        IsStreamHandler = false,
                        IsExceptionHandler = true,
                        ExceptionTypeName = exceptionTypeName,
                        ExceptionTypeSymbol = @interface.TypeArguments[1] as INamedTypeSymbol
                    };
                }
            }

            return null;
        }

        private static Location GetHandlerMethodLocation(INamedTypeSymbol typeSymbol, INamedTypeSymbol @interface, string methodName)
        {
            try
            {
                var interfaceMethod = @interface.GetMembers()
                    .OfType<IMethodSymbol>()
                    .FirstOrDefault(m => m.Name == methodName);
                var implementation = interfaceMethod != null
                    ? typeSymbol.FindImplementationForInterfaceMember(interfaceMethod) as IMethodSymbol
                    : null;
                return implementation?.Locations.FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Finds all handler types (request, notification, stream, exception handlers, and exception actions) for a given type symbol
        /// </summary>
        public static async Task<List<MediatRHandlerInfo>> FindAllHandlersForTypeSymbol(Solution solution, INamedTypeSymbol typeSymbol, SemanticModel semanticModel, CancellationToken cancellationToken = default)
        {
            var uniqueHandlers = new HashSet<MediatRHandlerInfo>();

            // Find all handler types for this request/notification type
            var handlers = await FindHandlersInSolutionBySymbol(solution, typeSymbol, cancellationToken);

            // Add handlers to HashSet to automatically deduplicate
            foreach (var handler in handlers)
            {
                uniqueHandlers.Add(handler);
            }

            var result = uniqueHandlers.ToList();
            System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: MediatRPatternMatcher: Found {result.Count} unique handlers after deduplication for {typeSymbol.Name}");
            return result;
        }

        /// <summary>
        /// Finds all handler types in solution for a given request type symbol
        /// </summary>
        public static async Task<List<MediatRHandlerInfo>> FindHandlersInSolutionBySymbol(Solution solution, INamedTypeSymbol requestTypeSymbol, CancellationToken cancellationToken = default)
        {
            var handlers = new List<MediatRHandlerInfo>();
            var requestTypeName = requestTypeSymbol.Name;

            // Use parallel processing for better performance
            var projectTasks = solution.Projects
                .Where(p => p.SupportsCompilation && p.HasDocuments && p.Language == LanguageNames.CSharp) // Filter early to C# compilations only
                .Select(async project =>
                {
                    var compilation = await project.GetCompilationAsync(cancellationToken);
                    if (compilation == null)
                    {
                        return new List<MediatRHandlerInfo>();
                    }

                    // Scope: process only projects that reference MediatR
                    var hasMediatR = compilation.GetTypeByMetadataName("MediatR.IRequest") != null ||
                                      compilation.GetTypeByMetadataName("MediatR.INotification") != null ||
                                      compilation.GetTypeByMetadataName("MediatR.IRequestHandler`2") != null ||
                                      compilation.GetTypeByMetadataName("MediatR.IRequestHandler`1") != null ||
                                      compilation.GetTypeByMetadataName("MediatR.INotificationHandler`1") != null ||
                                      compilation.GetTypeByMetadataName("MediatR.IStreamRequestHandler`2") != null ||
                                      compilation.GetTypeByMetadataName("MediatR.IRequestExceptionHandler`3") != null ||
                                      compilation.GetTypeByMetadataName("MediatR.IRequestExceptionAction`2") != null;
                    if (!hasMediatR)
                    {
                        return new List<MediatRHandlerInfo>();
                    }

                    return await FindAllHandlerTypesInProject(compilation, requestTypeSymbol, cancellationToken);
                });

            var projectResults = await Task.WhenAll(projectTasks);

            foreach (var projectHandlers in projectResults)
            {
                handlers.AddRange(projectHandlers);
            }

            // Log handler details for debugging
            if (handlers.Count > 0)
            {
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: MediatRPatternMatcher: Found {handlers.Count} handler(s) for: {requestTypeSymbol.Name}");
                foreach (var handler in handlers)
                {
                    var handlerTypeDesc = GetHandlerTypeDescription(handler.HandlerType);
                    System.Diagnostics.Debug.WriteLine($"  - Found {handlerTypeDesc}: {handler.HandlerTypeName} at {handler.Location?.SourceTree?.FilePath}");
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: MediatRPatternMatcher: No handlers found for {requestTypeSymbol.Name}");
            }

            return handlers;
        }


        /// <summary>
        /// Finds all handler types (request, notification, stream, exception handlers, and exception actions) for a given request type in a project
        /// </summary>
        private static async Task<List<MediatRHandlerInfo>> FindAllHandlerTypesInProject(Compilation compilation, INamedTypeSymbol requestTypeSymbol, CancellationToken cancellationToken)
        {
            var handlers = new List<MediatRHandlerInfo>();

            // Filter syntax trees early - only process C# files
            var relevantTrees = compilation.SyntaxTrees
                .Where(tree => tree.FilePath != null && CSharpFileExtensions.Any(ext => tree.FilePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase)));

            foreach (var syntaxTree in relevantTrees)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = await syntaxTree.GetRootAsync(cancellationToken);

                var typeDeclarations = root.DescendantNodes().OfType<TypeDeclarationSyntax>();

                foreach (var typeDecl in typeDeclarations)
                {
                    var typeSymbol = semanticModel.GetDeclaredSymbol(typeDecl) as INamedTypeSymbol;
                    if (typeSymbol == null) continue;

                    var handlerInfo = GetHandlerInfo(typeSymbol, semanticModel);
                    if (handlerInfo != null && AreTypesEqual(handlerInfo.RequestTypeSymbol, requestTypeSymbol))
                    {
                        handlers.Add(handlerInfo);
                    }
                }
            }

            return handlers;
        }

        private static string GetHandlerTypeDescription(MediatRHandlerType handlerType)
        {
            switch (handlerType)
            {
                case MediatRHandlerType.RequestHandler:
                    return "request handler";
                case MediatRHandlerType.NotificationHandler:
                    return "notification handler";
                case MediatRHandlerType.StreamRequestHandler:
                    return "stream handler";
                case MediatRHandlerType.RequestExceptionHandler:
                    return "exception handler";
                case MediatRHandlerType.RequestExceptionAction:
                    return "exception action";
                default:
                    return "handler";
            }
        }

        private static bool AreTypesEqual(INamedTypeSymbol type1, INamedTypeSymbol type2)
        {
            if (type1 == null || type2 == null) return false;
            if (SymbolEqualityComparer.Default.Equals(type1, type2)) return true;
            if (type1.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == type2.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)) return true;

            string GetMetadataName(INamedTypeSymbol t)
            {
                if (t.ContainingType != null)
                {
                    return GetMetadataName(t.ContainingType) + "+" + t.MetadataName;
                }
                var ns = t.ContainingNamespace?.ToDisplayString();
                return string.IsNullOrEmpty(ns) || ns == "<global namespace>" ? t.MetadataName : ns + "." + t.MetadataName;
            }

            return GetMetadataName(type1) == GetMetadataName(type2) &&
                   type1.ContainingAssembly?.Name == type2.ContainingAssembly?.Name;
        }
    }
}