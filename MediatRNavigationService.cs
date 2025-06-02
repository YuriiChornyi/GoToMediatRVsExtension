using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.VisualStudio.ComponentModelHost;

namespace VSIXExtention
{
    public class MediatRNavigationService
    {
        private readonly IServiceProvider _serviceProvider;

        public MediatRNavigationService(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public async Task<bool> TryNavigateToHandlerAsync(INamedTypeSymbol requestTypeSymbol)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // Use the same workspace detection logic as the other components
                VisualStudioWorkspace workspace = null;
                
                // Method 1: Through service provider
                workspace = _serviceProvider.GetService(typeof(VisualStudioWorkspace)) as VisualStudioWorkspace;
                
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
                        var componentModel = _serviceProvider.GetService(typeof(SComponentModel)) as IComponentModel;
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

                if (workspace?.CurrentSolution == null)
                {
                    return false;
                }

                var requestTypeName = requestTypeSymbol.Name;
                
                // Check if this is a notification or request
                var requestInfo = MediatRPatternMatcher.GetRequestInfo(requestTypeSymbol, null);
                if (requestInfo == null)
                {
                    return false;
                }

                var handlers = requestInfo.IsNotification 
                    ? await MediatRPatternMatcher.FindNotificationHandlersInSolution(workspace.CurrentSolution, requestTypeName)
                    : await MediatRPatternMatcher.FindHandlersInSolution(workspace.CurrentSolution, requestTypeName);

                if (handlers.Any())
                {
                    if (handlers.Count == 1)
                    {
                        // Single handler found, navigate to it
                        var handler = handlers.First();
                        return await NavigateToLocationAsync(handler.Location);
                    }
                    else
                    {
                        // Multiple handlers found (common for notifications), show selection dialog
                        return await NavigateToMultipleHandlersAsync(handlers, requestInfo.IsNotification);
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                // Log error or show message
                System.Diagnostics.Debug.WriteLine($"MediatR Navigation: Error navigating to handler: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> NavigateToMultipleHandlersAsync(System.Collections.Generic.List<MediatRPatternMatcher.MediatRHandlerInfo> handlers, bool isNotification)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                var handlerNames = handlers.Select(h => h.HandlerTypeName).ToArray();
                var selectedHandler = ShowHandlerSelectionDialog(handlerNames, isNotification);

                if (!string.IsNullOrEmpty(selectedHandler))
                {
                    var handler = handlers.FirstOrDefault(h => h.HandlerTypeName == selectedHandler);
                    if (handler != null)
                    {
                        return await NavigateToLocationAsync(handler.Location);
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in NavigateToMultipleHandlersAsync: {ex.Message}");
                return false;
            }
        }

        private string ShowHandlerSelectionDialog(string[] handlerNames, bool isNotification)
        {
            try
            {
                var handlerType = isNotification ? "notification handlers" : "request handlers";
                var message = $"Multiple {handlerType} found. Select one to navigate to:";
                
                var dialog = new HandlerSelectionDialog(message, handlerNames);
                var result = dialog.ShowDialog();
                
                return result == System.Windows.Forms.DialogResult.OK ? dialog.SelectedHandler : null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error showing selection dialog: {ex.Message}");
                // Fallback: just return the first handler
                return handlerNames.FirstOrDefault();
            }
        }

        private async Task<bool> NavigateToLocationAsync(Location location)
        {
            if (location?.SourceTree == null)
                return false;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                var filePath = location.SourceTree.FilePath;
                var linePosition = location.GetLineSpan().StartLinePosition;

                // Get the VsShell service to open the document
                var shell = (IVsUIShellOpenDocument)_serviceProvider.GetService(typeof(SVsUIShellOpenDocument));
                if (shell == null)
                    return false;

                // Open the document
                var hr = shell.OpenDocumentViaProject(
                    filePath,
                    VSConstants.LOGVIEWID_Code,
                    out _,
                    out _,
                    out _,
                    out var windowFrame);

                if (hr != VSConstants.S_OK || windowFrame == null)
                    return false;

                // Show the window
                windowFrame.Show();

                // Get the text view to position the cursor
                hr = windowFrame.GetProperty((int)__VSFPROPID.VSFPROPID_DocData, out var docData);
                if (hr == VSConstants.S_OK && docData is IVsTextBuffer textBuffer)
                {
                    // Get the text manager to navigate to the specific line
                    var textManager = (IVsTextManager)_serviceProvider.GetService(typeof(SVsTextManager));
                    if (textManager != null)
                    {
                        textManager.NavigateToLineAndColumn(
                            textBuffer,
                            VSConstants.LOGVIEWID_Code,
                            linePosition.Line,
                            linePosition.Character,
                            linePosition.Line,
                            linePosition.Character);
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error opening document: {ex.Message}");
                return false;
            }
        }
    }

    // Simple dialog for selecting among multiple handlers
    public class HandlerSelectionDialog : System.Windows.Forms.Form
    {
        public string SelectedHandler { get; private set; }

        public HandlerSelectionDialog(string message, string[] handlerNames)
        {
            InitializeDialog(message, handlerNames);
        }

        private void InitializeDialog(string message, string[] handlerNames)
        {
            Text = "Select MediatR Handler";
            Size = new System.Drawing.Size(400, 300);
            StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;

            var label = new System.Windows.Forms.Label
            {
                Text = message,
                Location = new System.Drawing.Point(10, 10),
                Size = new System.Drawing.Size(370, 30),
                AutoSize = false
            };

            var listBox = new System.Windows.Forms.ListBox
            {
                Location = new System.Drawing.Point(10, 50),
                Size = new System.Drawing.Size(370, 150)
            };
            listBox.Items.AddRange(handlerNames);
            listBox.SelectedIndex = 0;

            var okButton = new System.Windows.Forms.Button
            {
                Text = "OK",
                Location = new System.Drawing.Point(225, 220),
                Size = new System.Drawing.Size(75, 23),
                DialogResult = System.Windows.Forms.DialogResult.OK
            };

            var cancelButton = new System.Windows.Forms.Button
            {
                Text = "Cancel",
                Location = new System.Drawing.Point(305, 220),
                Size = new System.Drawing.Size(75, 23),
                DialogResult = System.Windows.Forms.DialogResult.Cancel
            };

            okButton.Click += (s, e) => {
                SelectedHandler = listBox.SelectedItem?.ToString();
                DialogResult = System.Windows.Forms.DialogResult.OK;
                Close();
            };

            listBox.DoubleClick += (s, e) => {
                SelectedHandler = listBox.SelectedItem?.ToString();
                DialogResult = System.Windows.Forms.DialogResult.OK;
                Close();
            };

            Controls.Add(label);
            Controls.Add(listBox);
            Controls.Add(okButton);
            Controls.Add(cancelButton);

            AcceptButton = okButton;
            CancelButton = cancelButton;
        }
    }
} 