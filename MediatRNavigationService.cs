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
    public class MediatRNavigationService : IDisposable
    {
        private readonly IServiceProvider _serviceProvider;
        private VisualStudioWorkspace _cachedWorkspace;
        private readonly object _workspaceLock = new object();
        
        // Cache compilation results to avoid repeated expensive operations
        private readonly ConcurrentDictionary<ProjectId, Compilation> _compilationCache = new ConcurrentDictionary<ProjectId, Compilation>();
        
        // Track solution version to detect changes
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
            _cacheManager = new MediatRCacheManager(this);
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

        private async void OnWorkspaceChanged(object sender, WorkspaceChangeEventArgs e)
        {
            try
            {
                // Only invalidate cache on truly structural changes that affect MediatR handlers
                switch (e.Kind)
                {
                    case WorkspaceChangeKind.SolutionAdded:
                        System.Diagnostics.Debug.WriteLine($"MediatR Extension: New solution loaded, reinitializing cache");
                        await ReinitializeCacheForNewSolution(e.NewSolution);
                        break;
                        
                    case WorkspaceChangeKind.SolutionCleared:
                    case WorkspaceChangeKind.SolutionRemoved:
                    case WorkspaceChangeKind.SolutionReloaded:
                        System.Diagnostics.Debug.WriteLine("MediatR Extension: Solution closing, saving cache");
                        _ = Task.Run(async () =>
                        {
                            await _cacheManager.SaveCacheAsync();
                            System.Diagnostics.Debug.WriteLine("MediatR Extension: Cache saved on solution close");
                        });
                        break;
                        
                    // Removed ProjectAdded/ProjectRemoved events - they're unreliable during solution closing
                    // and cause unnecessary cache clearing. Document save events handle cache updates properly.
                        
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

        private async Task ReinitializeCacheForNewSolution(Solution newSolution)
        {
            try
            {
                if (newSolution?.FilePath == null)
                {
                    System.Diagnostics.Debug.WriteLine("MediatR Extension: No solution file path, skipping cache reinitialization");
                    return;
                }

                // Dispose the old cache manager
                _cacheManager?.Dispose();
                
                // Create a new cache manager for the new solution
                _cacheManager = new MediatRCacheManager(this);
                
                // Initialize it with the new solution
                await _cacheManager.InitializeAsync(newSolution);
                
                // Clear compilation cache since we're in a new solution
                _compilationCache.Clear();
                
                System.Diagnostics.Debug.WriteLine($"MediatR Extension: Cache reinitialized for new solution: {newSolution.FilePath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatR Extension: Error reinitializing cache for new solution: {ex.Message}");
                
                // Fallback: create a basic cache manager if reinitialization fails
                try
                {
                    _cacheManager?.Dispose();
                    _cacheManager = new MediatRCacheManager(this);
                }
                catch (Exception fallbackEx)
                {
                    System.Diagnostics.Debug.WriteLine($"MediatR Extension: Error in fallback cache creation: {fallbackEx.Message}");
                }
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
            
            // Accept all C# files - the file name heuristic was too restrictive
            // Users might name their handlers/requests differently (e.g., UserService.cs, Controllers/UserController.cs, etc.)
            return true;
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

                // Clear compilation cache for this project to get fresh data
                if (_compilationCache.ContainsKey(document.Project.Id))
                {
                    _compilationCache.TryRemove(document.Project.Id, out _);
                    System.Diagnostics.Debug.WriteLine($"MediatR Extension: Cleared compilation cache for project {document.Project.Name}");
                }

                var newHandlers = await ScanDocumentForHandlers(document);
                if (newHandlers.Any())
                {
                    _cacheManager.UpdateCacheWithHandlers(newHandlers);
                    System.Diagnostics.Debug.WriteLine($"MediatR Extension: Updated cache with {newHandlers.Count} handlers from {filePath}");
                }

                // Always clear cache for recently used request types to ensure fresh scans
                // This handles the case where handlers were added, modified, or deleted
                var recentRequestTypes = _cacheManager.GetRecentlyUsedRequestTypes();
                foreach (var requestType in recentRequestTypes)
                {
                    _cacheManager.InvalidateHandlersForRequestType(requestType);
                }
                
                if (recentRequestTypes.Any())
                {
                    System.Diagnostics.Debug.WriteLine($"MediatR Extension: Invalidated cache for {recentRequestTypes.Count} recent request types: [{string.Join(", ", recentRequestTypes)}]");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error updating cache for saved document: {ex.Message}");
            }
        }

        public async Task<List<MediatRPatternMatcher.MediatRHandlerInfo>> ScanDocumentForHandlers(Document document)
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
                var success = await NavigateToLocationAsync(handlers[0].Location);
                if (!success)
                {
                    // Try to recover by performing a fresh search
                    return await TryRecoverFromFailedNavigation(handlers[0], isNotification);
                }
                return success;
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
                    var success = await NavigateToLocationAsync(selectedHandler.Location);
                    if (!success)
                    {
                        // Try to recover by performing a fresh search
                        return await TryRecoverFromFailedNavigation(selectedHandler, isNotification);
                    }
                    return success;
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

            var filePath = location.SourceTree.FilePath;
            var lineSpan = location.GetLineSpan();
            
            System.Diagnostics.Debug.WriteLine($"MediatR Navigation: Navigating to {filePath} at line {lineSpan.StartLinePosition.Line + 1}");
            
            // Check if file exists first
            if (!System.IO.File.Exists(filePath))
            {
                System.Diagnostics.Debug.WriteLine($"MediatR Navigation: File not found: {filePath}");
                return false;
            }
            
            var result = await OpenDocumentAndNavigate(filePath, lineSpan.StartLinePosition);
            System.Diagnostics.Debug.WriteLine($"MediatR Navigation: Navigation result: {result}");
            return result;
        }

        private async Task<bool> TryRecoverFromFailedNavigation(MediatRPatternMatcher.MediatRHandlerInfo failedHandler, bool isNotification)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"MediatR Navigation: Attempting recovery for {failedHandler.HandlerTypeName} (RequestType: {failedHandler.RequestTypeName})");

                var workspace = GetOrCreateWorkspace();
                if (workspace?.CurrentSolution == null)
                {
                    await ShowRecoveryFailedMessageAsync(failedHandler.HandlerTypeName, "No workspace available");
                    return false;
                }

                // Create a mock request info for the failed handler
                var requestInfo = new MediatRPatternMatcher.MediatRRequestInfo
                {
                    RequestTypeName = failedHandler.RequestTypeName,
                    IsNotification = isNotification
                };

                // Perform fresh search without clearing cache first - more efficient
                var freshHandlers = await FindHandlersOptimized(workspace.CurrentSolution, requestInfo);

                if (freshHandlers.Any())
                {
                    // Update cache with fresh results (this will replace the stale entry)
                    _cacheManager.CacheHandlers(failedHandler.RequestTypeName, freshHandlers);
                    
                    System.Diagnostics.Debug.WriteLine($"MediatR Navigation: Recovery found {freshHandlers.Count} handlers for {failedHandler.RequestTypeName}");

                    // Try to find the same handler type or navigate to first available
                    var matchingHandler = freshHandlers.FirstOrDefault(h => h.HandlerTypeName == failedHandler.HandlerTypeName) 
                                         ?? freshHandlers.First();

                    return await NavigateToLocationAsync(matchingHandler.Location);
                }
                else
                {
                    // Only clear cache if no handlers found (handler was deleted)
                    _cacheManager.InvalidateHandlersForRequestType(failedHandler.RequestTypeName);
                    System.Diagnostics.Debug.WriteLine($"MediatR Navigation: No handlers found for {failedHandler.RequestTypeName}, cleared cache entry");
                    
                    await ShowRecoveryFailedMessageAsync(failedHandler.HandlerTypeName, $"Handler for '{failedHandler.RequestTypeName}' no longer exists in the solution");
                    return false;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatR Navigation: Error during recovery: {ex.Message}");
                await ShowRecoveryFailedMessageAsync(failedHandler.HandlerTypeName, $"Recovery failed: {ex.Message}");
                return false;
            }
        }

        private async Task ShowRecoveryFailedMessageAsync(string handlerTypeName, string reason)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            
            try
            {
                // Get the service provider to show the message
                var serviceProvider = _serviceProvider ?? ServiceProvider.GlobalProvider;
                if (serviceProvider != null)
                {
                    var uiShell = serviceProvider.GetService(typeof(SVsUIShell)) as IVsUIShell;
                    if (uiShell != null)
                    {
                        var message = $"Could not navigate to handler '{handlerTypeName}'.\n\n" +
                                     $"Reason: {reason}\n\n" +
                                     "The handler may have been moved, renamed, or deleted. " +
                                     "Try rebuilding your solution or check if the handler still exists.";

                        var title = "MediatR Extension - Navigation Failed";
                        var result = 0;
                        uiShell.ShowMessageBox(0, Guid.Empty, title, message, 
                                             string.Empty, 0, OLEMSGBUTTON.OLEMSGBUTTON_OK, 
                                             OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST, 
                                             OLEMSGICON.OLEMSGICON_WARNING, 0, out result);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error showing recovery failed message: {ex.Message}");
            }
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
            else if (solution?.FilePath != null && _cacheManager.CurrentSolutionPath != solution.FilePath)
            {
                // Solution has changed but cache manager wasn't reinitialized - fix it
                System.Diagnostics.Debug.WriteLine($"MediatR Extension: Detected solution mismatch. Cache: {_cacheManager.CurrentSolutionPath}, Current: {solution.FilePath}");
                await ReinitializeCacheForNewSolution(solution);
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