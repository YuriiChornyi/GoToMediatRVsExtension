using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using VSIXExtention.Interfaces;

namespace VSIXExtention.Services
{
    public class WorkspaceService : IWorkspaceService, IDisposable
    {
        private readonly IServiceProvider _serviceProvider;
        private VisualStudioWorkspace _cachedWorkspace;
        private readonly object _workspaceLock = new object();
        private bool _eventsSubscribed = false;
        private bool _disposed = false;

        public event EventHandler<WorkspaceChangeEventArgs> WorkspaceChanged;

        public WorkspaceService(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        }

        public VisualStudioWorkspace GetWorkspace()
        {
            if (_cachedWorkspace != null)
                return _cachedWorkspace;

            lock (_workspaceLock)
            {
                if (_cachedWorkspace != null)
                    return _cachedWorkspace;

                _cachedWorkspace = GetVisualStudioWorkspace();
                
                if (_cachedWorkspace != null && !_eventsSubscribed)
                {
                    SubscribeToWorkspaceEvents();
                    _eventsSubscribed = true;
                }
                
                return _cachedWorkspace;
            }
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

        private void SubscribeToWorkspaceEvents()
        {
            try
            {
                if (_cachedWorkspace != null)
                {
                    _cachedWorkspace.WorkspaceChanged += OnWorkspaceChanged;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error subscribing to workspace events: {ex.Message}");
            }
        }

        private void OnWorkspaceChanged(object sender, WorkspaceChangeEventArgs e)
        {
            try
            {
                WorkspaceChanged?.Invoke(sender, e);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error handling workspace change: {ex.Message}");
            }
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
                if (textBuffer.Properties.TryGetProperty<IVsTextBuffer>(typeof(IVsTextBuffer), out var vsTextBuffer))
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
                try
                {
                    if (_cachedWorkspace != null && _eventsSubscribed)
                    {
                        _cachedWorkspace.WorkspaceChanged -= OnWorkspaceChanged;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error during WorkspaceService disposal: {ex.Message}");
                }
                finally
                {
                    _disposed = true;
                }
            }
        }
    }
} 