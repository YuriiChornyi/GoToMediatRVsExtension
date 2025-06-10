using System;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;

namespace VSIXExtention
{
    [Export]
    public class MediatRGoToImplementationProvider : IDisposable
    {
        private readonly IServiceProvider _serviceProvider;
        private VisualStudioWorkspace _cachedWorkspace;
        private readonly object _workspaceLock = new object();
        private bool _disposed = false;

        [ImportingConstructor]
        public MediatRGoToImplementationProvider()
        {
            _serviceProvider = ServiceProvider.GlobalProvider;
        }

        public async Task<bool> TryGoToImplementationAsync(ITextView textView, int position)
        {
            try
            {
                // Fast path: check context before any expensive operations
                if (!IsValidContext(textView))
                    return false;

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var workspace = GetOrCreateWorkspace();
                if (workspace?.CurrentSolution == null)
                    return false;

                var document = GetDocument(textView, workspace);
                if (document == null)
                    return false;

                var typeSymbol = await GetMediatRTypeSymbolAsync(textView, position, document);
                if (typeSymbol == null)
                    return false;

                // Use the new method that finds ALL handlers (both request and notification)
                var semanticModel = await document.GetSemanticModelAsync();
                var allHandlers = await MediatRPatternMatcher.FindAllHandlersForTypeSymbol(workspace.CurrentSolution, typeSymbol, semanticModel);

                if (!allHandlers.Any())
                {
                    System.Diagnostics.Debug.WriteLine($"MediatR Provider: No handlers found for {typeSymbol.Name}");
                    return false;
                }

                // Log what we found for debugging
                var requestHandlers = allHandlers.Where(h => !h.IsNotificationHandler).ToList();
                var notificationHandlers = allHandlers.Where(h => h.IsNotificationHandler).ToList();
                
                System.Diagnostics.Debug.WriteLine($"MediatR Provider: Found {allHandlers.Count} total handlers for {typeSymbol.Name}:");
                if (requestHandlers.Any())
                {
                    System.Diagnostics.Debug.WriteLine($"  - {requestHandlers.Count} request handler(s): {string.Join(", ", requestHandlers.Select(h => h.HandlerTypeName))}");
                }
                if (notificationHandlers.Any())
                {
                    System.Diagnostics.Debug.WriteLine($"  - {notificationHandlers.Count} notification handler(s): {string.Join(", ", notificationHandlers.Select(h => h.HandlerTypeName))}");
                }

                // Navigate to handlers using the existing navigation logic
                return await NavigateToHandlers(allHandlers, typeSymbol, semanticModel);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatR Provider: Error in TryGoToImplementationAsync: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> NavigateToHandlers(System.Collections.Generic.List<MediatRPatternMatcher.MediatRHandlerInfo> allHandlers, INamedTypeSymbol typeSymbol, SemanticModel semanticModel)
        {
            try
            {
                if (allHandlers.Count == 1)
                {
                    // Single handler - navigate directly
                    return await NavigateToLocationAsync(allHandlers[0].Location);
                }

                // Multiple handlers - show selection dialog
                // Check if this class implements both IRequest and INotification
                bool implementsBoth = MediatRPatternMatcher.ImplementsBothRequestAndNotification(typeSymbol, semanticModel);
                
                if (implementsBoth)
                {
                    // Group handlers by type for better user experience
                    var requestHandlers = allHandlers.Where(h => !h.IsNotificationHandler).ToList();
                    var notificationHandlers = allHandlers.Where(h => h.IsNotificationHandler).ToList();
                    
                    string message = $"Multiple handlers found for '{typeSymbol.Name}':\n";
                    if (requestHandlers.Any())
                    {
                        message += $"• {requestHandlers.Count} Request Handler(s)\n";
                    }
                    if (notificationHandlers.Any())
                    {
                        message += $"• {notificationHandlers.Count} Notification Handler(s)\n";
                    }
                    message += "\nPlease select one:";
                    
                    var handlerDisplayInfo = allHandlers.Select(h => new HandlerDisplayInfo
                    {
                        Handler = h,
                        DisplayText = FormatHandlerDisplayText(h)
                    }).ToArray();

                    var selectedHandlerName = ShowHandlerSelectionDialog(handlerDisplayInfo, message);
                    
                    if (selectedHandlerName == null)
                    {
                        return true; // User cancelled
                    }

                    var selectedHandler = handlerDisplayInfo.FirstOrDefault(hdi => hdi.DisplayText == selectedHandlerName)?.Handler;
                    if (selectedHandler != null)
                    {
                        return await NavigateToLocationAsync(selectedHandler.Location);
                    }
                }
                else
                {
                    // Standard multiple handlers of the same type
                    var handlerDisplayInfo = allHandlers.Select(h => new HandlerDisplayInfo
                    {
                        Handler = h,
                        DisplayText = FormatHandlerDisplayText(h)
                    }).ToArray();

                    var isNotification = allHandlers.First().IsNotificationHandler;
                    string handlerType = isNotification ? "notification handler" : "handler";
                    string message = $"Multiple {handlerType}s found. Please select one:";

                    var selectedHandlerName = ShowHandlerSelectionDialog(handlerDisplayInfo, message);
                    
                    if (selectedHandlerName == null)
                    {
                        return true; // User cancelled
                    }

                    var selectedHandler = handlerDisplayInfo.FirstOrDefault(hdi => hdi.DisplayText == selectedHandlerName)?.Handler;
                    if (selectedHandler != null)
                    {
                        return await NavigateToLocationAsync(selectedHandler.Location);
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatR Provider: Error navigating to handlers: {ex.Message}");
                return false;
            }
        }

        private string FormatHandlerDisplayText(MediatRPatternMatcher.MediatRHandlerInfo handler)
        {
            try
            {
                var handlerType = handler.IsNotificationHandler ? "[Notification]" : "[Request]";
                var filePath = handler.Location?.SourceTree?.FilePath;
                
                if (string.IsNullOrEmpty(filePath))
                {
                    return $"{handlerType} {handler.HandlerTypeName}";
                }

                // Extract last 2-3 folders for context
                var pathParts = filePath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (pathParts.Length <= 2)
                {
                    return $"{handlerType} {handler.HandlerTypeName} ({string.Join("/", pathParts)})";
                }

                var relevantParts = pathParts.Skip(Math.Max(0, pathParts.Length - 3)).ToArray();
                return $"{handlerType} {handler.HandlerTypeName} ({string.Join("/", relevantParts)})";
            }
            catch
            {
                var handlerType = handler.IsNotificationHandler ? "[Notification]" : "[Request]";
                return $"{handlerType} {handler.HandlerTypeName}";
            }
        }

        private string ShowHandlerSelectionDialog(HandlerDisplayInfo[] handlerDisplayInfo, string message)
        {
            var handlerNames = handlerDisplayInfo.Select(hdi => hdi.DisplayText).ToArray();
            var dialog = new HandlerSelectionDialog(message, handlerNames);
            
            var result = dialog.ShowModal();
            if (result != true)
            {
                return null;
            }
            
            return dialog.SelectedHandler;
        }

        private async Task<bool> NavigateToLocationAsync(Location location)
        {
            if (location?.SourceTree?.FilePath == null)
            {
                System.Diagnostics.Debug.WriteLine("MediatR Provider: Location or FilePath is null");
                return false;
            }

            var filePath = location.SourceTree.FilePath;
            var lineSpan = location.GetLineSpan();
            
            System.Diagnostics.Debug.WriteLine($"MediatR Provider: Navigating to {filePath} at line {lineSpan.StartLinePosition.Line + 1}");
            
            if (!System.IO.File.Exists(filePath))
            {
                System.Diagnostics.Debug.WriteLine($"MediatR Provider: File not found: {filePath}");
                return false;
            }
            
            return await OpenDocumentAndNavigate(filePath, lineSpan.StartLinePosition);
        }

        private async Task<bool> OpenDocumentAndNavigate(string filePath, Microsoft.CodeAnalysis.Text.LinePosition linePosition)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                
                System.Diagnostics.Debug.WriteLine($"MediatR Provider: Opening file: {filePath} at line {linePosition.Line + 1}");

                var dte = _serviceProvider.GetService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                if (dte?.ItemOperations == null)
                {
                    return false;
                }

                var window = dte.ItemOperations.OpenFile(filePath);
                if (window?.Document?.Object("TextDocument") is EnvDTE.TextDocument textDocument)
                {
                    var selection = textDocument.Selection;
                    selection.MoveToLineAndOffset(linePosition.Line + 1, linePosition.Character + 1, false);
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatR Provider: Error opening document {filePath}: {ex.Message}");
                return false;
            }
        }

        private bool IsValidContext(ITextView textView)
        {
            var textBuffer = textView?.TextBuffer;
            if (textBuffer == null)
                return false;

            var contentType = textBuffer.ContentType;
            if (contentType?.TypeName != "CSharp")
                return false;

            var filePath = GetFilePathFromTextBuffer(textBuffer);
            if (string.IsNullOrEmpty(filePath) || !filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                return false;

            if (!textView.Selection.IsEmpty && IsMultilineSelection(textView))
                return false;

            return true;
        }

        private bool IsMultilineSelection(ITextView textView)
        {
            var selectionSpan = textView.Selection.SelectedSpans[0];
            var startLine = textView.TextSnapshot.GetLineFromPosition(selectionSpan.Start.Position);
            var endLine = textView.TextSnapshot.GetLineFromPosition(selectionSpan.End.Position);
            return endLine.LineNumber > startLine.LineNumber;
        }

        private VisualStudioWorkspace GetOrCreateWorkspace()
        {
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
            var workspace = Package.GetGlobalService(typeof(VisualStudioWorkspace)) as VisualStudioWorkspace;
            if (workspace != null)
                return workspace;

            workspace = _serviceProvider.GetService(typeof(VisualStudioWorkspace)) as VisualStudioWorkspace;
            if (workspace != null)
                return workspace;

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

        private Document GetDocument(ITextView textView, VisualStudioWorkspace workspace)
        {
            var filePath = GetFilePathFromTextBuffer(textView.TextBuffer);

            var documentIds = workspace.CurrentSolution.GetDocumentIdsWithFilePath(filePath);
            if (!documentIds.Any())
                return null;

            var documentId = documentIds.First();
            return workspace.CurrentSolution.GetDocument(documentId);
        }

        private async Task<INamedTypeSymbol> GetMediatRTypeSymbolAsync(ITextView textView, int position, Document document)
        {
            var syntaxTree = await document.GetSyntaxTreeAsync();
            if (syntaxTree == null)
                return null;

            var textSpan = GetTextSpan(textView, position);
            var root = await syntaxTree.GetRootAsync();
            var node = root.FindNode(textSpan, getInnermostNodeForTie: true);

            var classDeclaration = node.FirstAncestorOrSelf<ClassDeclarationSyntax>();
            var identifierName = node as IdentifierNameSyntax ?? node.FirstAncestorOrSelf<IdentifierNameSyntax>();

            if (classDeclaration == null && identifierName == null)
                return null;

            var semanticModel = await document.GetSemanticModelAsync();
            if (semanticModel == null)
                return null;

            if (classDeclaration != null)
            {
                var typeSymbol = semanticModel.GetDeclaredSymbol(classDeclaration) as INamedTypeSymbol;
                if (IsValidMediatRType(typeSymbol, semanticModel))
                    return typeSymbol;
            }

            if (identifierName != null)
            {
                var symbolInfo = semanticModel.GetSymbolInfo(identifierName);
                if (symbolInfo.Symbol is INamedTypeSymbol namedTypeSymbol && IsValidMediatRType(namedTypeSymbol, semanticModel))
                    return namedTypeSymbol;
            }

            return null;
        }

        private Microsoft.CodeAnalysis.Text.TextSpan GetTextSpan(ITextView textView, int position)
        {
            if (!textView.Selection.IsEmpty)
            {
                var selectionSpan = textView.Selection.SelectedSpans[0];
                return new Microsoft.CodeAnalysis.Text.TextSpan(selectionSpan.Start.Position, selectionSpan.Length);
            }

            return new Microsoft.CodeAnalysis.Text.TextSpan(position, 0);
        }

        private bool IsValidMediatRType(INamedTypeSymbol typeSymbol, SemanticModel semanticModel)
        {
            return typeSymbol != null && MediatRPatternMatcher.IsMediatRRequest(typeSymbol, semanticModel);
        }

        private string GetFilePathFromTextBuffer(ITextBuffer textBuffer)
        {
            try
            {
                if (textBuffer.Properties.TryGetProperty<ITextDocument>(typeof(ITextDocument), out var textDocument))
                {
                    var filePath = textDocument?.FilePath;
                    if (!string.IsNullOrEmpty(filePath))
                        return filePath;
                }

                if (textBuffer.Properties.TryGetProperty<Microsoft.VisualStudio.TextManager.Interop.IVsTextBuffer>(
                    typeof(Microsoft.VisualStudio.TextManager.Interop.IVsTextBuffer), out var vsTextBuffer))
                {
                    ThreadHelper.ThrowIfNotOnUIThread();

                    if (vsTextBuffer is Microsoft.VisualStudio.Shell.Interop.IPersistFileFormat persistFileFormat)
                    {
                        persistFileFormat.GetCurFile(out var filePath, out _);
                        return filePath;
                    }
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
            }
        }
    }
}