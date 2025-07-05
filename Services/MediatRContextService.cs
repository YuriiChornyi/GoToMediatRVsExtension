using System;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Text.Editor;

namespace VSIXExtention.Services
{
    public class MediatRContextService
    {
        private readonly WorkspaceService _workspaceService;
        
        public MediatRContextService(WorkspaceService workspaceService)
        {
            _workspaceService = workspaceService;
        }

        public async Task<bool> IsInMediatRContextAsync(ITextView textView)
        {
            try
            {
                if (!IsValidContext(textView))
                    return false;

                var document = _workspaceService.GetDocumentFromTextView(textView);
                if (document == null)
                    return false;

                var typeSymbol = await GetMediatRTypeSymbolAsync(textView, textView.Caret.Position.BufferPosition.Position);
                return typeSymbol != null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: MediatRContext: Error checking context: {ex.Message}");
                return false;
            }
        }

        public async Task<INamedTypeSymbol> GetMediatRTypeSymbolAsync(ITextView textView, int position)
        {
            try
            {
                var document = _workspaceService.GetDocumentFromTextView(textView);
                if (document == null)
                    return null;

                var syntaxTree = await document.GetSyntaxTreeAsync();
                if (syntaxTree == null)
                    return null;

                var textSpan = GetTextSpan(textView, position);
                var root = await syntaxTree.GetRootAsync();
                var node = root.FindNode(textSpan, getInnermostNodeForTie: true);

                // Quick syntax check - must be in/on a type declaration (class/record) or identifier
                var typeDeclaration = node.FirstAncestorOrSelf<TypeDeclarationSyntax>();
                var identifierName = node as IdentifierNameSyntax ?? node.FirstAncestorOrSelf<IdentifierNameSyntax>();

                if (typeDeclaration == null && identifierName == null)
                    return null;

                // Only get semantic model if we passed syntax checks (expensive operation)
                var semanticModel = await document.GetSemanticModelAsync();
                if (semanticModel == null)
                    return null;

                // Check type declaration first (class, record, etc. - more common case)
                if (typeDeclaration != null)
                {
                    var typeSymbol = semanticModel.GetDeclaredSymbol(typeDeclaration) as INamedTypeSymbol;
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
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: MediatRContext: Error getting type symbol: {ex.Message}");
                return null;
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

            var filePath = _workspaceService.GetFilePathFromTextView(textView);

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

        private TextSpan GetTextSpan(ITextView textView, int position)
        {
            if (!textView.Selection.IsEmpty)
            {
                var selectionSpan = textView.Selection.SelectedSpans[0];
                return new TextSpan(selectionSpan.Start.Position, selectionSpan.Length);
            }

            return new TextSpan(position, 0);
        }

        private bool IsValidMediatRType(INamedTypeSymbol typeSymbol, SemanticModel semanticModel)
        {
            return typeSymbol != null && MediatRPatternMatcher.GetRequestInfo(typeSymbol, semanticModel) != null;
        }
    }
} 