using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Threading.Tasks;
using VSIXExtention.DI;
using VSIXExtention.Interfaces;
using IWorkspaceService = VSIXExtention.Interfaces.IWorkspaceService;

namespace VSIXExtention.Services
{
    public class SolutionContextManager : IVsSolutionEvents, IDisposable
    {
        private ExtensionServiceContainer _solutionContainer;
        private IVsSolution _solution;
        private uint _solutionEventsCookie;

        private IMediatRCacheService _cacheService;
        private IWorkspaceService _workspaceService;
        private IDocumentEventService _documentEventsService;

        public async Task InitializeAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            _solution = await ServiceProvider.GetGlobalServiceAsync(typeof(SVsSolution)) as IVsSolution;
            _solution?.AdviseSolutionEvents(this, out _solutionEventsCookie);
        }

        public int OnAfterOpenSolution(object pUnkReserved, int fNewSolution)
        {
            System.Diagnostics.Debug.WriteLine("MediatRNavigationExtension: SolutionContextManager: Solution opening: initializing solution-scoped services");

            ThreadHelper.ThrowIfNotOnUIThread();
            
            Microsoft.VisualStudio.Threading.JoinableTask _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await Task.Delay(5000); // wait 5 sec on sollution open for workspace and Roslyn services to init

                try
                {
                    // Create solution-scoped container
                    _solutionContainer = new ExtensionServiceContainer("Solution");

                    // Register solution-scoped services
                    _solutionContainer.RegisterSolutionScoped<IWorkspaceService, WorkspaceService>();
                    _solutionContainer.RegisterSolutionScoped<IMediatRCacheService, MediatRCacheService>();
                    _solutionContainer.RegisterSolutionScoped<IDocumentEventService, DocumentEventsService>();

                    // Initialize the solution container in ServiceLocator
                    ServiceLocator.InitializeSolutionContainer(_solutionContainer);

                    // Get and initialize services
                    _workspaceService = ServiceLocator.GetService<IWorkspaceService>();
                    _cacheService = ServiceLocator.GetService<IMediatRCacheService>();
                    _documentEventsService = ServiceLocator.GetService<IDocumentEventService>();

                    // Initialize services in proper order
                    _workspaceService.InitializeWorkspace();
                    
                    var workspace = _workspaceService.GetWorkspace();
                    if (workspace?.CurrentSolution != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: SolutionContextManager: Solution loaded - FilePath: {workspace.CurrentSolution.FilePath ?? "null"}, ID: {workspace.CurrentSolution.Id}");
                        await _cacheService.InitializeAsync(workspace.CurrentSolution);
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("MediatRNavigationExtension: SolutionContextManager: Workspace or CurrentSolution is null, skipping cache initialization");
                        
                        // Still try to initialize cache with a null solution to create minimal functionality
                        try
                        {
                            await _cacheService.InitializeAsync(null);
                        }
                        catch (Exception cacheEx)
                        {
                            System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: SolutionContextManager: Failed to initialize cache with null solution: {cacheEx.Message}");
                        }
                    }
                    
                    _documentEventsService.Initialize();

                    System.Diagnostics.Debug.WriteLine("MediatRNavigationExtension: SolutionContextManager: Solution-scoped services initialized successfully");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: SolutionContextManager: Error initializing solution services: {ex.Message}");
                }
            });

            return VSConstants.S_OK;
        }

        public int OnAfterCloseSolution(object pUnkReserved)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            System.Diagnostics.Debug.WriteLine("MediatRNavigationExtension: SolutionContextManager: Solution closing: cleaning up solution-scoped services");

            Microsoft.VisualStudio.Threading.JoinableTask _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    // Unhook event handlers
                    if (_documentEventsService != null)
                    {
                        _documentEventsService.Dispose();
                    }

                    // Save cache before disposing
                    if (_cacheService != null)
                    {
                        await _cacheService.SaveCacheAsync();
                        // Clear cache after saving to prevent stale data in next solution
                        _cacheService.ClearCache();
                    }

                    // Clear solution services from ServiceLocator
                    ServiceLocator.ClearSolutionServices();

                    // Dispose solution container (this will dispose all solution-scoped services)
                    _solutionContainer?.Dispose();
                    _solutionContainer = null;

                    // Clear references
                    _workspaceService = null;
                    _cacheService = null;
                    _documentEventsService = null;

                    System.Diagnostics.Debug.WriteLine("MediatRNavigationExtension: SolutionContextManager: Solution-scoped services cleaned up successfully");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: SolutionContextManager: Error cleaning up solution services: {ex.Message}");
                }
            });

            return VSConstants.S_OK;
        }

        // Required by IVsSolutionEvents but not used - just return S_OK
        public int OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded) => VSConstants.S_OK;
        public int OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel) => VSConstants.S_OK;
        public int OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved) => VSConstants.S_OK;
        public int OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy) => VSConstants.S_OK;
        public int OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel) => VSConstants.S_OK;
        public int OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy) => VSConstants.S_OK;
        public int OnQueryCloseSolution(object pUnkReserved, ref int pfCancel) => VSConstants.S_OK;
        public int OnBeforeCloseSolution(object pUnkReserved) => VSConstants.S_OK;

        public void Dispose()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _solution?.UnadviseSolutionEvents(_solutionEventsCookie);
            _solutionContainer.Dispose();
        }
    }
}
