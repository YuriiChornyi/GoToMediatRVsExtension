using System;
using System.ComponentModel.Design;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;

namespace VSIXExtention
{
    /// <summary>
    /// This is the class that implements the package exposed by this assembly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The minimum requirement for a class to be considered a valid package for Visual Studio
    /// is to implement the IVsPackage interface and register itself with the shell.
    /// This package uses the helper classes defined inside the Managed Package Framework (MPF)
    /// to do it: it derives from the Package class that provides the implementation of the
    /// IVsPackage interface and uses the registration attributes defined in the framework to
    /// register itself and its components with the shell. These attributes tell the pkgdef creation
    /// utility what data to put into .pkgdef file.
    /// </para>
    /// <para>
    /// To get loaded into VS, the package must be referred by &lt;Asset Type="Microsoft.VisualStudio.VsPackage" ...&gt; in .vsixmanifest file.
    /// </para>
    /// </remarks>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(VSIXExtentionPackage.PackageGuidString)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideAutoLoad(Microsoft.VisualStudio.Shell.Interop.UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(Microsoft.VisualStudio.Shell.Interop.UIContextGuids80.NoSolution, PackageAutoLoadFlags.BackgroundLoad)]
    public sealed class VSIXExtentionPackage : AsyncPackage
    {
        /// <summary>
        /// VSIXExtentionPackage GUID string.
        /// </summary>
        public const string PackageGuidString = "cf38f10f-fa64-4c4b-9ebc-6d7d897607ea";

        public static readonly Guid CommandSet = new Guid("cf38f10f-fa64-4c4b-9ebc-6d7d897607eb");
        public const int GoToMediatRImplementationCommandId = 0x0100;
        public const int GoToMediatRImplementationTestCommandId = 0x0101;
        public const int GoToMediatRImplementationContextCommandId = 0x0102;

        private MediatRGoToImplementationProvider _goToImplementationProvider;

        #region Package Members

        /// <summary>
        /// Initialization of the package; this method is called right after the package is sited, so this is the place
        /// where you can put all the initialization code that rely on services provided by VisualStudio.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to monitor for initialization cancellation, which can occur when VS is shutting down.</param>
        /// <param name="progress">A provider for progress updates.</param>
        /// <returns>A task representing the async work of package initialization, or an already completed task if there is none. Do not return null from this method.</returns>
        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            System.Diagnostics.Debug.WriteLine("MediatR Extension: Starting initialization...");

            // When initialized asynchronously, the current thread may be a background thread at this point.
            // Do any initialization that requires the UI thread after switching to the UI thread.
            await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            // Initialize the MediatR provider
            _goToImplementationProvider = new MediatRGoToImplementationProvider();

            // Register the command handler
            var commandService = await GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService != null)
            {
                var menuCommandID = new CommandID(CommandSet, GoToMediatRImplementationCommandId);
                var menuItem = new OleMenuCommand(ExecuteGoToImplementation, menuCommandID);

                // Set initial state
                menuItem.Visible = true;
                menuItem.Enabled = true;
                menuItem.Supported = true;

                menuItem.BeforeQueryStatus += MenuItem_BeforeQueryStatus;
                commandService.AddCommand(menuItem);

                // Register the context menu command
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

            System.Diagnostics.Debug.WriteLine("MediatR Extension: Initialization complete");
        }

        private async void MenuItem_BeforeQueryStatus(object sender, EventArgs e)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var command = sender as OleMenuCommand;
            if (command == null) return;

