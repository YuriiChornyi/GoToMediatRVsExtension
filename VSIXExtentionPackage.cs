using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using VSIXExtention.DI;
using VSIXExtention.Interfaces;
using VSIXExtention.Services;

namespace VSIXExtention
{
    /// <summary>
    /// MediatR VS Extension Package with clean architecture
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(VSIXExtentionPackage.PackageGuidString)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideAutoLoad(Microsoft.VisualStudio.Shell.Interop.UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(Microsoft.VisualStudio.Shell.Interop.UIContextGuids80.NoSolution, PackageAutoLoadFlags.BackgroundLoad)]
    public sealed class VSIXExtentionPackage : AsyncPackage
    {
        public const string PackageGuidString = "cf38f10f-fa64-4c4b-9ebc-6d7d897607ea";
        public static readonly Guid CommandSet = new Guid("cf38f10f-fa64-4c4b-9ebc-6d7d897607eb");
        public const int GoToMediatRImplementationCommandId = 0x0100;
        public const int GoToMediatRImplementationContextCommandId = 0x0102;

        private ExtensionServiceContainer _serviceContainer;

        #region Package Members

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            System.Diagnostics.Debug.WriteLine("MediatRNavigationExtension: Package: Starting initialization...");

            await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            // Initialize dependency injection container
            await InitializeServicesAsync();

            // Register commands
            await RegisterCommandsAsync();

            await base.InitializeAsync(cancellationToken, progress);

            System.Diagnostics.Debug.WriteLine("MediatRNavigationExtension: Package: Initialization complete");
        }

        private async Task InitializeServicesAsync()
        {
            _serviceContainer = new ExtensionServiceContainer();

            // Register all MediatR services with their dependencies
            _serviceContainer.RegisterMediatRServices(this);

            // Initialize the service locator
            ServiceLocator.Initialize(_serviceContainer);

            // DO NOT register solution services here - they will be registered when a solution opens
            // _serviceContainer.RegisterSolutionServices();

            // Create and initialize solution context manager - this will handle solution events
            var solutionContextManager = new SolutionContextManager();
            
            // Register the solution context manager as a singleton so it can be disposed properly
            _serviceContainer.RegisterInstance<SolutionContextManager>(solutionContextManager);

            await solutionContextManager.InitializeAsync();

            System.Diagnostics.Debug.WriteLine("MediatRNavigationExtension: Package: Services initialized");
        }

        private async Task RegisterCommandsAsync()
        {
            var commandService = await GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService != null)
            {
                // Register main menu command
                var menuCommandID = new CommandID(CommandSet, GoToMediatRImplementationCommandId);
                var menuItem = new OleMenuCommand((sender, e) =>
                {
                    Microsoft.VisualStudio.Threading.JoinableTask _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                    {
                        await ExecuteGoToImplementation(sender, e);
                    });
                }, menuCommandID);

                menuItem.BeforeQueryStatus += (sender, e) =>
                {
                    var command = sender as OleMenuCommand;
                    if (command == null) return;

                    ThreadHelper.JoinableTaskFactory.Run(async () =>
                    {
                        await MenuItem_BeforeQueryStatus(command);
                    });
                };

                commandService.AddCommand(menuItem);

                // Register context menu command
                var contextMenuCommandID = new CommandID(CommandSet, GoToMediatRImplementationContextCommandId);

                var contextMenuItem = new OleMenuCommand((sender, e) =>
                {
                    Microsoft.VisualStudio.Threading.JoinableTask _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                    {
                        await ExecuteGoToImplementation(sender, e);
                    });
                }, contextMenuCommandID);

                contextMenuItem.BeforeQueryStatus += (sender, e) =>
                {
                    var command = sender as OleMenuCommand;
                    if (command == null) return;

                    ThreadHelper.JoinableTaskFactory.Run(async () =>
                    {
                        await MenuItem_BeforeQueryStatus(command);
                    });
                };

                commandService.AddCommand(contextMenuItem);

                System.Diagnostics.Debug.WriteLine("MediatRNavigationExtension: Package: Commands registered successfully");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("MediatRNavigationExtension: Package: ERROR - Command service is null!");
            }
        }

        private async Task MenuItem_BeforeQueryStatus(OleMenuCommand command)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                var textView = GetActiveTextView();
                if (textView == null)
                {
                    command.Visible = false;
                    command.Enabled = false;
                    return;
                }

                var contextService = _serviceContainer.GetService<IMediatRContextService>();
                bool isMediatRContext = await contextService.IsInMediatRContextAsync(textView);

                command.Visible = isMediatRContext;
                command.Enabled = isMediatRContext;
                command.Supported = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Package: Error checking MediatR context: {ex.Message}");
                command.Visible = false;
                command.Enabled = false;
            }
        }

        private async Task ExecuteGoToImplementation(object sender, EventArgs e)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                var textView = GetActiveTextView();
                if (textView == null)
                {
                    await ShowMessageAsync("No active text view found.", "MediatRNavigationExtension: Package");
                    return;
                }

                int position = GetCaretOrSelectionPosition(textView);

                var commandHandler = _serviceContainer.GetService<IMediatRCommandHandler>();
                bool success = await commandHandler.ExecuteGoToImplementationAsync(textView, position);

                // Error messages are handled within the command handler
                // This keeps the package class clean and focused
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Package: Error in ExecuteGoToImplementation: {ex.Message}");
                await ShowMessageAsync($"An error occurred: {ex.Message}", "MediatRNavigationExtension: Package Error");
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
                    // Dispose solution context manager first (which will clean up solution services)
                    var solutionContextManager = _serviceContainer?.TryGetService<SolutionContextManager>();
                    solutionContextManager?.Dispose();

                    // Dispose service locator and containers
                    ServiceLocator.Dispose();
                    _serviceContainer?.Dispose();
                    _serviceContainer = null;
                    
                    System.Diagnostics.Debug.WriteLine("MediatRNavigationExtension: Package: Package disposed successfully");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Package: Error disposing package: {ex.Message}");
                }
            }

            base.Dispose(disposing);
        }

        #endregion
    }
}