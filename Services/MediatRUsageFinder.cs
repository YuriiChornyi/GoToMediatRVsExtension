using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using VSIXExtention.Models;

namespace VSIXExtention.Services
{
    public class MediatRUsageFinder
    {
        private readonly WorkspaceService _workspaceService;
        private const string MediatRNamespace = "MediatR";
        private static readonly string[] SendMethods = { "Send", "SendAsync" };
        private static readonly string[] PublishMethods = { "Publish", "PublishAsync" };

        public MediatRUsageFinder(WorkspaceService workspaceService)
        {
            _workspaceService = workspaceService;
        }

        /// <summary>
        /// Finds all places where a specific request type is sent or published
        /// </summary>
        public async Task<List<MediatRUsageInfo>> FindUsagesAsync(INamedTypeSymbol requestTypeSymbol)
        {
            var workspace = _workspaceService.GetWorkspace();
            if (workspace?.CurrentSolution == null)
                return new List<MediatRUsageInfo>();

            var usages = new List<MediatRUsageInfo>();
            var requestTypeName = requestTypeSymbol.Name;

            System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: MediatRUsageFinder: Searching for usages of {requestTypeName}");

            // Process all projects in parallel
            var projectTasks = workspace.CurrentSolution.Projects
                .Where(p => p.SupportsCompilation && p.HasDocuments)
                .Select(async project =>
                {
                    var compilation = await project.GetCompilationAsync();
                    return compilation != null ? 
                        await FindUsagesInProject(compilation, requestTypeSymbol) : 
                        new List<MediatRUsageInfo>();
                });

            var projectResults = await Task.WhenAll(projectTasks);
            
            foreach (var projectUsages in projectResults)
            {
                usages.AddRange(projectUsages);
            }

            System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: MediatRUsageFinder: Found {usages.Count} usages for {requestTypeName}");
            return usages;
        }

        private async Task<List<MediatRUsageInfo>> FindUsagesInProject(Compilation compilation, INamedTypeSymbol requestTypeSymbol)
        {
            var usages = new List<MediatRUsageInfo>();
            var requestTypeName = requestTypeSymbol.Name;

            // Filter syntax trees early - only process C# files
            var relevantTrees = compilation.SyntaxTrees
                .Where(tree => tree.FilePath != null && tree.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));

            foreach (var syntaxTree in relevantTrees)
            {
                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = await syntaxTree.GetRootAsync();

                // Find all method invocations
                var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();

                foreach (var invocation in invocations)
                {
                    var usageInfo = AnalyzeInvocation(invocation, semanticModel, requestTypeSymbol);
                    if (usageInfo != null)
                    {
                        usages.Add(usageInfo);
                    }
                }
            }

            return usages;
        }

        private MediatRUsageInfo AnalyzeInvocation(InvocationExpressionSyntax invocation, SemanticModel semanticModel, INamedTypeSymbol requestTypeSymbol)
        {
            try
            {
                // Check if this is a MediatR Send/Publish call
                if (!IsMediatRCall(invocation, semanticModel, out string methodName, out bool isNotification))
                {
                    return null;
                }

                // Analyze the arguments to see if our request type is being sent
                var sentType = GetSentRequestType(invocation, semanticModel, requestTypeSymbol);
                if (sentType == null)
                {
                    return null;
                }

                // Get context information
                var location = invocation.GetLocation();
                var containingMethod = GetContainingMethod(invocation);
                var containingClass = GetContainingClass(invocation);

                var usageInfo = new MediatRUsageInfo
                {
                    RequestTypeName = requestTypeSymbol.Name,
                    MethodName = containingMethod?.Identifier.ValueText ?? "Unknown",
                    ClassName = containingClass?.Identifier.ValueText ?? "Unknown",
                    FilePath = location.SourceTree?.FilePath ?? "Unknown",
                    LineNumber = location.GetLineSpan().StartLinePosition.Line + 1,
                    Location = location,
                    IsNotificationUsage = isNotification,
                    UsageType = methodName,
                    ContextDescription = CreateContextDescription(containingMethod, containingClass)
                };

                return usageInfo;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: MediatRUsageFinder: Error analyzing invocation: {ex.Message}");
                return null;
            }
        }

