using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;

namespace VSIXExtention.Services
{
    public class WorkspaceService : IWorkspaceService, IDisposable
    {
        private VisualStudioWorkspace _cachedWorkspace;

        public void SetWorkspace(VisualStudioWorkspace workspace)
        {
            _cachedWorkspace = workspace;
        }

        public void Dispose()
        {
            // Nothing to dispose anymore
        }

        public VisualStudioWorkspace GetWorkspace()
        {
            return _cachedWorkspace;
        }

        public Document GetDocumentFromTextView(ITextView textView)
        {
            if (_cachedWorkspace?.CurrentSolution == null)
                return null;

            var filePath = GetFilePathFromTextView(textView);
            if (string.IsNullOrEmpty(filePath))
                return null;

            var documentIds = _cachedWorkspace.CurrentSolution.GetDocumentIdsWithFilePath(filePath);
            var documentId = documentIds.FirstOrDefault();

            return documentId != null ? _cachedWorkspace.CurrentSolution.GetDocument(documentId) : null;
        }

        public string GetFilePathFromTextView(ITextView textView)
        {
            var textBuffer = textView?.TextBuffer;
            if (textBuffer == null)
                return null;

            return GetFilePathFromTextBuffer(textBuffer);
        }

        private string GetFilePathFromTextBuffer(ITextBuffer textBuffer)
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                // Method 1: Through TextDocument (fastest and most reliable)
                if (textBuffer.Properties.TryGetProperty<ITextDocument>(typeof(ITextDocument), out var textDocument))
                {
                    var filePath = textDocument?.FilePath;
                    if (!string.IsNullOrEmpty(filePath))
                        return filePath;
                }

                // Method 2: Through VsTextBuffer (fallback)
                if (textBuffer.Properties.TryGetProperty<IVsTextBuffer>(typeof(IVsTextBuffer), out var vsTextBuffer))
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