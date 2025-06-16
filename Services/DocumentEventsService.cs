using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Linq;
using VSIXExtention.DI;
using VSIXExtention.Helpers;
using VSIXExtention.Interfaces;

namespace VSIXExtention.Services
{
    public class DocumentEventsService : IDocumentEventService, IVsRunningDocTableEvents, IDisposable
    {
        private IVsRunningDocumentTable _runningDocumentTable;
        private IWorkspaceService _workspaceService;
        private IMediatRCacheService _cacheService;
        private uint _rdtCookie;
        private bool _initialized = false;
        private bool _disposed = false;

        public DocumentEventsService()
        {
        }

        public void Initialize()
        {
            if (_initialized)
                return;

            _runningDocumentTable = Package.GetGlobalService(typeof(SVsRunningDocumentTable)) as IVsRunningDocumentTable;
            _workspaceService = ServiceLocator.GetService<IWorkspaceService>();
            _cacheService = ServiceLocator.GetService<IMediatRCacheService>();

            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                if (_runningDocumentTable != null)
                {
                    var hr = _runningDocumentTable.AdviseRunningDocTableEvents(this, out _rdtCookie);
                    if (hr == VSConstants.S_OK)
                    {
                        _initialized = true;
                        System.Diagnostics.Debug.WriteLine("MediatRNavigationExtension: DocumentEventsService: Successfully subscribed to document save events");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: DocumentEventsService: Failed to subscribe to document events, HRESULT: {hr}");
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("MediatRNavigationExtension: DocumentEventsService: Could not get IVsRunningDocumentTable service");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: DocumentEventsService: Error during initialization: {ex.Message}");
            }
        }

        // IVsRunningDocTableEvents implementation
        public int OnAfterFirstDocumentLock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining)
        {
            return VSConstants.S_OK;
        }

        public int OnBeforeLastDocumentUnlock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining)
        {
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
                            Microsoft.VisualStudio.Threading.JoinableTask _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                            {
                                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: DocumentEventsService: Document saved: {docPath}");

                                var (semanticModel, typeSymbol) = await RoslynSymbolHelper.GetSymbolsFromFilePathAsync(docPath);

                                var mediatrHandlers = await MediatRPatternMatcher.FindAllHandlersForTypeSymbol(_workspaceService.GetWorkspace().CurrentSolution, typeSymbol, semanticModel);

                                // Get all request info to cache individual handler types
                                var allRequestInfo = MediatRPatternMatcher.GetAllRequestInfo(typeSymbol, semanticModel);

                                // Group handlers by type and cache them individually (if cache service is available)
                                if (_cacheService != null)
                                {
                                    foreach (var requestInfo in allRequestInfo)
                                    {
                                        var handlersForThisType = mediatrHandlers
                                            .Where(h => h.RequestTypeName == requestInfo.RequestTypeName
                                                && h.IsNotificationHandler == requestInfo.IsNotification)
                                            .ToList();

                                        System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: DocumentEventsService: Adding {handlersForThisType.Count} handlers to cache.");

                                        if (handlersForThisType.Any())
                                        {
                                            _cacheService.CacheHandlers(requestInfo.RequestTypeName, handlersForThisType);
                                        }
                                    }
                                }
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: DocumentEventsService: Error in OnAfterSave: {ex.Message}");
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
                        System.Diagnostics.Debug.WriteLine("MediatRNavigationExtension: DocumentEventsService: Unsubscribed from document save events");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: DocumentEventsService: Error during disposal: {ex.Message}");
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