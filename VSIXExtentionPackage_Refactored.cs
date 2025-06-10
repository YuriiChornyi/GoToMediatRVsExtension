using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using VSIXExtention.Interfaces;
using VSIXExtention.Services;

namespace VSIXExtention
{
    /// <summary>
    /// Refactored MediatR VS Extension Package with clean architecture
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(VSIXExtentionPackage.PackageGuidString)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideAutoLoad(Microsoft.VisualStudio.Shell.Interop.UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(Microsoft.VisualStudio.Shell.Interop.UIContextGuids80.NoSolution, PackageAutoLoadFlags.BackgroundLoad)]
    public sealed class VSIXExtentionPackageRefactored : AsyncPackage
    {
        public const string PackageGuidString = "cf38f10f-fa64-4c4b-9ebc-6d7d897607ea";
        public static readonly Guid CommandSet = new Guid("cf38f10f-fa64-4c4b-9ebc-6d7d897607eb");
        public const int GoToMediatRImplementationCommandId = 0x0100;
        public const int GoToMediatRImplementationContextCommandId = 0x0102;

        private ServiceContainer _serviceContainer;

        #region Package Members

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            System.Diagnostics.Debug.WriteLine("MediatR Extension: Starting initialization...");

            await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            // Initialize dependency injection container
            await InitializeServicesAsync();

            // Register commands
            await RegisterCommandsAsync();

            System.Diagnostics.Debug.WriteLine("MediatR Extension: Initialization complete");
        }

        private async Task InitializeServicesAsync()
        {
            _serviceContainer = new ServiceContainer();

            // Register services with dependency injection
            _serviceContainer.RegisterSingleton<IWorkspaceService, WorkspaceService>(
                container => new WorkspaceService(this));

            _serviceContainer.RegisterSingleton<IMediatRContextService, MediatRContextService>();
            
            // Note: You'll need to implement these services based on your existing code
            _serviceContainer.RegisterSingleton<IMediatRCacheService, MediatRCacheServiceRefactored>();
            _serviceContainer.RegisterSingleton<IMediatRHandlerFinder, MediatRHandlerFinderService>();
            _serviceContainer.RegisterSingleton<IMediatRNavigationService, MediatRNavigationServiceRefactored>();
            _serviceContainer.RegisterSingleton<INavigationUIService, NavigationUIService>();
            _serviceContainer.RegisterSingleton<IDocumentEventService, DocumentEventService>();
            
            _serviceContainer.RegisterSingleton<IMediatRCommandHandler, MediatRCommandHandler>();

            // Initialize the service locator
            ServiceLocator.Initialize(_serviceContainer);

            // Initialize document event service
            var documentEventService = _serviceContainer.GetService<IDocumentEventService>();
            documentEventService.Initialize();

            System.Diagnostics.Debug.WriteLine("MediatR Extension: Services initialized");
        }

        private async Task RegisterCommandsAsync()
        {
            var commandService = await GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService != null)
            {
                // Register main menu command
                var menuCommandID = new CommandID(CommandSet, GoToMediatRImplementationCommandId);
                var menuItem = new OleMenuCommand(ExecuteGoToImplementation, menuCommandID);
                menuItem.BeforeQueryStatus += MenuItem_BeforeQueryStatus;
                commandService.AddCommand(menuItem);

                // Register context menu command
                var contextMenuCommandID = new CommandID(CommandSet, GoToMediatRImplementationContextCommandId);
                var contextMenuItem = new OleMenuCommand(ExecuteGoToImplementation, contextMenuCommandID);
                contextMenuItem.BeforeQueryStatus += MenuItem_BeforeQueryStatus;
                commandService.AddCommand(contextMenuItem);

                System.Diagnostics.Debug.WriteLine("MediatR Extension: Commands registered successfully");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("MediatR Extension: ERROR - Command service is null!");
            }
        }

        private async void MenuItem_BeforeQueryStatus(object sender, EventArgs e)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var command = sender as OleMenuCommand;
            if (command == null) return;

            try
            {
                var textView = GetActiveTextView();
                if (textView == null)
                {
                    command.Visible = false;
                    command.Enabled = false;
                    return;
                }

                var contextService = ServiceLocator.GetService<IMediatRContextService>();
                bool isMediatRContext = await contextService.IsInMediatRContextAsync(textView);

                command.Visible = isMediatRContext;
                command.Enabled = isMediatRContext;
                command.Supported = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatR Extension: Error checking MediatR context: {ex.Message}");
                command.Visible = false;
                command.Enabled = false;
            }
        }

        private async void ExecuteGoToImplementation(object sender, EventArgs e)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                var textView = GetActiveTextView();
                if (textView == null)
                {
                    await ShowMessageAsync("No active text view found.", "MediatR Extension");
                    return;
                }

                int position = GetCaretOrSelectionPosition(textView);
                
                var commandHandler = ServiceLocator.GetService<IMediatRCommandHandler>();
                bool success = await commandHandler.ExecuteGoToImplementationAsync(textView, position);

                // Error messages are handled within the command handler
                // This keeps the package class clean and focused
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatR Extension: Error in ExecuteGoToImplementation: {ex.Message}");
                await ShowMessageAsync($"An error occurred: {ex.Message}", "MediatR Extension Error");
            }
        }

        private IWpfTextView GetActiveTextView()
        {
            try
            {
                var textManager = GetService(typeof(SVsTextManager)) as IVsTextManager2;
                if (textManager == null) return null;

                textManager.GetActiveView2(1, null, (uint)_VIEWFRAMETYPE.vftCodeWindow, out var view);
                if (view == null) return null;

                var componentModel = GetService(typeof(SComponentModel)) as IComponentModel;
                var editorAdapter = componentModel?.GetService<IVsEditorAdaptersFactoryService>();

                return editorAdapter?.GetWpfTextView(view);
            }
            catch
            {
                return null;
            }
        }

        private int GetCaretOrSelectionPosition(ITextView textView)
        {
            if (!textView.Selection.IsEmpty)
            {
                return textView.Selection.SelectedSpans[0].Start.Position;
            }
            
            return textView.Caret.Position.BufferPosition.Position;
        }

        private async Task ShowMessageAsync(string message, string title)
        {
            await VS.MessageBox.ShowAsync(message, title);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    // Dispose service container which will dispose all services
                    ServiceLocator.Dispose();
                    _serviceContainer?.Dispose();
                    System.Diagnostics.Debug.WriteLine("MediatR Extension: Package disposed successfully");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"MediatR Extension: Error disposing package: {ex.Message}");
                }
            }

            base.Dispose(disposing);
        }

        #endregion
    }
} 