        private bool IsMediatRCall(InvocationExpressionSyntax invocation, SemanticModel semanticModel, out string methodName, out bool isNotification)
        {
            methodName = null;
            isNotification = false;

            // Check if this is a member access (e.g., _mediator.Send(...))
            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                methodName = memberAccess.Name.Identifier.ValueText;
                
                // Check if it's a Send or Publish method
                if (SendMethods.Contains(methodName))
                {
                    isNotification = false;
                    return IsMediatRService(memberAccess.Expression, semanticModel);
                }
                else if (PublishMethods.Contains(methodName))
                {
                    isNotification = true;
                    return IsMediatRService(memberAccess.Expression, semanticModel);
                }
            }

            return false;
        }

        private bool IsMediatRService(ExpressionSyntax expression, SemanticModel semanticModel)
        {
            try
            {
                var symbolInfo = semanticModel.GetSymbolInfo(expression);
                var symbol = symbolInfo.Symbol;

                if (symbol != null)
                {
                    ITypeSymbol typeSymbol = null;
                    
                    // Handle different types of symbols
                    switch (symbol)
                    {
                        case IFieldSymbol fieldSymbol:
                            typeSymbol = fieldSymbol.Type;
                            break;
                        case IPropertySymbol propertySymbol:
                            typeSymbol = propertySymbol.Type;
                            break;
                        case IParameterSymbol parameterSymbol:
                            typeSymbol = parameterSymbol.Type;
                            break;
                        case ILocalSymbol localSymbol:
                            typeSymbol = localSymbol.Type;
                            break;
                        case IMethodSymbol methodSymbol when methodSymbol.ReturnType != null:
                            typeSymbol = methodSymbol.ReturnType;
                            break;
                    }

                    if (typeSymbol?.ContainingNamespace?.ToDisplayString() == MediatRNamespace)
                    {
                        return typeSymbol.Name == "IMediator" || typeSymbol.Name == "ISender" || typeSymbol.Name == "IPublisher";
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private INamedTypeSymbol GetSentRequestType(InvocationExpressionSyntax invocation, SemanticModel semanticModel, INamedTypeSymbol requestTypeSymbol)
        {
            try
            {
                if (invocation.ArgumentList?.Arguments.Count > 0)
                {
                    var firstArgument = invocation.ArgumentList.Arguments[0];
                    var argumentType = semanticModel.GetTypeInfo(firstArgument.Expression).Type as INamedTypeSymbol;

                    if (argumentType != null)
                    {
                        // Compare symbols by metadata identity instead of reference equality
                        if (AreTypesEqual(argumentType, requestTypeSymbol))
                        {
                            return argumentType;
                        }
                    }
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private bool AreTypesEqual(INamedTypeSymbol type1, INamedTypeSymbol type2)
        {
            // Try 1: Standard comparison
            if (SymbolEqualityComparer.Default.Equals(type1, type2)) return true;
            
            // Try 2: Fully qualified names
            if (type1.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == type2.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)) return true;
            
            // Try 3: Metadata + assembly (most robust)
            return GetMetadataName(type1) == GetMetadataName(type2) && 
                   type1.ContainingAssembly?.Name == type2.ContainingAssembly?.Name;
        }

        private string GetMetadataName(INamedTypeSymbol type)
        {
            if (type.ContainingType != null)
            {
                // Nested type
                return GetMetadataName(type.ContainingType) + "+" + type.MetadataName;
            }
            
            var namespaceName = type.ContainingNamespace?.ToDisplayString();
            if (string.IsNullOrEmpty(namespaceName) || namespaceName == "<global namespace>")
            {
                return type.MetadataName;
            }
            
            return namespaceName + "." + type.MetadataName;
        }

        private MethodDeclarationSyntax GetContainingMethod(SyntaxNode node)
        {
            return node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        }

        private TypeDeclarationSyntax GetContainingClass(SyntaxNode node)
        {
            return node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        }

        private string CreateContextDescription(MethodDeclarationSyntax method, TypeDeclarationSyntax containingClass)
        {
            if (method == null || containingClass == null)
                return "Unknown context";

            var methodName = method.Identifier.ValueText;
            var className = containingClass.Identifier.ValueText;

            return $"{methodName}() method in {className}";
        }
    }
} 