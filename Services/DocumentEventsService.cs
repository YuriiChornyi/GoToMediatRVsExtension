using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using VSIXExtention.Interfaces;

namespace VSIXExtention.Services
{
    public class DocumentEventsService : IDocumentEventService, IVsRunningDocTableEvents, IDisposable
    {
        private readonly IServiceProvider _serviceProvider;
        private IVsRunningDocumentTable _runningDocumentTable;
        private uint _rdtCookie;
        private bool _initialized = false;
        private bool _disposed = false;

        public event EventHandler<string> DocumentSaved;

        public DocumentEventsService(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        }

        public void Initialize()
        {
            if (_initialized)
                return;

            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                _runningDocumentTable = _serviceProvider.GetService(typeof(SVsRunningDocumentTable)) as IVsRunningDocumentTable;
                
                if (_runningDocumentTable != null)
                {
                    var hr = _runningDocumentTable.AdviseRunningDocTableEvents(this, out _rdtCookie);
                    if (hr == VSConstants.S_OK)
                    {
                        _initialized = true;
                        System.Diagnostics.Debug.WriteLine("Document Events: Successfully subscribed to document save events");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"Document Events: Failed to subscribe to document events, HRESULT: {hr}");
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("Document Events: Could not get IVsRunningDocumentTable service");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Document Events: Error during initialization: {ex.Message}");
            }
        }

        // IVsRunningDocTableEvents implementation
        public int OnAfterFirstDocumentLock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining)
        {
            return VSConstants.S_OK;
        }

        public int OnBeforeLastDocumentUnlock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining)
        {
            if (dwReadLocksRemaining == 0 && dwEditLocksRemaining == 0)
            {
                // Document is being completely removed from RDT
                //ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                //{
                //    //await HandleDocumentRemovedAsync(docCookie);
                //});
            }
            return VSConstants.S_OK;
        }

        public int OnAfterSave(uint docCookie)
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                if (_runningDocumentTable != null)
                {
                    var hr = _runningDocumentTable.GetDocumentInfo(docCookie, out _, out _, out _, out var docPath, out _, out _, out _);

                    if (hr == VSConstants.S_OK && !string.IsNullOrEmpty(docPath))
                    {
                        // Only process C# files
                        if (docPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                        {
                            System.Diagnostics.Debug.WriteLine($"Document Events: Document saved: {docPath}");
                            DocumentSaved?.Invoke(this, docPath);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Document Events: Error in OnAfterSave: {ex.Message}");
            }

            return VSConstants.S_OK;
        }

        public int OnAfterAttributeChange(uint docCookie, uint grfAttribs)
        {
            return VSConstants.S_OK;
        }

        public int OnBeforeDocumentWindowShow(uint docCookie, int fFirstShow, IVsWindowFrame pFrame)
        {
            return VSConstants.S_OK;
        }

        public int OnAfterDocumentWindowHide(uint docCookie, IVsWindowFrame pFrame)
        {
            return VSConstants.S_OK;
        }

        public void Dispose()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!_disposed)
            {
                try
                {
                    if (_runningDocumentTable != null && _initialized && _rdtCookie != 0)
                    {
                        _runningDocumentTable.UnadviseRunningDocTableEvents(_rdtCookie);
                        System.Diagnostics.Debug.WriteLine("Document Events: Unsubscribed from document save events");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Document Events: Error during disposal: {ex.Message}");
                }
                finally
                {
                    _disposed = true;
                    _initialized = false;
                    _rdtCookie = 0;
                    _runningDocumentTable = null;
                }
            }
        }
    }
}