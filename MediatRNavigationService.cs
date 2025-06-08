using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;

namespace VSIXExtention
{
    public class HandlerDisplayInfo
    {
        public MediatRPatternMatcher.MediatRHandlerInfo Handler { get; set; }
        public string DisplayText { get; set; }
    }

    public class MediatRNavigationService : IDisposable
    {
        private readonly IServiceProvider _serviceProvider;
        private VisualStudioWorkspace _cachedWorkspace;
        private readonly object _workspaceLock = new object();
        
        // Cache compilation results to avoid repeated expensive operations
        private readonly ConcurrentDictionary<ProjectId, Compilation> _compilationCache = new ConcurrentDictionary<ProjectId, Compilation>();
        
        // Track solution version to detect changes
        private int _lastSolutionVersion = -1;
        private bool _workspaceEventsSubscribed = false;
        private bool _disposed = false;
        
        // Document save event handling
        internal IVsRunningDocumentTable _runningDocumentTable;
        private uint _rdtCookie;
        private bool _rdtEventsSubscribed = false;
        
        // Cache manager - separated concern
        private MediatRCacheManager _cacheManager;

        public MediatRNavigationService(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
            _cacheManager = new MediatRCacheManager();
        }

        public async Task<bool> TryNavigateToHandlerAsync(INamedTypeSymbol requestTypeSymbol)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var workspace = GetOrCreateWorkspace();
                if (workspace?.CurrentSolution == null)
                {
                    return false;
                }

                // Load persistent cache if not already loaded
                await EnsureCacheLoadedAsync(workspace.CurrentSolution);

                var requestInfo = MediatRPatternMatcher.GetRequestInfo(requestTypeSymbol, null);
                if (requestInfo == null)
                {
                    return false;
                }

                var handlers = await FindHandlersWithCaching(workspace.CurrentSolution, requestInfo);

