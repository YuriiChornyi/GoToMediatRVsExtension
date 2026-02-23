using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Language.CodeLens;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Utilities;
using System;
using System.Collections.Concurrent;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VSIXExtension.Models;
using VSIXExtension.Options;

namespace VSIXExtension.Services
{
    [Export(typeof(ICodeLensCallbackListener))]
    [PartCreationPolicy(CreationPolicy.Shared)]
    [ContentType("CSharp")]
    public class CodeLensCallbackService : ICodeLensCallbackListener, IDisposable
    {
        private VisualStudioWorkspace _workspace;
        private bool _workspaceChangeSubscribed;
        private CancellationTokenSource _refreshCts;
        private readonly object _lock = new object();

        private readonly ConcurrentDictionary<string, (MediatRCodeLensResult result, VersionStamp solutionVersion)> _cache
            = new ConcurrentDictionary<string, (MediatRCodeLensResult, VersionStamp)>();

        private readonly ConcurrentDictionary<string, (MediatRCodeLensDetailResult result, VersionStamp solutionVersion)> _detailCache
            = new ConcurrentDictionary<string, (MediatRCodeLensDetailResult, VersionStamp)>();

        public CodeLensCallbackService()
        {
            System.Diagnostics.Debug.WriteLine("MediatRNavigationExtension: CodeLensCallback: Constructor called — listener instantiated by MEF");
        }

        private VisualStudioWorkspace GetWorkspace()
        {
            if (_workspace != null)
                return _workspace;

            try
            {
                System.Diagnostics.Debug.WriteLine("MediatRNavigationExtension: CodeLensCallback: GetWorkspace — acquiring workspace via GlobalProvider");
                var componentModel = Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel;
                _workspace = componentModel?.GetService<VisualStudioWorkspace>();

                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: CodeLensCallback: Workspace acquired: {_workspace != null}");

                if (_workspace != null && !_workspaceChangeSubscribed)
                {
                    _workspace.WorkspaceChanged += OnWorkspaceChanged;
                    _workspaceChangeSubscribed = true;
                    System.Diagnostics.Debug.WriteLine("MediatRNavigationExtension: CodeLensCallback: Subscribed to WorkspaceChanged");
                }

                return _workspace;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: CodeLensCallback: ERROR getting workspace: {ex}");
                return null;
            }
        }

        public bool IsCodeLensEnabled()
        {
            try
            {
                var enabled = MediatRNavigationOptions.Instance.EnableCodeLens;
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: CodeLensCallback: IsCodeLensEnabled => {enabled}");
                return enabled;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: CodeLensCallback: ERROR reading EnableCodeLens option: {ex.Message}");
                return true;
            }
        }

