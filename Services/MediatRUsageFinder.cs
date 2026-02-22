using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
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
        public async Task<List<MediatRUsageInfo>> FindUsagesAsync(INamedTypeSymbol requestTypeSymbol, CancellationToken cancellationToken = default)
        {
            var workspace = await _workspaceService.GetWorkspaceAsync();
            if (workspace?.CurrentSolution == null)
                return new List<MediatRUsageInfo>();

            var usages = new List<MediatRUsageInfo>();
            var requestTypeName = requestTypeSymbol.Name;

            System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: MediatRUsageFinder: Searching for usages of {requestTypeName}");

            // Process all projects in parallel
            var projectTasks = workspace.CurrentSolution.Projects
                .Where(p => p.SupportsCompilation && p.HasDocuments && p.Language == LanguageNames.CSharp)
                .Select(async project =>
                {
                    var compilation = await project.GetCompilationAsync(cancellationToken);
                    if (compilation == null)
                    {
                        return new List<MediatRUsageInfo>();
                    }

                    // Scope: process only projects that reference MediatR
                    var hasMediatR = compilation.GetTypeByMetadataName("MediatR.IRequest") != null ||
                                      compilation.GetTypeByMetadataName("MediatR.INotification") != null ||
                                      compilation.GetTypeByMetadataName("MediatR.IRequestHandler`2") != null ||
                                      compilation.GetTypeByMetadataName("MediatR.IRequestHandler`1") != null ||
                                      compilation.GetTypeByMetadataName("MediatR.INotificationHandler`1") != null ||
                                      compilation.GetTypeByMetadataName("MediatR.IMediator") != null ||
                                      compilation.GetTypeByMetadataName("MediatR.ISender") != null ||
                                      compilation.GetTypeByMetadataName("MediatR.IPublisher") != null;
                    if (!hasMediatR)
                    {
                        return new List<MediatRUsageInfo>();
                    }

                    return await FindUsagesInProject(compilation, requestTypeSymbol, cancellationToken);
                });

            var projectResults = await Task.WhenAll(projectTasks);
            
            foreach (var projectUsages in projectResults)
            {
                usages.AddRange(projectUsages);
            }

            System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: MediatRUsageFinder: Found {usages.Count} usages for {requestTypeName}");
            return usages;
        }

        private async Task<List<MediatRUsageInfo>> FindUsagesInProject(Compilation compilation, INamedTypeSymbol requestTypeSymbol, CancellationToken cancellationToken)
        {
            var usages = new List<MediatRUsageInfo>();
            var requestTypeName = requestTypeSymbol.Name;

            // Filter syntax trees early - only process C# files
            var relevantTrees = compilation.SyntaxTrees
                .Where(tree => tree.FilePath != null && tree.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));

            foreach (var syntaxTree in relevantTrees)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = await syntaxTree.GetRootAsync(cancellationToken);

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
                var containingType = GetContainingType(invocation);

                var usageInfo = new MediatRUsageInfo
                {
                    RequestTypeName = requestTypeSymbol.Name,
                    MethodName = containingMethod?.Identifier.ValueText ?? "Unknown",
                    ClassName = containingType?.Identifier.ValueText ?? "Unknown",
                    FilePath = location.SourceTree?.FilePath ?? "Unknown",
                    LineNumber = location.GetLineSpan().StartLinePosition.Line + 1,
                    Location = location,
                    IsNotificationUsage = isNotification,
                    UsageType = methodName,
                    ContextDescription = CreateContextDescription(containingMethod, containingType)
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
                    // 1) Prefer IOperation-based analysis to unwrap conversions and variable references
                    var op = semanticModel.GetOperation(firstArgument.Expression);
                    var resolved = ResolveConcreteRequestTypeFromOperation(op, semanticModel);
                    if (resolved != null && AreTypesEqual(resolved, requestTypeSymbol))
                        return resolved;

                    // 2) Fallback to TypeInfo on the expression
                    var typeInfo = semanticModel.GetTypeInfo(firstArgument.Expression);
                    var argumentType = typeInfo.Type as INamedTypeSymbol ?? typeInfo.ConvertedType as INamedTypeSymbol;
                    if (argumentType != null && AreTypesEqual(argumentType, requestTypeSymbol))
                        return argumentType;
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private INamedTypeSymbol ResolveConcreteRequestTypeFromOperation(IOperation operation, SemanticModel semanticModel)
        {
            if (operation == null) return null;

            operation = UnwrapConversion(operation);

            switch (operation)
            {
                case IObjectCreationOperation objectCreation:
                    return objectCreation.Type as INamedTypeSymbol;

                case ILocalReferenceOperation localRef:
                    var local = localRef.Local;
                    var declSyntax = local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() as VariableDeclaratorSyntax;
                    var initializerExpr = declSyntax?.Initializer?.Value;
                    if (initializerExpr != null)
                    {
                        var initOp = semanticModel.GetOperation(initializerExpr);
                        return ResolveConcreteRequestTypeFromOperation(initOp, semanticModel);
                    }
                    // fallback to local static type if it's already concrete
                    return local.Type as INamedTypeSymbol;

                case IFieldReferenceOperation fieldRef:
                    var fieldDeclSyntax = fieldRef.Field.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() as VariableDeclaratorSyntax;
                    var fieldInitExpr = fieldDeclSyntax?.Initializer?.Value;
                    if (fieldInitExpr != null)
                    {
                        var fieldInitOp = semanticModel.GetOperation(fieldInitExpr);
                        return ResolveConcreteRequestTypeFromOperation(fieldInitOp, semanticModel);
                    }
                    return fieldRef.Field.Type as INamedTypeSymbol;

                case IPropertyReferenceOperation propRef:
                    // Try to get initializer from auto-property if available
                    var propDeclSyntax = propRef.Property.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() as PropertyDeclarationSyntax;
                    var propInitExpr = propDeclSyntax?.Initializer?.Value;
                    if (propInitExpr != null)
                    {
                        var propInitOp = semanticModel.GetOperation(propInitExpr);
                        return ResolveConcreteRequestTypeFromOperation(propInitOp, semanticModel);
                    }
                    return propRef.Property.Type as INamedTypeSymbol;

                case IParameterReferenceOperation paramRef:
                    // No reliable way to get concrete construction at callsite from here; return parameter type
                    return paramRef.Parameter.Type as INamedTypeSymbol;
            }

            return operation.Type as INamedTypeSymbol;
        }

        private IOperation UnwrapConversion(IOperation operation)
        {
            var current = operation;
            while (current is IConversionOperation conv)
            {
                current = conv.Operand;
            }
            return current;
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

        private TypeDeclarationSyntax GetContainingType(SyntaxNode node)
        {
            return node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        }

        private string CreateContextDescription(MethodDeclarationSyntax method, TypeDeclarationSyntax containingType)
        {
            if (method == null || containingType == null)
                return "Unknown context";

            var methodName = method.Identifier.ValueText;
            var typeName = containingType.Identifier.ValueText;

            return $"{methodName}() method in {typeName}";
        }
    }
} 