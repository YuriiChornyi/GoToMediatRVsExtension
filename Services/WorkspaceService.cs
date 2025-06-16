using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
using System.Linq;
using VSIXExtention.Interfaces;

namespace VSIXExtention.Services
{
    public class WorkspaceService : IWorkspaceService, IDisposable
    {
        private VisualStudioWorkspace _cachedWorkspace;
        private bool _disposed = false;

        public WorkspaceService()
        {
        }

        public void InitializeWorkspace()
        {
            var workspace = Package.GetGlobalService(typeof(VisualStudioWorkspace)) as VisualStudioWorkspace;

            if (workspace != null)
                _cachedWorkspace = workspace;

            try
            {
                var compModel = (IComponentModel)Package.GetGlobalService(typeof(SComponentModel));

                if (compModel != null)
                {
                    var vsWorkspace = compModel.GetService<VisualStudioWorkspace>();

                    _cachedWorkspace = vsWorkspace;
                }
                else
                {
                    _cachedWorkspace = null;
                }
            }
            catch
            {
                _cachedWorkspace = null;
            }
        }

        public VisualStudioWorkspace GetWorkspace()
        {
            return _cachedWorkspace;

        }

        public Document GetDocumentFromTextView(ITextView textView)
        {
            var workspace = GetWorkspace();
            if (workspace?.CurrentSolution == null)
                return null;

            var filePath = GetFilePathFromTextView(textView);
            if (string.IsNullOrEmpty(filePath))
                return null;

            var documentIds = workspace.CurrentSolution.GetDocumentIdsWithFilePath(filePath);
            var documentId = documentIds.FirstOrDefault();

            return documentId != null ? workspace.CurrentSolution.GetDocument(documentId) : null;
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

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
            }
        }
    }
}