        public async Task<MediatRCodeLensResult> GetMediatRCodeLensData(string filePath, string elementDescription, string kind)
        {
            System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: CodeLensCallback: GetMediatRCodeLensData called — file='{filePath}', element='{elementDescription}', kind='{kind}'");

            if (!IsCodeLensEnabled())
                return new MediatRCodeLensResult { IsMediatRType = false };

            try
            {
                var workspace = GetWorkspace();
                if (workspace?.CurrentSolution == null)
                {
                    System.Diagnostics.Debug.WriteLine("MediatRNavigationExtension: CodeLensCallback: Workspace or CurrentSolution is null");
                    return new MediatRCodeLensResult { IsMediatRType = false };
                }

                var cacheKey = $"{filePath}|{elementDescription}|{kind}";
                var currentVersion = workspace.CurrentSolution.Version;

                if (_cache.TryGetValue(cacheKey, out var cached) && cached.solutionVersion == currentVersion)
                {
                    System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: CodeLensCallback: Cache hit for '{elementDescription}' => IsMediatR={cached.result.IsMediatRType}");
                    return cached.result;
                }

                INamedTypeSymbol typeSymbol;
                bool isMethodKind = string.Equals(kind, "method", StringComparison.OrdinalIgnoreCase);

                if (isMethodKind)
                {
                    typeSymbol = await FindContainingHandlerTypeFromMethodAsync(workspace, filePath, elementDescription);
                }
                else
                {
                    typeSymbol = await FindTypeSymbolAsync(workspace, filePath, elementDescription);
                }

                if (typeSymbol == null)
                {
                    System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: CodeLensCallback: Symbol not found for '{elementDescription}' (kind={kind}) in '{filePath}'");
                    return CacheAndReturn(cacheKey, new MediatRCodeLensResult { IsMediatRType = false }, currentVersion);
                }

                // Reuse result if the same type was already computed from another CodeLens entry
                var typeKey = GetTypeKey(typeSymbol);
                if (_cache.TryGetValue(typeKey, out var typeCached) && typeCached.solutionVersion == currentVersion)
                {
                    System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: CodeLensCallback: Cache hit (type) for '{typeSymbol.ToDisplayString()}'");
                    _cache[cacheKey] = typeCached;
                    return typeCached.result;
                }

                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: CodeLensCallback: Found type symbol '{typeSymbol.ToDisplayString()}' for '{elementDescription}'");

                bool isRequest = MediatRPatternMatcher.IsMediatRRequest(typeSymbol, null);
                bool isHandler = MediatRPatternMatcher.IsMediatRHandler(typeSymbol, null);

                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: CodeLensCallback: '{elementDescription}' — isRequest={isRequest}, isHandler={isHandler}");

                if (!isRequest && !isHandler)
                    return CacheAndReturn(cacheKey, new MediatRCodeLensResult { IsMediatRType = false }, currentVersion);

                var result = new MediatRCodeLensResult { IsMediatRType = true };

                if (isRequest)
                {
                    result.IsRequest = true;
                    var handlers = await MediatRPatternMatcher.FindAllHandlersForTypeSymbol(
                        workspace.CurrentSolution, typeSymbol, null);
                    result.HandlerCount = handlers.Count;

                    var usageFinder = new MediatRUsageFinder(CreateTempWorkspaceService(workspace));
                    var usages = await usageFinder.FindUsagesAsync(typeSymbol);
                    result.UsageCount = usages.Count;

                    result.Description = $"{result.HandlerCount} handler{(result.HandlerCount != 1 ? "s" : "")} | {result.UsageCount} usage{(result.UsageCount != 1 ? "s" : "")}";
                    System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: CodeLensCallback: Request '{elementDescription}' => {result.Description}");
                }
                else if (isHandler)
                {
                    result.IsHandler = true;
                    var handlerInfo = MediatRPatternMatcher.GetHandlerInfo(typeSymbol, null);
                    var requestTypeSymbol = handlerInfo?.RequestTypeSymbol;
                    result.HandledRequestName = handlerInfo?.RequestTypeName ?? "Unknown";

                    if (requestTypeSymbol != null)
                    {
                        var usageFinder = new MediatRUsageFinder(CreateTempWorkspaceService(workspace));
                        var usages = await usageFinder.FindUsagesAsync(requestTypeSymbol);
                        result.UsageCount = usages.Count;
                    }

                    result.Description = $"handles {result.HandledRequestName} | {result.UsageCount} usage{(result.UsageCount != 1 ? "s" : "")}";
                    System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: CodeLensCallback: Handler '{elementDescription}' => {result.Description}");
                }

                _cache[typeKey] = (result, currentVersion);
                return CacheAndReturn(cacheKey, result, currentVersion);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: CodeLensCallback: ERROR in GetMediatRCodeLensData: {ex}");
                return new MediatRCodeLensResult { IsMediatRType = false };
            }
        }

