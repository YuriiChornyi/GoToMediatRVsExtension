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
    public class MediatRGoToImplementationProvider
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly Lazy<MediatRNavigationService> _navigationService;
        private VisualStudioWorkspace _cachedWorkspace;
        private readonly object _workspaceLock = new object();

        [ImportingConstructor]
        public MediatRGoToImplementationProvider()
        {
            _serviceProvider = ServiceProvider.GlobalProvider;
            // Lazy initialization to improve startup performance
            _navigationService = new Lazy<MediatRNavigationService>(() => new MediatRNavigationService(_serviceProvider));
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

                var document = await GetDocumentAsync(textView, workspace);
                if (document == null)
                    return false;

                var typeSymbol = await GetMediatRTypeSymbolAsync(textView, position, document);
                if (typeSymbol == null)
                    return false;

                return await _navigationService.Value.TryNavigateToHandlerAsync(typeSymbol);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatR Provider: Error in TryGoToImplementationAsync: {ex.Message}");
                return false;
            }
        }

        private bool IsValidContext(ITextView textView)
        {
            // Early bailout: check buffer properties first (fastest check)
            var textBuffer = textView?.TextBuffer;
            if (textBuffer == null)
                return false;

            // Quick content type check
            var contentType = textBuffer.ContentType;
            if (contentType?.TypeName != "CSharp")
                return false;

            var filePath = GetFilePathFromTextBuffer(textBuffer);
            
            // Only process C# files
            if (string.IsNullOrEmpty(filePath) || !filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                return false;

            // Skip multiline selections for performance
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
            // Try methods in order of likelihood to succeed (most common first)
            
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

        private async Task<Document> GetDocumentAsync(ITextView textView, VisualStudioWorkspace workspace)
        {
            var filePath = GetFilePathFromTextBuffer(textView.TextBuffer);
            
            // Use faster method to get document if available
            var documentIds = workspace.CurrentSolution.GetDocumentIdsWithFilePath(filePath);
            if (!documentIds.Any())
                return null;

            // Take first matching document (usually there's only one)
            var documentId = documentIds.First();
            return workspace.CurrentSolution.GetDocument(documentId);
        }

        private async Task<INamedTypeSymbol> GetMediatRTypeSymbolAsync(ITextView textView, int position, Document document)
        {
            // Get syntax tree once and reuse
            var syntaxTree = await document.GetSyntaxTreeAsync();
            if (syntaxTree == null)
                return null;

            var textSpan = GetTextSpan(textView, position);
            var root = await syntaxTree.GetRootAsync();
            
            // Find the most specific node first
            var node = root.FindNode(textSpan, getInnermostNodeForTie: true);

            // Quick syntax check - must be in/on a class or identifier
            var classDeclaration = node.FirstAncestorOrSelf<ClassDeclarationSyntax>();
            var identifierName = node as IdentifierNameSyntax ?? node.FirstAncestorOrSelf<IdentifierNameSyntax>();

            if (classDeclaration == null && identifierName == null)
                return null;

            // Only get semantic model if we passed syntax checks (expensive operation)
            var semanticModel = await document.GetSemanticModelAsync();
            if (semanticModel == null)
                return null;

            // Check class declaration first (more common case)
            if (classDeclaration != null)
            {
                var typeSymbol = semanticModel.GetDeclaredSymbol(classDeclaration) as INamedTypeSymbol;
                if (IsValidMediatRType(typeSymbol, semanticModel))
                    return typeSymbol;
            }

            // Check identifier reference (less common)
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
            return typeSymbol != null && MediatRPatternMatcher.GetRequestInfo(typeSymbol, semanticModel) != null;
        }

        private string GetFilePathFromTextBuffer(ITextBuffer textBuffer)
        {
            try
            {
                // Method 1: Through TextDocument (fastest and most reliable)
                if (textBuffer.Properties.TryGetProperty<ITextDocument>(typeof(ITextDocument), out var textDocument))
                {
                    var filePath = textDocument?.FilePath;
                    if (!string.IsNullOrEmpty(filePath))
                        return filePath;
                }

                // Method 2: Through VsTextBuffer (fallback)
                if (textBuffer.Properties.TryGetProperty<Microsoft.VisualStudio.TextManager.Interop.IVsTextBuffer>(
                    typeof(Microsoft.VisualStudio.TextManager.Interop.IVsTextBuffer), out var vsTextBuffer))
                {
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
    }
}