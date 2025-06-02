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

        public static bool IsMediatRRequest(INamedTypeSymbol typeSymbol, SemanticModel semanticModel)
        {
            if (typeSymbol == null) 
            {
                System.Diagnostics.Debug.WriteLine("MediatR Pattern: Type symbol is null");
                return false;
            }

            System.Diagnostics.Debug.WriteLine($"MediatR Pattern: Checking type: {typeSymbol.Name}");

            var interfaces = typeSymbol.AllInterfaces;
            System.Diagnostics.Debug.WriteLine($"MediatR Pattern: Found {interfaces.Length} interfaces");
            
            foreach (var @interface in interfaces)
            {
                var namespaceName = @interface.ContainingNamespace?.ToDisplayString();
                System.Diagnostics.Debug.WriteLine($"MediatR Pattern: Interface: {@interface.Name}, Namespace: {namespaceName}");
                
                if (namespaceName == "MediatR")
                {
                    if (@interface.Name == "IRequest" || @interface.Name == "INotification")
                    {
                        System.Diagnostics.Debug.WriteLine($"MediatR Pattern: Found MediatR interface: {@interface.Name}");
                        return true;
                    }
                }
            }

            System.Diagnostics.Debug.WriteLine("MediatR Pattern: No MediatR interfaces found");
            return false;
        }

        public static MediatRRequestInfo GetRequestInfo(INamedTypeSymbol typeSymbol, SemanticModel semanticModel)
        {
            if (!IsMediatRRequest(typeSymbol, semanticModel))
                return null;

            var interfaces = typeSymbol.AllInterfaces;
            
            foreach (var @interface in interfaces)
            {
                var namespaceName = @interface.ContainingNamespace?.ToDisplayString();
                if (namespaceName == "MediatR")
                {
                    if (@interface.Name == "IRequest")
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
                    else if (@interface.Name == "INotification")
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
            }

            return null;
        }

        public static bool IsMediatRHandler(INamedTypeSymbol typeSymbol, SemanticModel semanticModel)
        {
            if (typeSymbol == null) return false;

            var interfaces = typeSymbol.AllInterfaces;
            
            foreach (var @interface in interfaces)
            {
                var namespaceName = @interface.ContainingNamespace?.ToDisplayString();
                if (namespaceName == "MediatR")
                {
                    if (@interface.Name == "IRequestHandler" || @interface.Name == "INotificationHandler")
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public static MediatRHandlerInfo GetHandlerInfo(INamedTypeSymbol typeSymbol, SemanticModel semanticModel)
        {
            if (!IsMediatRHandler(typeSymbol, semanticModel))
                return null;

            var interfaces = typeSymbol.AllInterfaces;
            
            foreach (var @interface in interfaces)
            {
                var namespaceName = @interface.ContainingNamespace?.ToDisplayString();
                if (namespaceName == "MediatR")
                {
                    if (@interface.Name == "IRequestHandler" && @interface.TypeArguments.Length >= 1)
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
                    else if (@interface.Name == "INotificationHandler" && @interface.TypeArguments.Length >= 1)
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
            }

            return null;
        }

        public static async Task<List<MediatRHandlerInfo>> FindHandlersInSolution(Solution solution, string requestTypeName)
        {
            System.Diagnostics.Debug.WriteLine($"MediatR Pattern: Starting handler search for request type: {requestTypeName}");
            var handlers = new List<MediatRHandlerInfo>();

            foreach (var project in solution.Projects)
            {
                System.Diagnostics.Debug.WriteLine($"MediatR Pattern: Scanning project: {project.Name}");
                
                var compilation = await project.GetCompilationAsync();
                if (compilation == null) 
                {
                    System.Diagnostics.Debug.WriteLine($"MediatR Pattern: No compilation for project: {project.Name}");
                    continue;
                }

                System.Diagnostics.Debug.WriteLine($"MediatR Pattern: Project {project.Name} has {compilation.SyntaxTrees.Count()} syntax trees");

                foreach (var syntaxTree in compilation.SyntaxTrees)
                {
                    var semanticModel = compilation.GetSemanticModel(syntaxTree);
                    var root = await syntaxTree.GetRootAsync();

                    var classDeclarations = root.DescendantNodes().OfType<ClassDeclarationSyntax>();
                    var classCount = classDeclarations.Count();
                    
                    if (classCount > 0)
                    {
                        System.Diagnostics.Debug.WriteLine($"MediatR Pattern: Examining {classCount} classes in {syntaxTree.FilePath}");
                    }

                    foreach (var classDecl in classDeclarations)
                    {
                        var typeSymbol = semanticModel.GetDeclaredSymbol(classDecl) as INamedTypeSymbol;
                        if (typeSymbol == null) continue;

                        var handlerInfo = GetHandlerInfo(typeSymbol, semanticModel);
                        if (handlerInfo != null)
                        {
                            System.Diagnostics.Debug.WriteLine($"MediatR Pattern: Found handler candidate: {handlerInfo.HandlerTypeName} for request: {handlerInfo.RequestTypeName}");
                            
                            if (handlerInfo.RequestTypeName == requestTypeName)
                            {
                                System.Diagnostics.Debug.WriteLine($"MediatR Pattern: MATCH! Handler {handlerInfo.HandlerTypeName} matches request {requestTypeName}");
                                handlers.Add(handlerInfo);
                            }
                        }
                    }
                }
            }

            System.Diagnostics.Debug.WriteLine($"MediatR Pattern: Handler search complete. Found {handlers.Count} matching handlers.");
            return handlers;
        }

        public static async Task<List<MediatRHandlerInfo>> FindNotificationHandlersInSolution(Solution solution, string notificationTypeName)
        {
            var handlers = new List<MediatRHandlerInfo>();

            foreach (var project in solution.Projects)
            {
                var compilation = await project.GetCompilationAsync();
                if (compilation == null) continue;

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
            }

            return handlers;
        }
    }
} 