        public async Task<MediatRCodeLensDetailResult> GetMediatRCodeLensDetails(string filePath, string elementDescription, string kind)
        {
            System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: CodeLensCallback: GetMediatRCodeLensDetails called — file='{filePath}', element='{elementDescription}', kind='{kind}'");

            if (!IsCodeLensEnabled())
                return new MediatRCodeLensDetailResult();

            try
            {
                var workspace = GetWorkspace();
                if (workspace?.CurrentSolution == null)
                    return new MediatRCodeLensDetailResult();

                var currentVersion = workspace.CurrentSolution.Version;

                bool isMethodKind = string.Equals(kind, "method", StringComparison.OrdinalIgnoreCase);
                var typeSymbol = isMethodKind
                    ? await FindContainingHandlerTypeFromMethodAsync(workspace, filePath, elementDescription)
                    : await FindTypeSymbolAsync(workspace, filePath, elementDescription);
                if (typeSymbol == null)
                    return new MediatRCodeLensDetailResult();

                var typeKey = GetTypeKey(typeSymbol);
                if (_detailCache.TryGetValue(typeKey, out var cached) && cached.solutionVersion == currentVersion)
                {
                    System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: CodeLensCallback: Detail cache hit for '{typeSymbol.ToDisplayString()}'");
                    return cached.result;
                }

                var detailResult = new MediatRCodeLensDetailResult();
                bool isRequest = MediatRPatternMatcher.IsMediatRRequest(typeSymbol, null);
                bool isHandler = MediatRPatternMatcher.IsMediatRHandler(typeSymbol, null);

                INamedTypeSymbol requestTypeForUsages = null;

                if (isRequest)
                {
                    var handlers = await MediatRPatternMatcher.FindAllHandlersForTypeSymbol(
                        workspace.CurrentSolution, typeSymbol, null);

                    foreach (var handler in handlers)
                    {
                        var loc = handler.Location;
                        var lineSpan = loc?.GetLineSpan();

                        detailResult.Entries.Add(new CodeLensDetailEntry
                        {
                            Category = "Handler",
                            TypeName = handler.HandlerTypeName,
                            FilePath = loc?.SourceTree?.FilePath ?? "",
                            Line = (lineSpan?.StartLinePosition.Line ?? 0) + 1,
                            Column = lineSpan?.StartLinePosition.Character ?? 0,
                            Context = $"{handler.HandlerType}: {handler.HandlerTypeName}"
                        });
                    }

                    requestTypeForUsages = typeSymbol;
                }
                else if (isHandler)
                {
                    var handlerInfo = MediatRPatternMatcher.GetHandlerInfo(typeSymbol, null);
                    requestTypeForUsages = handlerInfo?.RequestTypeSymbol;
                }

                if (requestTypeForUsages != null)
                {
                    var usageFinder = new MediatRUsageFinder(CreateTempWorkspaceService(workspace));
                    var usages = await usageFinder.FindUsagesAsync(requestTypeForUsages);

                    foreach (var usage in usages)
                    {
                        detailResult.Entries.Add(new CodeLensDetailEntry
                        {
                            Category = "Usage",
                            TypeName = usage.RequestTypeName,
                            FilePath = usage.FilePath,
                            Line = usage.LineNumber,
                            Column = 0,
                            Context = usage.ContextDescription
                        });
                    }
                }

                _detailCache[typeKey] = (detailResult, currentVersion);

                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: CodeLensCallback: GetMediatRCodeLensDetails returning {detailResult.Entries.Count} entries for '{elementDescription}'");
                return detailResult;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: CodeLensCallback: ERROR in GetMediatRCodeLensDetails: {ex}");
                return new MediatRCodeLensDetailResult();
            }
        }

        private async Task<INamedTypeSymbol> FindTypeSymbolAsync(VisualStudioWorkspace workspace, string filePath, string elementDescription)
        {
            var documentIds = workspace.CurrentSolution.GetDocumentIdsWithFilePath(filePath);
            System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: CodeLensCallback: FindTypeSymbol — found {documentIds.Length} document(s) for '{filePath}'");

            foreach (var docId in documentIds)
            {
                var document = workspace.CurrentSolution.GetDocument(docId);
                if (document == null) continue;

                var semanticModel = await document.GetSemanticModelAsync();
                if (semanticModel == null) continue;

                var syntaxTree = await document.GetSyntaxTreeAsync();
                if (syntaxTree == null) continue;

                var root = await syntaxTree.GetRootAsync();
                var typeDeclarations = root.DescendantNodes().OfType<TypeDeclarationSyntax>();

                foreach (var typeDecl in typeDeclarations)
                {
                    var symbol = semanticModel.GetDeclaredSymbol(typeDecl) as INamedTypeSymbol;
                    if (symbol == null) continue;

                    var displayString = symbol.ToDisplayString();
                    var metadataName = symbol.ContainingNamespace != null && !symbol.ContainingNamespace.IsGlobalNamespace
                        ? $"{symbol.ContainingNamespace.ToDisplayString()}.{symbol.Name}"
                        : symbol.Name;

                    System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: CodeLensCallback: FindTypeSymbol — comparing element='{elementDescription}' vs Name='{symbol.Name}', Display='{displayString}', Metadata='{metadataName}'");

                    if (symbol.Name == elementDescription ||
                        displayString == elementDescription ||
                        metadataName == elementDescription ||
                        symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) == elementDescription ||
                        symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == elementDescription ||
                        symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "") == elementDescription)
                    {
                        System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: CodeLensCallback: FindTypeSymbol — MATCHED '{elementDescription}' => '{displayString}'");
                        return symbol;
                    }
                }
            }

