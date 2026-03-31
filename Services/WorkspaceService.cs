using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Projection;
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

            var buffer = textView.TextBuffer;

            // For .razor files, resolve to the projected C# buffer so Roslyn can provide a semantic model
            if (buffer.ContentType.IsOfType("razor"))
            {
                var csharpBuffer = GetCSharpProjectionBuffer(textView);
                if (csharpBuffer != null)
                    buffer = csharpBuffer;
                else
                    return null; // No C# projection available — can't navigate
            }

            var filePath = GetFilePathFromTextBuffer(buffer);
            if (string.IsNullOrEmpty(filePath))
                return null;

            var documentIds = workspace.CurrentSolution.GetDocumentIdsWithFilePath(filePath);
            var documentId = documentIds.FirstOrDefault();

            return documentId != null ? workspace.CurrentSolution.GetDocument(documentId) : null;
        }

        /// <summary>
        /// Returns the projected C# buffer inside a razor view, or null if none exists.
        /// </summary>
        internal ITextBuffer GetCSharpProjectionBuffer(ITextView textView)
        {
            try
            {
                return textView.BufferGraph
                    .GetTextBuffers(b => b.ContentType.IsOfType("CSharp"))
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Convenience method: maps a position from the outer text view buffer to the projected C# buffer
        /// for .razor files. For non-razor files, returns the position unchanged.
        /// </summary>
        public int GetMappedPosition(ITextView textView, int position)
        {
            if (!textView.TextBuffer.ContentType.IsOfType("razor"))
                return position;

            var csharpBuffer = GetCSharpProjectionBuffer(textView);
            if (csharpBuffer == null)
                return position;

            var mapped = MapToProjectedPosition(textView, csharpBuffer, position);
            return mapped >= 0 ? mapped : position;
        }

        /// <summary>
        /// Maps a position in the outer (razor) buffer to its counterpart in the projected C# buffer.
        /// Returns -1 if mapping is unavailable.
        /// </summary>
        internal int MapToProjectedPosition(ITextView textView, ITextBuffer csharpBuffer, int outerPosition)
        {
            try
            {
                var outerPoint = new SnapshotPoint(textView.TextSnapshot, outerPosition);
                var mapped = textView.BufferGraph.MapDownToBuffer(
                    outerPoint, PointTrackingMode.Positive, csharpBuffer, PositionAffinity.Successor);
                return mapped.HasValue ? mapped.Value.Position : -1;
            }
            catch
            {
                return -1;
            }
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