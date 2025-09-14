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
    public sealed class VSIXExtentionPackage : AsyncPackage
    {
        public const string PackageGuidString = "cf38f10f-fa64-4c4b-9ebc-6d7d897607ea";
        public static readonly Guid CommandSet = new Guid("cf38f10f-fa64-4c4b-9ebc-6d7d897607eb");
        public const int GoToMediatRImplementationCommandId = 0x0100;
        public const int GoToMediatRImplementationContextCommandId = 0x0102;
        public const int GoToMediatRUsageCommandId = 0x0103;
        public const int GoToMediatRUsageContextCommandId = 0x0104;

        private readonly MediatRCommandHandler _mediatRCommandHandler;
        private readonly MediatRContextService _mediatRContextService;
        private readonly WorkspaceService _workspaceService;

        public VSIXExtentionPackage()
        {
            _workspaceService = new WorkspaceService();
            _mediatRCommandHandler = new MediatRCommandHandler(_workspaceService);
            _mediatRContextService = new MediatRContextService(_workspaceService);
        }

        #region Package Members

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            System.Diagnostics.Debug.WriteLine("MediatRNavigationExtension: Package: Starting initialization...");

            // Switch to UI thread early to ensure thread-safe service acquisition
            await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            try
            {
                // Acquire services on UI thread for maximum compatibility
                var componentModel = await GetServiceAsync(typeof(SComponentModel)) as IComponentModel;
                if (componentModel != null)
                {
                    var vsWorkspace = componentModel.GetService<Microsoft.VisualStudio.LanguageServices.VisualStudioWorkspace>();
                    _workspaceService.SetWorkspace(vsWorkspace);
                    
                    System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Package: Workspace acquired: {vsWorkspace != null}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("MediatRNavigationExtension: Package: WARNING - ComponentModel not available, workspace will use lazy initialization");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Package: Error during workspace initialization: {ex.Message}");
                // Continue initialization - WorkspaceService will fall back to lazy initialization
            }

            // Register commands
            await RegisterCommandsAsync();

            // Call base initialization
            await base.InitializeAsync(cancellationToken, progress);

            System.Diagnostics.Debug.WriteLine("MediatRNavigationExtension: Package: Initialization complete");
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

                // Register "Go to Send/Publish" menu command
                var usageMenuCommandID = new CommandID(CommandSet, GoToMediatRUsageCommandId);
                var usageMenuItem = new OleMenuCommand((sender, e) =>
                {
                    Microsoft.VisualStudio.Threading.JoinableTask _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                    {
                        await ExecuteGoToUsage(sender, e);
                    });
                }, usageMenuCommandID);

                usageMenuItem.BeforeQueryStatus += (sender, e) =>
                {
                    var command = sender as OleMenuCommand;
                    if (command == null) return;

                    ThreadHelper.JoinableTaskFactory.Run(async () =>
                    {
                        await UsageMenuItem_BeforeQueryStatus(command);
                    });
                };

                commandService.AddCommand(usageMenuItem);

                // Register "Go to Send/Publish" context menu command
                var usageContextMenuCommandID = new CommandID(CommandSet, GoToMediatRUsageContextCommandId);
                var usageContextMenuItem = new OleMenuCommand((sender, e) =>
                {
                    Microsoft.VisualStudio.Threading.JoinableTask _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                    {
                        await ExecuteGoToUsage(sender, e);
                    });
                }, usageContextMenuCommandID);

                usageContextMenuItem.BeforeQueryStatus += (sender, e) =>
                {
                    var command = sender as OleMenuCommand;
                    if (command == null) return;

                    ThreadHelper.JoinableTaskFactory.Run(async () =>
                    {
                        await UsageMenuItem_BeforeQueryStatus(command);
                    });
                };

                commandService.AddCommand(usageContextMenuItem);

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

                // "Go to MediatR Implementation" should show when:
                // 1. On Request/Command/Query/Notification (original behavior)
                // 2. On nested MediatR call inside handler (new behavior for mixed context)
                // Performance optimization: check cheaper condition first
                bool isInRequestContext = await _mediatRContextService.IsInMediatRRequestContextAsync(textView);
                bool isInNestedCallContext = !isInRequestContext && await _mediatRContextService.IsInNestedMediatRCallContextAsync(textView);

                command.Visible = isInRequestContext || isInNestedCallContext;
                command.Enabled = isInRequestContext || isInNestedCallContext;
                command.Supported = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Package: Error checking MediatR request context: {ex.Message}");
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

                await _mediatRCommandHandler.ExecuteGoToImplementationAsync(textView, position);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Package: Error in ExecuteGoToImplementation: {ex.Message}");
                await ShowMessageAsync($"An error occurred: {ex.Message}", "MediatRNavigationExtension: Package Error");
            }
        }

        private async Task ExecuteGoToUsage(object sender, EventArgs e)
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

                await _mediatRCommandHandler.ExecuteGoToUsageAsync(textView, position);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Package: Error in ExecuteGoToUsage: {ex.Message}");
                await ShowMessageAsync($"An error occurred: {ex.Message}", "MediatRNavigationExtension: Package Error");
            }
        }

        private async Task UsageMenuItem_BeforeQueryStatus(OleMenuCommand command)
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

                // "Go to MediatR Send/Publish" should show when:
                // 1. On Handler class or Handle method (original behavior)
                // 2. On nested MediatR call inside handler class (new behavior for mixed context)
                // Performance optimization: check cheaper condition first
                bool isInHandlerContext = await _mediatRContextService.IsInMediatRHandlerContextAsync(textView);
                bool isInNestedCallContext = !isInHandlerContext && await _mediatRContextService.IsInNestedMediatRCallInHandlerContextAsync(textView);

                command.Visible = isInHandlerContext || isInNestedCallContext;
                command.Enabled = isInHandlerContext || isInNestedCallContext;
                command.Supported = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Package: Error checking MediatR handler context: {ex.Message}");
                command.Visible = false;
                command.Enabled = false;
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
                    _workspaceService?.Dispose();
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