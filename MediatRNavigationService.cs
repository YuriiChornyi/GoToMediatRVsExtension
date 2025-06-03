using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.VisualStudio.ComponentModelHost;

namespace VSIXExtention
{
    public class MediatRNavigationService
    {
        private readonly IServiceProvider _serviceProvider;
        private VisualStudioWorkspace _cachedWorkspace;
        private readonly object _workspaceLock = new object();
        
        // Cache compilation results to avoid repeated expensive operations
        private readonly ConcurrentDictionary<ProjectId, Compilation> _compilationCache = new ConcurrentDictionary<ProjectId, Compilation>();
        private readonly ConcurrentDictionary<string, List<MediatRPatternMatcher.MediatRHandlerInfo>> _handlerCache = new ConcurrentDictionary<string, List<MediatRPatternMatcher.MediatRHandlerInfo>>();
        private DateTime _lastCacheUpdate = DateTime.MinValue;
        private readonly TimeSpan _cacheValidityPeriod = TimeSpan.FromMinutes(5); // Cache for 5 minutes

        public MediatRNavigationService(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
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

        private async Task<List<MediatRPatternMatcher.MediatRHandlerInfo>> FindHandlersWithCaching(
            Solution solution, 
            MediatRPatternMatcher.MediatRRequestInfo requestInfo)
        {
            var requestTypeName = requestInfo.RequestTypeName;
            var cacheKey = $"{requestTypeName}_{requestInfo.IsNotification}";

            // Check cache first
            if (_handlerCache.TryGetValue(cacheKey, out var cachedHandlers) && 
                DateTime.Now - _lastCacheUpdate < _cacheValidityPeriod)
            {
                return cachedHandlers;
            }

            // Find handlers with optimized search
            var handlers = await FindHandlersOptimized(solution, requestInfo);
            
            // Update cache
            _handlerCache.AddOrUpdate(cacheKey, handlers, (key, old) => handlers);
            _lastCacheUpdate = DateTime.Now;

            return handlers;
        }

        private async Task<List<MediatRPatternMatcher.MediatRHandlerInfo>> FindHandlersOptimized(
            Solution solution, 
            MediatRPatternMatcher.MediatRRequestInfo requestInfo)
        {
            var requestTypeName = requestInfo.RequestTypeName;
            
            // Filter projects to only those likely to contain handlers (performance optimization)
            var relevantProjects = solution.Projects
                .Where(p => p.SupportsCompilation && 
                           p.Language == LanguageNames.CSharp &&
                           !IsTestProject(p.Name))
                .ToList();

            if (!relevantProjects.Any())
                return new List<MediatRPatternMatcher.MediatRHandlerInfo>();

            // Process projects in parallel for better performance
            var tasks = relevantProjects.Select(async project =>
            {
                try
                {
                    var compilation = await GetOrCreateCompilationAsync(project);
                    if (compilation == null) return new List<MediatRPatternMatcher.MediatRHandlerInfo>();

                    return requestInfo.IsNotification 
                        ? await FindNotificationHandlersInProjectOptimized(compilation, requestTypeName)
                        : await FindHandlersInProjectOptimized(compilation, requestTypeName);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error processing project {project.Name}: {ex.Message}");
                    return new List<MediatRPatternMatcher.MediatRHandlerInfo>();
                }
            });

            var results = await Task.WhenAll(tasks);
            return results.SelectMany(handlers => handlers).ToList();
        }

        private async Task<Compilation> GetOrCreateCompilationAsync(Project project)
        {
            // Use cached compilation if available
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
            // Skip test projects for better performance (they rarely contain handlers)
            var testIndicators = new[] { "test", "tests", "spec", "specs", "unittest", "integrationtest" };
            var lowerName = projectName.ToLowerInvariant();
            return testIndicators.Any(indicator => lowerName.Contains(indicator));
        }

        private async Task<List<MediatRPatternMatcher.MediatRHandlerInfo>> FindHandlersInProjectOptimized(
            Compilation compilation, 
            string requestTypeName)
        {
            var handlers = new List<MediatRPatternMatcher.MediatRHandlerInfo>();

            // Process syntax trees in parallel for large projects
            var syntaxTrees = compilation.SyntaxTrees.ToList();
            
            if (syntaxTrees.Count <= 5)
            {
                // For small projects, process sequentially to avoid overhead
                foreach (var syntaxTree in syntaxTrees)
                {
                    var treeHandlers = await ProcessSyntaxTreeForHandlers(compilation, syntaxTree, requestTypeName, false);
                    handlers.AddRange(treeHandlers);
                }
            }
            else
            {
                // For larger projects, process in parallel
                var tasks = syntaxTrees.Select(syntaxTree => 
                    ProcessSyntaxTreeForHandlers(compilation, syntaxTree, requestTypeName, false));
                
                var results = await Task.WhenAll(tasks);
                handlers.AddRange(results.SelectMany(h => h));
            }

            return handlers;
        }

        private async Task<List<MediatRPatternMatcher.MediatRHandlerInfo>> FindNotificationHandlersInProjectOptimized(
            Compilation compilation, 
            string notificationTypeName)
        {
            var handlers = new List<MediatRPatternMatcher.MediatRHandlerInfo>();

            var syntaxTrees = compilation.SyntaxTrees.ToList();
            
            if (syntaxTrees.Count <= 5)
            {
                foreach (var syntaxTree in syntaxTrees)
                {
                    var treeHandlers = await ProcessSyntaxTreeForHandlers(compilation, syntaxTree, notificationTypeName, true);
                    handlers.AddRange(treeHandlers);
                }
            }
            else
            {
                var tasks = syntaxTrees.Select(syntaxTree => 
                    ProcessSyntaxTreeForHandlers(compilation, syntaxTree, notificationTypeName, true));
                
                var results = await Task.WhenAll(tasks);
                handlers.AddRange(results.SelectMany(h => h));
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

                // Quick pre-filter: check if file might contain handlers before expensive operations
                var sourceText = await syntaxTree.GetTextAsync();
                var text = sourceText.ToString();
                
                // Fast text-based check to avoid parsing files that definitely don't contain handlers
                if (!text.Contains("IRequestHandler") && !text.Contains("INotificationHandler"))
                    return handlers;

                var classDeclarations = root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>();

                foreach (var classDecl in classDeclarations)
                {
                    var typeSymbol = semanticModel.GetDeclaredSymbol(classDecl) as INamedTypeSymbol;
                    if (typeSymbol == null) continue;

                    var handlerInfo = MediatRPatternMatcher.GetHandlerInfo(typeSymbol, semanticModel);
                    if (handlerInfo == null) continue;

                    // Match the handler type and target type
                    if (handlerInfo.IsNotificationHandler == isNotificationHandler && 
                        handlerInfo.RequestTypeName == targetTypeName)
                    {
                        handlers.Add(handlerInfo);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error processing syntax tree {syntaxTree.FilePath}: {ex.Message}");
            }

            return handlers;
        }

        private async Task<bool> NavigateToHandlers(
            List<MediatRPatternMatcher.MediatRHandlerInfo> handlers, 
            bool isNotification)
        {
            if (!handlers.Any())
            {
                return false;
            }

            if (handlers.Count == 1)
            {
                return await NavigateToLocationAsync(handlers.First().Location);
            }

            return await NavigateToMultipleHandlersAsync(handlers, isNotification);
        }

        private async Task<bool> NavigateToMultipleHandlersAsync(
            List<MediatRPatternMatcher.MediatRHandlerInfo> handlers, 
            bool isNotification)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                var handlerNames = handlers.Select(h => h.HandlerTypeName).ToArray();
                var selectedHandler = ShowHandlerSelectionDialog(handlerNames, isNotification);

                if (string.IsNullOrEmpty(selectedHandler))
                {
                    return false;
                }

                var handler = handlers.FirstOrDefault(h => h.HandlerTypeName == selectedHandler);
                return handler != null && await NavigateToLocationAsync(handler.Location);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in NavigateToMultipleHandlersAsync: {ex.Message}");
                return false;
            }
        }

        private string ShowHandlerSelectionDialog(string[] handlerNames, bool isNotification)
        {
            try
            {
                var handlerType = isNotification ? "notification handlers" : "request handlers";
                var message = $"Multiple {handlerType} found. Select one to navigate to:";
                
                using (var dialog = new HandlerSelectionDialog(message, handlerNames))
                {
                    var result = dialog.ShowDialog();
                    return result == DialogResult.OK ? dialog.SelectedHandler : null;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error showing selection dialog: {ex.Message}");
                return handlerNames.FirstOrDefault();
            }
        }

        private async Task<bool> NavigateToLocationAsync(Location location)
        {
            if (location?.SourceTree == null)
                return false;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                var filePath = location.SourceTree.FilePath;
                var linePosition = location.GetLineSpan().StartLinePosition;

                return await OpenDocumentAndNavigate(filePath, linePosition);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error opening document: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> OpenDocumentAndNavigate(string filePath, Microsoft.CodeAnalysis.Text.LinePosition linePosition)
        {
            var shell = (IVsUIShellOpenDocument)_serviceProvider.GetService(typeof(SVsUIShellOpenDocument));
            if (shell == null)
                return false;

            var hr = shell.OpenDocumentViaProject(
                filePath,
                VSConstants.LOGVIEWID_Code,
                out _,
                out _,
                out _,
                out var windowFrame);

            if (hr != VSConstants.S_OK || windowFrame == null)
                return false;

            windowFrame.Show();

            return await NavigateToPosition(windowFrame, linePosition);
        }

        private async Task<bool> NavigateToPosition(IVsWindowFrame windowFrame, Microsoft.CodeAnalysis.Text.LinePosition linePosition)
        {
            var hr = windowFrame.GetProperty((int)__VSFPROPID.VSFPROPID_DocData, out var docData);
            if (hr != VSConstants.S_OK || !(docData is IVsTextBuffer textBuffer))
                return false;

            var textManager = (IVsTextManager)_serviceProvider.GetService(typeof(SVsTextManager));
            textManager?.NavigateToLineAndColumn(
                textBuffer,
                VSConstants.LOGVIEWID_Code,
                linePosition.Line,
                linePosition.Character,
                linePosition.Line,
                linePosition.Character);

            return true;
        }

        // Clear cache when solution changes (call this from appropriate events)
        public void ClearCache()
        {
            _compilationCache.Clear();
            _handlerCache.Clear();
            _lastCacheUpdate = DateTime.MinValue;
        }
    }

    public class HandlerSelectionDialog : Form
    {
        public string SelectedHandler { get; private set; }

        public HandlerSelectionDialog(string message, string[] handlerNames)
        {
            InitializeDialog(message, handlerNames);
        }

        private void InitializeDialog(string message, string[] handlerNames)
        {
            Text = "Select MediatR Handler";
            Size = new Size(400, 300);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;

            var label = CreateLabel(message);
            var listBox = CreateListBox(handlerNames);
            var (okButton, cancelButton) = CreateButtons();

            SetupEventHandlers(listBox, okButton);

            Controls.AddRange(new Control[] { label, listBox, okButton, cancelButton });

            AcceptButton = okButton;
            CancelButton = cancelButton;
        }

        private Label CreateLabel(string message)
        {
            return new Label
            {
                Text = message,
                Location = new Point(10, 10),
                Size = new Size(370, 30),
                AutoSize = false
            };
        }

        private ListBox CreateListBox(string[] handlerNames)
        {
            var listBox = new ListBox
            {
                Location = new Point(10, 50),
                Size = new Size(370, 150)
            };
            
            listBox.Items.AddRange(handlerNames);
            listBox.SelectedIndex = 0;

            return listBox;
        }

        private (Button okButton, Button cancelButton) CreateButtons()
        {
            var okButton = new Button
            {
                Text = "OK",
                Location = new Point(225, 220),
                Size = new Size(75, 23),
                DialogResult = DialogResult.OK
            };

            var cancelButton = new Button
            {
                Text = "Cancel",
                Location = new Point(305, 220),
                Size = new Size(75, 23),
                DialogResult = DialogResult.Cancel
            };

            return (okButton, cancelButton);
        }

        private void SetupEventHandlers(ListBox listBox, Button okButton)
        {
            okButton.Click += (s, e) => {
                SelectedHandler = listBox.SelectedItem?.ToString();
                DialogResult = DialogResult.OK;
                Close();
            };

            listBox.DoubleClick += (s, e) => {
                SelectedHandler = listBox.SelectedItem?.ToString();
                DialogResult = DialogResult.OK;
                Close();
            };
        }
    }
} 