                return await NavigateToHandlers(handlers, requestInfo.IsNotification);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatR Navigation: Error navigating to handler: {ex.Message}");
                return false;
            }
        }

        private VisualStudioWorkspace GetOrCreateWorkspace()
        {
            // Thread-safe lazy workspace initialization with caching
            if (_cachedWorkspace != null)
                return _cachedWorkspace;

            lock (_workspaceLock)
            {
                if (_cachedWorkspace != null)
                    return _cachedWorkspace;

                _cachedWorkspace = GetVisualStudioWorkspace();
                
                // Subscribe to workspace events for cache invalidation
                if (_cachedWorkspace != null && !_workspaceEventsSubscribed)
                {
                    SubscribeToWorkspaceEvents(_cachedWorkspace);
                    _workspaceEventsSubscribed = true;
                }
                
                return _cachedWorkspace;
            }
        }

        private VisualStudioWorkspace GetVisualStudioWorkspace()
        {
            // Try methods in order of likelihood to succeed
            
            // Method 1: Through global service provider (most reliable)
            var workspace = Package.GetGlobalService(typeof(VisualStudioWorkspace)) as VisualStudioWorkspace;
            if (workspace != null)
                return workspace;

            // Method 2: Through service provider
            workspace = _serviceProvider.GetService(typeof(VisualStudioWorkspace)) as VisualStudioWorkspace;
            if (workspace != null)
                return workspace;

            // Method 3: Through component model (fallback)
            try
            {
                var componentModel = _serviceProvider.GetService(typeof(SComponentModel)) as IComponentModel;
                return componentModel?.GetService<VisualStudioWorkspace>();
            }
            catch
            {
                return null;
            }
        }

        private void SubscribeToWorkspaceEvents(VisualStudioWorkspace workspace)
        {
            try
            {
                // Subscribe to workspace change events - more selective approach
                workspace.WorkspaceChanged += OnWorkspaceChanged;
                
                // Subscribe to document save events via Running Document Table
                SubscribeToDocumentSaveEvents();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error subscribing to workspace events: {ex.Message}");
            }
        }

        private void SubscribeToDocumentSaveEvents()
        {
            try
            {
                _runningDocumentTable = _serviceProvider.GetService(typeof(SVsRunningDocumentTable)) as IVsRunningDocumentTable;
                if (_runningDocumentTable != null)
                {
                    var hr = _runningDocumentTable.AdviseRunningDocTableEvents(new DocumentSaveEventHandler(this), out _rdtCookie);
                    if (hr == VSConstants.S_OK)
                    {
                        _rdtEventsSubscribed = true;
                        System.Diagnostics.Debug.WriteLine("MediatR Extension: Subscribed to document save events");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error subscribing to document save events: {ex.Message}");
            }
        }

        private void OnWorkspaceChanged(object sender, WorkspaceChangeEventArgs e)
        {
            try
            {
                // Only invalidate cache on truly structural changes that affect MediatR handlers
                switch (e.Kind)
                {
                    case WorkspaceChangeKind.SolutionAdded:
                        // Only clear cache for actual solution structure changes
                        System.Diagnostics.Debug.WriteLine($"MediatR Extension: Solution structure changed ({e.Kind}), clearing cache");
                        _cacheManager.ClearCache();
                        break;
                        
                    case WorkspaceChangeKind.SolutionCleared:
                    case WorkspaceChangeKind.SolutionRemoved:
                    case WorkspaceChangeKind.SolutionReloaded:
                        // Solution is closing - save cache but don't clear it (we want to preserve it)
                        System.Diagnostics.Debug.WriteLine("MediatR Extension: Solution closing, saving cache");
                        _ = Task.Run(async () =>
                        {
                            await _cacheManager.SaveCacheAsync();
                            System.Diagnostics.Debug.WriteLine("MediatR Extension: Cache saved on solution close");
                        });
                        break;
                        
                    case WorkspaceChangeKind.ProjectAdded:
                    case WorkspaceChangeKind.ProjectRemoved:
                        System.Diagnostics.Debug.WriteLine($"MediatR Extension: Project structure changed ({e.Kind}), clearing cache");
                        _cacheManager.ClearCache();
                        break;
                        
                    case WorkspaceChangeKind.DocumentAdded:
                    case WorkspaceChangeKind.DocumentRemoved:
                        if (IsRelevantDocument(e))
                        {
                            System.Diagnostics.Debug.WriteLine($"MediatR Extension: Relevant document {e.Kind}, clearing cache");
                            _cacheManager.ClearCache();
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error handling workspace change: {ex.Message}");
            }
        }

        private bool IsRelevantDocument(WorkspaceChangeEventArgs e)
        {
            try
            {
                var document = e.NewSolution?.GetDocument(e.DocumentId) ?? e.OldSolution?.GetDocument(e.DocumentId);
                if (document?.FilePath == null) return false;
                
                return CouldContainMediatRPatterns(document.FilePath);
            }
            catch
            {
                return false;
            }
        }

        internal async Task OnDocumentSaved(string filePath)
        {
            try
            {
                if (!CouldContainMediatRPatterns(filePath)) return;
                
                System.Diagnostics.Debug.WriteLine($"MediatR Extension: Document saved: {filePath}");
                await UpdateCacheForSavedDocument(filePath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error handling document save: {ex.Message}");
            }
        }

        private bool CouldContainMediatRPatterns(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return false;
            
            // Only scan C# files
            if (!filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return false;
            
            // Quick heuristic based on file name - likely to contain handlers/requests
            var fileName = System.IO.Path.GetFileName(filePath).ToLowerInvariant();
            return fileName.Contains("handler") || 
                   fileName.Contains("request") || 
                   fileName.Contains("command") || 
                   fileName.Contains("query") || 
                   fileName.Contains("notification");
        }

        private async Task UpdateCacheForSavedDocument(string filePath)
        {
            try
            {
                var workspace = GetOrCreateWorkspace();
                if (workspace?.CurrentSolution == null) return;

                var documentIds = workspace.CurrentSolution.GetDocumentIdsWithFilePath(filePath);
                if (!documentIds.Any()) return;

                var document = workspace.CurrentSolution.GetDocument(documentIds.First());
                if (document == null) return;

                var newHandlers = await ScanDocumentForHandlers(document);
                if (newHandlers.Any())
                {
                    _cacheManager.UpdateCacheWithHandlers(newHandlers);
                    System.Diagnostics.Debug.WriteLine($"MediatR Extension: Updated cache with {newHandlers.Count} handlers from {filePath}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error updating cache for saved document: {ex.Message}");
            }
        }

        private async Task<List<MediatRPatternMatcher.MediatRHandlerInfo>> ScanDocumentForHandlers(Document document)
        {
            var handlers = new List<MediatRPatternMatcher.MediatRHandlerInfo>();
            
            try
            {
                var compilation = await GetOrCreateCompilationAsync(document.Project);
                if (compilation == null) return handlers;

                var syntaxTree = await document.GetSyntaxTreeAsync();
                if (syntaxTree == null) return handlers;

                // Scan for both request handlers and notification handlers
                var requestHandlers = await ProcessSyntaxTreeForHandlers(compilation, syntaxTree, "", false);
                var notificationHandlers = await ProcessSyntaxTreeForHandlers(compilation, syntaxTree, "", true);
                
                handlers.AddRange(requestHandlers);
                handlers.AddRange(notificationHandlers);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error scanning document for handlers: {ex.Message}");
            }
            
            return handlers;
        }

        private async Task<List<MediatRPatternMatcher.MediatRHandlerInfo>> FindHandlersWithCaching(
            Solution solution, 
            MediatRPatternMatcher.MediatRRequestInfo requestInfo)
        {
            var requestTypeName = requestInfo.RequestTypeName;
            
            System.Diagnostics.Debug.WriteLine($"MediatR Extension: Looking for handlers for {requestTypeName} (IsNotification: {requestInfo.IsNotification})");
            
            // Try to get from cache first
            var cachedHandlers = _cacheManager.GetCachedHandlers(requestTypeName);
            if (cachedHandlers != null)
            {
                System.Diagnostics.Debug.WriteLine($"MediatR Extension: Found {cachedHandlers.Count} cached handlers for {requestTypeName}");
                return cachedHandlers;
            }
            
            System.Diagnostics.Debug.WriteLine($"MediatR Extension: No cached handlers found, scanning solution for {requestTypeName}");
            
            // Not in cache, find them and cache the result
            var handlers = await FindHandlersOptimized(solution, requestInfo);
            
            _cacheManager.CacheHandlers(requestTypeName, handlers);
            
            System.Diagnostics.Debug.WriteLine($"MediatR Extension: Found and cached {handlers.Count} handlers for {requestTypeName}");
            return handlers;
        }



        private async Task<List<MediatRPatternMatcher.MediatRHandlerInfo>> FindHandlersOptimized(
            Solution solution, 
            MediatRPatternMatcher.MediatRRequestInfo requestInfo)
        {
            var handlers = new List<MediatRPatternMatcher.MediatRHandlerInfo>();

            try
            {
                foreach (var project in solution.Projects)
                {
                    // Skip test projects for better performance
                    if (IsTestProject(project.Name)) continue;

                    var compilation = await GetOrCreateCompilationAsync(project);
                    if (compilation == null) continue;

                    List<MediatRPatternMatcher.MediatRHandlerInfo> projectHandlers;
                    
                    if (requestInfo.IsNotification)
                    {
                        projectHandlers = await FindNotificationHandlersInProjectOptimized(compilation, requestInfo.RequestTypeName);
                    }
                    else
                    {
                        projectHandlers = await FindHandlersInProjectOptimized(compilation, requestInfo.RequestTypeName);
                    }
                    
                    handlers.AddRange(projectHandlers);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error finding handlers: {ex.Message}");
            }

            return handlers;
        }

        private async Task<Compilation> GetOrCreateCompilationAsync(Project project)
        {
            if (_compilationCache.TryGetValue(project.Id, out var cachedCompilation))
                return cachedCompilation;

            var compilation = await project.GetCompilationAsync();
            if (compilation != null)
            {
                _compilationCache.TryAdd(project.Id, compilation);
            }
            return compilation;
        }

        private static bool IsTestProject(string projectName)
        {
            var lowerName = projectName.ToLowerInvariant();
            return lowerName.Contains("test") || lowerName.Contains("spec") || lowerName.Contains("unit");
        }

        private async Task<List<MediatRPatternMatcher.MediatRHandlerInfo>> FindHandlersInProjectOptimized(
            Compilation compilation, 
            string requestTypeName)
        {
            var handlers = new List<MediatRPatternMatcher.MediatRHandlerInfo>();

            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                var treeHandlers = await ProcessSyntaxTreeForHandlers(compilation, syntaxTree, requestTypeName, false);
                handlers.AddRange(treeHandlers);
            }

            return handlers;
        }

        private async Task<List<MediatRPatternMatcher.MediatRHandlerInfo>> FindNotificationHandlersInProjectOptimized(
            Compilation compilation, 
            string notificationTypeName)
        {
            var handlers = new List<MediatRPatternMatcher.MediatRHandlerInfo>();

            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                var treeHandlers = await ProcessSyntaxTreeForHandlers(compilation, syntaxTree, notificationTypeName, true);
                handlers.AddRange(treeHandlers);
            }

            return handlers;
        }

        private async Task<List<MediatRPatternMatcher.MediatRHandlerInfo>> ProcessSyntaxTreeForHandlers(
            Compilation compilation,
            SyntaxTree syntaxTree,
            string targetTypeName,
            bool isNotificationHandler)
        {
            var handlers = new List<MediatRPatternMatcher.MediatRHandlerInfo>();

            try
            {
                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = await syntaxTree.GetRootAsync();

                var classDeclarations = root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>().ToList();
                System.Diagnostics.Debug.WriteLine($"MediatR Extension: Processing {syntaxTree.FilePath} - found {classDeclarations.Count} classes");

                foreach (var classDeclaration in classDeclarations)
                {
                    var classSymbol = semanticModel.GetDeclaredSymbol(classDeclaration) as INamedTypeSymbol;
                    if (classSymbol == null) continue;

                    var handlerInfo = MediatRPatternMatcher.GetHandlerInfo(classSymbol, semanticModel);
                    if (handlerInfo != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"MediatR Extension: Found handler {handlerInfo.HandlerTypeName} for {handlerInfo.RequestTypeName} (IsNotification: {handlerInfo.IsNotificationHandler})");
                        
                        if (handlerInfo.IsNotificationHandler == isNotificationHandler)
                        {
                            // If we're scanning for a specific type, filter by it
                            // If targetTypeName is empty, we want all handlers of this type
                            if (!string.IsNullOrEmpty(targetTypeName) && 
                                !handlerInfo.RequestTypeName.Equals(targetTypeName, StringComparison.OrdinalIgnoreCase))
                            {
                                System.Diagnostics.Debug.WriteLine($"MediatR Extension: Skipping handler {handlerInfo.HandlerTypeName} - looking for {targetTypeName}, found {handlerInfo.RequestTypeName}");
                                continue;
                            }

                            handlerInfo.Location = classDeclaration.GetLocation();
                            handlers.Add(handlerInfo);
                            System.Diagnostics.Debug.WriteLine($"MediatR Extension: Added handler {handlerInfo.HandlerTypeName} for {handlerInfo.RequestTypeName}");
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"MediatR Extension: Skipping handler {handlerInfo.HandlerTypeName} - wrong type (looking for notification: {isNotificationHandler}, found: {handlerInfo.IsNotificationHandler})");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error processing syntax tree {syntaxTree.FilePath}: {ex.Message}");
            }

            System.Diagnostics.Debug.WriteLine($"MediatR Extension: Found {handlers.Count} matching handlers in {syntaxTree.FilePath}");
            return handlers;
        }

        private async Task<bool> NavigateToHandlers(
            List<MediatRPatternMatcher.MediatRHandlerInfo> handlers, 
            bool isNotification)
        {
            if (!handlers.Any())
            {
                System.Diagnostics.Debug.WriteLine("No MediatR handlers found.");
                return false;
            }

            if (handlers.Count == 1)
            {
                return await NavigateToLocationAsync(handlers[0].Location);
            }

            var result = await NavigateToMultipleHandlersAsync(handlers, isNotification);
            
            // Handle cancellation vs failure differently
            if (result == null) // Cancellation
            {
                return true; // Don't show error for cancellation
            }
            
            if (result == false) // Failure
            {
                System.Diagnostics.Debug.WriteLine("No MediatR handlers found.");
            }
            
            return result ?? false;
        }

        private async Task<bool?> NavigateToMultipleHandlersAsync(
            List<MediatRPatternMatcher.MediatRHandlerInfo> handlers, 
            bool isNotification)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var handlerDisplayInfo = handlers.Select(h => new HandlerDisplayInfo
                {
                    Handler = h,
                    DisplayText = FormatHandlerDisplayText(h)
                }).ToArray();

                var handlerNames = handlerDisplayInfo.Select(hdi => hdi.DisplayText).ToArray();
                string handlerType = isNotification ? "notification handler" : "handler";
                string message = $"Multiple {handlerType}s found. Please select one:";

                var selectedHandlerName = ShowHandlerSelectionDialog(handlerDisplayInfo, isNotification);
                
                if (selectedHandlerName == null)
                {
                    return null; // User cancelled
                }

                var selectedHandler = handlerDisplayInfo.FirstOrDefault(hdi => hdi.DisplayText == selectedHandlerName)?.Handler;
                if (selectedHandler != null)
                {
                    return await NavigateToLocationAsync(selectedHandler.Location);
                }

                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in NavigateToMultipleHandlersAsync: {ex.Message}");
                return false;
            }
        }

        private string FormatHandlerDisplayText(MediatRPatternMatcher.MediatRHandlerInfo handler)
        {
            try
            {
                var filePath = handler.Location?.SourceTree?.FilePath;
                if (string.IsNullOrEmpty(filePath))
                {
                    return handler.HandlerTypeName;
                }

                // Extract last 2-3 folders for context
                var pathParts = filePath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (pathParts.Length <= 2)
                {
                    return $"{handler.HandlerTypeName} ({string.Join("/", pathParts)})";
                }

                var relevantParts = pathParts.Skip(Math.Max(0, pathParts.Length - 3)).ToArray();
                return $"{handler.HandlerTypeName} ({string.Join("/", relevantParts)})";
            }
            catch
            {
                return handler.HandlerTypeName;
            }
        }

        private string ShowHandlerSelectionDialog(HandlerDisplayInfo[] handlerDisplayInfo, bool isNotification)
        {
            string handlerType = isNotification ? "notification handler" : "handler";
            string message = $"Multiple {handlerType}s found. Please select one:";
            
            var handlerNames = handlerDisplayInfo.Select(hdi => hdi.DisplayText).ToArray();
            var dialog = new HandlerSelectionDialog(message, handlerNames);
            
            // Use ShowModal() for DialogWindow
            var result = dialog.ShowModal();
            if (result != true)
            {
                return null; // User cancelled or dialog failed
            }
            
            return dialog.SelectedHandler;
        }

        private async Task<bool> NavigateToLocationAsync(Location location)
        {
            if (location?.SourceTree?.FilePath == null)
            {
                System.Diagnostics.Debug.WriteLine("MediatR Navigation: Location or FilePath is null");
                return false;
            }

            var lineSpan = location.GetLineSpan();
            System.Diagnostics.Debug.WriteLine($"MediatR Navigation: Navigating to {location.SourceTree.FilePath} at line {lineSpan.StartLinePosition.Line + 1}");
            var result = await OpenDocumentAndNavigate(location.SourceTree.FilePath, lineSpan.StartLinePosition);
            System.Diagnostics.Debug.WriteLine($"MediatR Navigation: Navigation result: {result}");
            return result;
        }

        private async Task<bool> OpenDocumentAndNavigate(string filePath, Microsoft.CodeAnalysis.Text.LinePosition linePosition)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                
                System.Diagnostics.Debug.WriteLine($"MediatR Navigation: Attempting to open file: {filePath} at line {linePosition.Line + 1}");

                var dte = _serviceProvider.GetService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                if (dte?.ItemOperations == null)
                {
                    System.Diagnostics.Debug.WriteLine("MediatR Navigation: Failed to get DTE or ItemOperations");
                    return false;
                }

                var window = dte.ItemOperations.OpenFile(filePath);
                if (window?.Document?.Object("TextDocument") is EnvDTE.TextDocument textDocument)
                {
                    var selection = textDocument.Selection;
                    selection.MoveToLineAndOffset(linePosition.Line + 1, linePosition.Character + 1, false);
                    System.Diagnostics.Debug.WriteLine($"MediatR Navigation: Successfully navigated to {filePath} at line {linePosition.Line + 1}");
                    return true;
                }
                
                // Alternative approach if the above fails
                if (window?.Document != null)
                {
                    System.Diagnostics.Debug.WriteLine($"MediatR Navigation: Trying alternative approach for {filePath}");
                    var document = window.Document;
                    var altTextDocument = document.Object("TextDocument") as EnvDTE.TextDocument;
                    if (altTextDocument != null)
                    {
                        var selection = altTextDocument.Selection;
                        selection.MoveToLineAndOffset(linePosition.Line + 1, linePosition.Character + 1, false);
                        System.Diagnostics.Debug.WriteLine($"MediatR Navigation: Successfully navigated to {filePath} at line {linePosition.Line + 1} (alternative approach)");
                        return true;
                    }
                }

                System.Diagnostics.Debug.WriteLine($"MediatR Navigation: Failed to get TextDocument from window for {filePath}");
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatR Navigation: Error opening document {filePath}: {ex.Message}");
                return false;
            }
        }

        public void ClearCache()
        {
            _compilationCache.Clear();
            _cacheManager.ClearCache();
            System.Diagnostics.Debug.WriteLine("MediatR Extension: Caches cleared");
        }

        public async Task SaveCacheAsync()
        {
            await _cacheManager.SaveCacheAsync();
        }

        private async Task EnsureCacheLoadedAsync(Solution solution)
        {
            if (!_cacheManager.IsCacheLoaded)
            {
                await _cacheManager.InitializeAsync(solution);
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                try
                {
                    // Dispose cache manager first
                    _cacheManager?.Dispose();

                    // Unsubscribe from workspace events
                    if (_cachedWorkspace != null && _workspaceEventsSubscribed)
                    {
                        _cachedWorkspace.WorkspaceChanged -= OnWorkspaceChanged;
                    }
                    
                    // Unsubscribe from document save events
                    if (_runningDocumentTable != null && _rdtEventsSubscribed)
                    {
                        _runningDocumentTable.UnadviseRunningDocTableEvents(_rdtCookie);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error during MediatRNavigationService disposal: {ex.Message}");
                }
                finally
                {
                    _disposed = true;
                    _compilationCache.Clear();
                }
            }
        }
    }

    internal class DocumentSaveEventHandler : IVsRunningDocTableEvents
    {
        private readonly MediatRNavigationService _service;

        public DocumentSaveEventHandler(MediatRNavigationService service)
        {
            _service = service;
        }

        public int OnAfterFirstDocumentLock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining)
        {
            return VSConstants.S_OK;
        }

        public int OnBeforeLastDocumentUnlock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining)
        {
            return VSConstants.S_OK;
        }

        public int OnAfterSave(uint docCookie)
        {
            try
            {
                var runningDocTable = _service._runningDocumentTable;
                if (runningDocTable != null)
                {
                    runningDocTable.GetDocumentInfo(docCookie, out _, out _, out _, out var docPath, out _, out _, out _);
                    if (!string.IsNullOrEmpty(docPath))
                    {
                        _service.OnDocumentSaved(docPath).RunSynchronously();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in OnAfterSave: {ex.Message}");
            }
            
            return VSConstants.S_OK;
        }

        public int OnAfterAttributeChange(uint docCookie, uint grfAttribs)
        {
            return VSConstants.S_OK;
        }

        public int OnBeforeDocumentWindowShow(uint docCookie, int fFirstShow, IVsWindowFrame pFrame)
        {
            return VSConstants.S_OK;
        }

        public int OnAfterDocumentWindowHide(uint docCookie, IVsWindowFrame pFrame)
        {
            return VSConstants.S_OK;
        }
    }


} 