            System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: CodeLensCallback: FindTypeSymbol — no match for '{elementDescription}' in '{filePath}'");
            return null;
        }

        private static readonly string[] HandlerMethodNames = { "Handle", "Execute" };

        private async Task<INamedTypeSymbol> FindContainingHandlerTypeFromMethodAsync(
            VisualStudioWorkspace workspace, string filePath, string elementDescription)
        {
            System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: CodeLensCallback: FindContainingHandlerTypeFromMethod — element='{elementDescription}'");

            var documentIds = workspace.CurrentSolution.GetDocumentIdsWithFilePath(filePath);
            foreach (var docId in documentIds)
            {
                var document = workspace.CurrentSolution.GetDocument(docId);
                if (document == null) continue;

                var semanticModel = await document.GetSemanticModelAsync();
                if (semanticModel == null) continue;

                var syntaxTree = await document.GetSyntaxTreeAsync();
                if (syntaxTree == null) continue;

                var root = await syntaxTree.GetRootAsync();

                // Type-first approach: find handler types, then match methods inside them
                foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
                {
                    var typeSymbol = semanticModel.GetDeclaredSymbol(typeDecl) as INamedTypeSymbol;
                    if (typeSymbol == null) continue;

                    if (!MediatRPatternMatcher.IsMediatRHandler(typeSymbol, null))
                        continue;

                    foreach (var methodDecl in typeDecl.Members.OfType<MethodDeclarationSyntax>())
                    {
                        var methodName = methodDecl.Identifier.ValueText;
                        if (!HandlerMethodNames.Contains(methodName))
                            continue;

                        var methodSymbol = semanticModel.GetDeclaredSymbol(methodDecl) as IMethodSymbol;
                        if (methodSymbol == null) continue;

                        if (MethodMatchesDescription(methodSymbol, elementDescription))
                        {
                            System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: CodeLensCallback: FindContainingHandlerTypeFromMethod — MATCHED method '{elementDescription}' => handler type '{typeSymbol.ToDisplayString()}'");
                            return typeSymbol;
                        }
                    }
                }
            }

            System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: CodeLensCallback: FindContainingHandlerTypeFromMethod — no handler match for '{elementDescription}'");
            return null;
        }

        private static bool MethodMatchesDescription(IMethodSymbol methodSymbol, string elementDescription)
        {
            if (methodSymbol.Name == elementDescription)
                return true;

            var methodDisplay = methodSymbol.ToDisplayString();
            if (methodDisplay == elementDescription)
                return true;

            var methodMetadata = methodSymbol.ContainingType != null
                ? $"{methodSymbol.ContainingType.ToDisplayString()}.{methodSymbol.Name}"
                : methodSymbol.Name;
            if (methodMetadata == elementDescription)
                return true;

            if (elementDescription.Contains($".{methodSymbol.Name}("))
                return true;

            if (elementDescription.EndsWith($".{methodSymbol.Name}"))
                return true;

            return false;
        }

        private static string GetTypeKey(INamedTypeSymbol typeSymbol)
        {
            return $"type:{typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}";
        }

        private WorkspaceService CreateTempWorkspaceService(VisualStudioWorkspace workspace)
        {
            var service = new WorkspaceService();
            service.SetWorkspace(workspace);
            return service;
        }

        private MediatRCodeLensResult CacheAndReturn(string key, MediatRCodeLensResult result, VersionStamp version)
        {
            _cache[key] = (result, version);
            return result;
        }

        private void OnWorkspaceChanged(object sender, WorkspaceChangeEventArgs e)
        {
            lock (_lock)
            {
                _refreshCts?.Cancel();
                _refreshCts = new CancellationTokenSource();
            }

            var token = _refreshCts.Token;
            int delaySeconds;
            try
            {
                delaySeconds = MediatRNavigationOptions.Instance.CodeLensRefreshDelaySeconds;
            }
            catch
            {
                delaySeconds = 3;
            }

            _ = Task.Delay(TimeSpan.FromSeconds(delaySeconds), token)
                .ContinueWith(_ =>
                {
                    _cache.Clear();
                    _detailCache.Clear();
                }, CancellationToken.None, TaskContinuationOptions.NotOnCanceled, TaskScheduler.Default);
        }

        public void Dispose()
        {
            if (_workspace != null && _workspaceChangeSubscribed)
            {
                _workspace.WorkspaceChanged -= OnWorkspaceChanged;
                _workspaceChangeSubscribed = false;
            }

            lock (_lock)
            {
                _refreshCts?.Cancel();
                _refreshCts?.Dispose();
            }
        }
    }
}
