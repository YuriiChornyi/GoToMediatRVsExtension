using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace VSIXExtension.Services
{
    public class WorkspaceService : IWorkspaceService, IDisposable
    {
        private readonly Lazy<VisualStudioWorkspace> _lazyWorkspace;
        private VisualStudioWorkspace _explicitWorkspace;
        private readonly object _lockObject = new object();

        public WorkspaceService()
        {
            // Lazy initialization as fallback - will be called on first access if not explicitly set
            _lazyWorkspace = new Lazy<VisualStudioWorkspace>(() =>
            {
                try
                {
                    ThreadHelper.ThrowIfNotOnUIThread();

                    var componentModel = ServiceProvider.GlobalProvider?.GetService(typeof(SComponentModel)) as IComponentModel;
                    var workspace = componentModel?.GetService<VisualStudioWorkspace>();

                    System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: WorkspaceService: Lazy-initialized workspace: {workspace != null}");
                    return workspace;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: WorkspaceService: Error during lazy workspace initialization: {ex.Message}");
                    return null;
                }
            });
        }

        /// <summary>
        /// Sets the workspace explicitly during package initialization (preferred method)
        /// </summary>
        public void SetWorkspace(VisualStudioWorkspace workspace)
        {
            lock (_lockObject)
            {
                _explicitWorkspace = workspace;
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: WorkspaceService: Explicitly set workspace: {workspace != null}");
            }
        }

        /// <summary>
        /// Gets the workspace, using explicit workspace if available, otherwise lazy initialization
        /// </summary>
        public VisualStudioWorkspace GetWorkspace()
        {
            lock (_lockObject)
            {
                // Prefer explicitly set workspace
                if (_explicitWorkspace != null)
                {
                    return _explicitWorkspace;
                }
            }

            // Fall back to lazy initialization if no explicit workspace was set
            return _lazyWorkspace.Value;
        }

        /// <summary>
        /// Async version that ensures UI thread access for workspace acquisition
        /// </summary>
        public async Task<VisualStudioWorkspace> GetWorkspaceAsync()
        {
            lock (_lockObject)
            {
                // Return immediately if we have an explicitly set workspace
                if (_explicitWorkspace != null)
                {
                    return _explicitWorkspace;
                }
            }

            // Switch to UI thread for safe workspace acquisition
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            return GetWorkspace();
        }

        public void Dispose()
        {
            lock (_lockObject)
            {
                _explicitWorkspace = null;
            }
            // Don't dispose the lazy workspace as it's owned by VS
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
    }
}