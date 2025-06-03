using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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

        public static bool IsMediatRRequest(INamedTypeSymbol typeSymbol, SemanticModel semanticModel)
        {
            if (typeSymbol == null) return false;

            return typeSymbol.AllInterfaces.Any(i => 
                i.ContainingNamespace?.ToDisplayString() == MediatRNamespace &&
                (i.Name == RequestInterface || i.Name == NotificationInterface));
        }

        public static MediatRRequestInfo GetRequestInfo(INamedTypeSymbol typeSymbol, SemanticModel semanticModel)
        {
            if (!IsMediatRRequest(typeSymbol, semanticModel))
                return null;

            foreach (var @interface in typeSymbol.AllInterfaces.Where(i => i.ContainingNamespace?.ToDisplayString() == MediatRNamespace))
            {
                if (@interface.Name == RequestInterface)
                {
                    var hasResponse = @interface.TypeArguments.Length > 0;
                    var responseTypeName = hasResponse ? @interface.TypeArguments[0].Name : null;

                    return new MediatRRequestInfo
                    {
                        RequestTypeName = typeSymbol.Name,
                        ResponseTypeName = responseTypeName,
                        RequestSymbol = typeSymbol,
                        HasResponse = hasResponse,
                        IsNotification = false
                    };
                }
                else if (@interface.Name == NotificationInterface)
                {
                    return new MediatRRequestInfo
                    {
                        RequestTypeName = typeSymbol.Name,
                        ResponseTypeName = null,
                        RequestSymbol = typeSymbol,
                        HasResponse = false,
                        IsNotification = true
                    };
                }
            }

            return null;
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

        public static async Task<List<MediatRHandlerInfo>> FindHandlersInSolution(Solution solution, string requestTypeName)
        {
            var handlers = new List<MediatRHandlerInfo>();

            foreach (var project in solution.Projects)
            {
                var compilation = await project.GetCompilationAsync();
                if (compilation == null) continue;

                var projectHandlers = await FindHandlersInProject(compilation, requestTypeName);
                handlers.AddRange(projectHandlers);
            }

            return handlers;
        }

        public static async Task<List<MediatRHandlerInfo>> FindNotificationHandlersInSolution(Solution solution, string notificationTypeName)
        {
            var handlers = new List<MediatRHandlerInfo>();

            foreach (var project in solution.Projects)
            {
                var compilation = await project.GetCompilationAsync();
                if (compilation == null) continue;

                var projectHandlers = await FindNotificationHandlersInProject(compilation, notificationTypeName);
                handlers.AddRange(projectHandlers);
            }

            return handlers;
        }

        private static async Task<List<MediatRHandlerInfo>> FindHandlersInProject(Compilation compilation, string requestTypeName)
        {
            var handlers = new List<MediatRHandlerInfo>();

            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = await syntaxTree.GetRootAsync();

                var classDeclarations = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

                foreach (var classDecl in classDeclarations)
                {
                    var typeSymbol = semanticModel.GetDeclaredSymbol(classDecl) as INamedTypeSymbol;
                    if (typeSymbol == null) continue;

                    var handlerInfo = GetHandlerInfo(typeSymbol, semanticModel);
                    if (handlerInfo?.RequestTypeName == requestTypeName)
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

            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = await syntaxTree.GetRootAsync();

                var classDeclarations = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

                foreach (var classDecl in classDeclarations)
                {
                    var typeSymbol = semanticModel.GetDeclaredSymbol(classDecl) as INamedTypeSymbol;
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