            try
            {
                // Check if we're currently on a MediatR request/command
                bool isMediatRContext = await IsInMediatRContextAsync();

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

        private async Task<bool> IsInMediatRContextAsync()
        {
            try
            {
                var textView = GetActiveTextView();
                if (textView == null) return false;

                var textBuffer = textView.TextBuffer;
                var filePath = GetFilePathFromTextBuffer(textBuffer);

                // Early bailout: Only check C# files
                if (string.IsNullOrEmpty(filePath) || !filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    return false;

                // Early bailout: Skip multiline selections (performance optimization)
                if (!textView.Selection.IsEmpty)
                {
                    var selectionSpan = textView.Selection.SelectedSpans[0];
                    var startLine = textView.TextSnapshot.GetLineFromPosition(selectionSpan.Start.Position);
                    var endLine = textView.TextSnapshot.GetLineFromPosition(selectionSpan.End.Position);

                    // If selection spans multiple lines, unlikely to be a specific class navigation
                    if (endLine.LineNumber > startLine.LineNumber)
                        return false;
                }

                // Try multiple ways to get the workspace
                VisualStudioWorkspace workspace = null;

                // Method 1: Through our service provider
                workspace = GetService(typeof(VisualStudioWorkspace)) as VisualStudioWorkspace;

                // Method 2: Through global service provider
                if (workspace == null)
                {
                    workspace = Package.GetGlobalService(typeof(VisualStudioWorkspace)) as VisualStudioWorkspace;
                }

                // Method 3: Through component model
                if (workspace == null)
                {
                    try
                    {
                        var componentModel = GetService(typeof(SComponentModel)) as IComponentModel;
                        if (componentModel != null)
                        {
                            workspace = componentModel.GetService<VisualStudioWorkspace>();
                        }
                    }
                    catch
                    {
                        // Ignore component model failures
                    }
                }

                if (workspace?.CurrentSolution == null) return false;

                var documentIds = workspace.CurrentSolution.GetDocumentIdsWithFilePath(filePath);
                var documentId = documentIds.FirstOrDefault();
                if (documentId == null) return false;

                var document = workspace.CurrentSolution.GetDocument(documentId);
                if (document == null) return false;

                var syntaxTree = await document.GetSyntaxTreeAsync();
                if (syntaxTree == null) return false;

                // Check if there's a selection first, then fall back to caret position
                Microsoft.CodeAnalysis.Text.TextSpan textSpan;
                if (!textView.Selection.IsEmpty)
                {
                    // Use the selection span
                    var selectionSpan = textView.Selection.SelectedSpans[0];
                    textSpan = new Microsoft.CodeAnalysis.Text.TextSpan(selectionSpan.Start.Position, selectionSpan.Length);
                }
                else
                {
                    // Use caret position
                    var caretPosition = textView.Caret.Position.BufferPosition.Position;
                    textSpan = new Microsoft.CodeAnalysis.Text.TextSpan(caretPosition, 0);
                }

                var root = await syntaxTree.GetRootAsync();
                var node = root.FindNode(textSpan);

                // Early bailout: Quick syntax check - if we're not in/on a class, skip expensive semantic analysis
                var classDeclaration = node.FirstAncestorOrSelf<ClassDeclarationSyntax>();
                if (classDeclaration == null)
                {
                    // Check if we're on an identifier that might reference a class
                    var identifierName = node as IdentifierNameSyntax ?? node.FirstAncestorOrSelf<IdentifierNameSyntax>();
                    if (identifierName == null)
                        return false; // Not on a class or identifier, definitely not MediatR
                }

                // Now do the expensive semantic analysis only if we passed the quick checks
                var semanticModel = await document.GetSemanticModelAsync();
                if (semanticModel == null) return false;

                // Check if we're on a class that implements IRequest
                if (classDeclaration != null)
                {
                    var typeSymbol = semanticModel.GetDeclaredSymbol(classDeclaration) as INamedTypeSymbol;
                    if (typeSymbol != null)
                    {
                        bool isMediatRRequest = MediatRPatternMatcher.IsMediatRRequest(typeSymbol, semanticModel);
                        if (isMediatRRequest)
                        {
                            return true;
                        }
                    }
                }

                // Check if we're on an identifier that references a MediatR request
                var identifierName2 = node as IdentifierNameSyntax ?? node.FirstAncestorOrSelf<IdentifierNameSyntax>();
                if (identifierName2 != null)
                {
                    var symbolInfo = semanticModel.GetSymbolInfo(identifierName2);
                    if (symbolInfo.Symbol is INamedTypeSymbol namedTypeSymbol)
                    {
                        bool isMediatRRequest = MediatRPatternMatcher.IsMediatRRequest(namedTypeSymbol, semanticModel);
                        return isMediatRRequest;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatR Extension: Error in IsInMediatRContextAsync: {ex.Message}");
                return false;
            }
        }

        private string GetFilePathFromTextBuffer(Microsoft.VisualStudio.Text.ITextBuffer textBuffer)
        {
            try
            {
                if (textBuffer.Properties.TryGetProperty<Microsoft.VisualStudio.TextManager.Interop.IVsTextBuffer>(typeof(Microsoft.VisualStudio.TextManager.Interop.IVsTextBuffer), out var vsTextBuffer))
                {
                    ThreadHelper.ThrowIfNotOnUIThread();

                    var persistFileFormat = vsTextBuffer as IPersistFileFormat;

                    if (persistFileFormat != null)
                    {
                        persistFileFormat.GetCurFile(out var filePath, out _);
                        return filePath;
                    }
                }

                // Alternative approach using document properties
                textBuffer.Properties.TryGetProperty<Microsoft.VisualStudio.Text.ITextDocument>(typeof(Microsoft.VisualStudio.Text.ITextDocument), out Microsoft.VisualStudio.Text.ITextDocument textDocument);
                return textDocument?.FilePath;
            }
            catch
            {
                return null;
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

                // Check if there's a selection first, then fall back to caret position
                int position;
                if (!textView.Selection.IsEmpty)
                {
                    // Use the start of the selection
                    position = textView.Selection.SelectedSpans[0].Start.Position;
                }
                else
                {
                    // Use caret position
                    position = textView.Caret.Position.BufferPosition.Position;
                }

                bool success = await _goToImplementationProvider.TryGoToImplementationAsync(textView, position);

                if (!success)
                {
                    await ShowMessageAsync(
                        "Could not find MediatR handler for the current request/command.\n\n" +
                        "Make sure:\n" +
                        "• You're positioned on a MediatR IRequest implementation\n" +
                        "• The corresponding IRequestHandler exists in the solution\n" +
                        "• The solution is compiled without errors",
                        "MediatR Extension");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatR Extension: Error in ExecuteGoToImplementation: {ex.Message}");
                await ShowMessageAsync($"An error occurred: {ex.Message}", "MediatR Extension Error");
            }
        }

        private async Task ShowMessageAsync(string message, string title)
        {
            await VS.MessageBox.ShowAsync(
                message,
                title);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    // Dispose the MediatR provider to save cache
                    _goToImplementationProvider?.Dispose();
                    System.Diagnostics.Debug.WriteLine("MediatR Extension: Package disposed, provider disposed");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"MediatR Extension: Error disposing provider: {ex.Message}");
                }
            }

            base.Dispose(disposing);
        }

        #endregion
    }
}
