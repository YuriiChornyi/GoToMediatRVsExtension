using System;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell.Interop;

namespace VSIXExtention
{
    [Export]
    public class MediatRGoToImplementationProvider
    {
        private readonly System.IServiceProvider _serviceProvider;
        private readonly MediatRNavigationService _navigationService;
        private readonly ITextDocumentFactoryService _textDocumentFactory;

        public MediatRGoToImplementationProvider(System.IServiceProvider serviceProvider,
            ITextDocumentFactoryService textDocumentFactory,
            MediatRNavigationService navigationService)
        {
            _serviceProvider = serviceProvider;
            _navigationService = navigationService;
            _serviceProvider = serviceProvider;
            _textDocumentFactory = textDocumentFactory;
            _navigationService = navigationService;
        }


        [ImportingConstructor]
        public MediatRGoToImplementationProvider()
        {
            _serviceProvider = ServiceProvider.GlobalProvider;
            _navigationService = new MediatRNavigationService(_serviceProvider);
        }
        public async Task<bool> TryGoToImplementationAsync(ITextView textView, int position)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // Use the same file path detection as context check
                var textBuffer = textView.TextBuffer;
                var filePath = GetFilePathFromTextBuffer(textBuffer);
                
                // Early bailout: Only process C# files
                if (string.IsNullOrEmpty(filePath) || !filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    return false;

                // Early bailout: Skip multiline selections (performance optimization)
                if (!textView.Selection.IsEmpty)
                {
                    var selectionSpan = textView.Selection.SelectedSpans[0];
                    var startLine = textView.TextSnapshot.GetLineFromPosition(selectionSpan.Start.Position);
                    var endLine = textView.TextSnapshot.GetLineFromPosition(selectionSpan.End.Position);
                    
                    // If selection spans multiple lines, unlikely to be a specific class navigation
                    if (endLine.LineNumber > startLine.LineNumber)
                        return false;
                }

                // Use the same workspace detection logic as context check
                VisualStudioWorkspace workspace = null;
                
                // Method 1: Through service provider
                workspace = _serviceProvider.GetService(typeof(VisualStudioWorkspace)) as VisualStudioWorkspace;
                
                // Method 2: Through global service provider
                if (workspace == null)
                {
                    workspace = Package.GetGlobalService(typeof(VisualStudioWorkspace)) as VisualStudioWorkspace;
                }
                
                // Method 3: Through component model
                if (workspace == null)
                {
                    try
                    {
                        var componentModel = _serviceProvider.GetService(typeof(SComponentModel)) as IComponentModel;
                        if (componentModel != null)
                        {
                            workspace = componentModel.GetService<VisualStudioWorkspace>();
                        }
                    }
                    catch
                    {
                        // Ignore component model failures
                    }
                }

                if (workspace?.CurrentSolution == null)
                {
                    return false;
                }

                var documentIds = workspace.CurrentSolution.GetDocumentIdsWithFilePath(filePath);
                var documentId = documentIds.FirstOrDefault();
                if (documentId == null)
                {
                    return false;
                }

                var doc = workspace.CurrentSolution.GetDocument(documentId);
                if (doc == null)
                {
                    return false;
                }

                var syntaxTree = await doc.GetSyntaxTreeAsync();
                if (syntaxTree == null)
                {
                    return false;
                }

                // Check if there's a selection first, then fall back to the provided position
                Microsoft.CodeAnalysis.Text.TextSpan textSpan;
                if (!textView.Selection.IsEmpty)
                {
                    // Use the selection span
                    var selectionSpan = textView.Selection.SelectedSpans[0];
                    textSpan = new Microsoft.CodeAnalysis.Text.TextSpan(selectionSpan.Start.Position, selectionSpan.Length);
                }
                else
                {
                    // Use provided position (usually caret position)
                    textSpan = new Microsoft.CodeAnalysis.Text.TextSpan(position, 0);
                }

                var root = await syntaxTree.GetRootAsync();
                var node = root.FindNode(textSpan);

                // Early bailout: Quick syntax check - if we're not in/on a class, skip expensive semantic analysis
                var classDeclaration = node.FirstAncestorOrSelf<ClassDeclarationSyntax>();
                if (classDeclaration == null)
                {
                    // Check if we're on an identifier that might reference a class
                    var identifierName = node as IdentifierNameSyntax ?? node.FirstAncestorOrSelf<IdentifierNameSyntax>();
                    if (identifierName == null)
                        return false; // Not on a class or identifier, definitely not MediatR
                }

                // Now do the expensive semantic analysis only if we passed the quick checks
                var semanticModel = await doc.GetSemanticModelAsync();
                if (semanticModel == null)
                {
                    return false;
                }

                // Check if we're on a class declaration
                if (classDeclaration != null)
                {
                    var typeSymbol = semanticModel.GetDeclaredSymbol(classDeclaration) as INamedTypeSymbol;
                    if (typeSymbol != null)
                    {
                        var requestInfo = MediatRPatternMatcher.GetRequestInfo(typeSymbol, semanticModel);
                        if (requestInfo != null)
                        {
                            return await _navigationService.TryNavigateToHandlerAsync(typeSymbol);
                        }
                    }
                }

                // Check if we're on an identifier that references a MediatR request
                var identifierName2 = node as IdentifierNameSyntax ?? node.FirstAncestorOrSelf<IdentifierNameSyntax>();
                if (identifierName2 != null)
                {
                    var symbolInfo = semanticModel.GetSymbolInfo(identifierName2);
                    if (symbolInfo.Symbol is INamedTypeSymbol namedTypeSymbol)
                    {
                        var requestInfo = MediatRPatternMatcher.GetRequestInfo(namedTypeSymbol, semanticModel);
                        if (requestInfo != null)
                        {
                            return await _navigationService.TryNavigateToHandlerAsync(namedTypeSymbol);
                        }
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatR Provider: Error in TryGoToImplementationAsync: {ex.Message}");
                return false;
            }
        }

        private string GetFilePathFromTextBuffer(Microsoft.VisualStudio.Text.ITextBuffer textBuffer)
        {
            try
            {
                if (textBuffer.Properties.TryGetProperty<Microsoft.VisualStudio.TextManager.Interop.IVsTextBuffer>(typeof(Microsoft.VisualStudio.TextManager.Interop.IVsTextBuffer), out var vsTextBuffer))
                {
                    var persistFileFormat = vsTextBuffer as Microsoft.VisualStudio.Shell.Interop.IPersistFileFormat;
                    if (persistFileFormat != null)
                    {
                        persistFileFormat.GetCurFile(out var filePath, out _);
                        return filePath;
                    }
                }

                // Alternative approach using document properties
                textBuffer.Properties.TryGetProperty<Microsoft.VisualStudio.Text.ITextDocument>(typeof(Microsoft.VisualStudio.Text.ITextDocument), out Microsoft.VisualStudio.Text.ITextDocument textDocument);
                return textDocument?.FilePath;
            }
            catch
            {
                return null;
            }
        }
    }
}