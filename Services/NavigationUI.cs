using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using System;
using System.Threading.Tasks;
using VSIXExtention.Interfaces;

namespace VSIXExtention.Services
{
    public class NavigationUI : INavigationUIService
    {
        public string ShowHandlerSelectionDialog(HandlerDisplayInfo[] handlers, bool isNotification)
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                string handlerType = isNotification ? "notification handler" : "handler";
                string message = $"Multiple {handlerType}s found. Please select one:";

                var handlerNames = new string[handlers.Length];
                for (int i = 0; i < handlers.Length; i++)
                {
                    handlerNames[i] = handlers[i].DisplayText;
                }

                var dialog = new HandlerSelectionDialog(message, handlerNames);

                // Use ShowModal() for DialogWindow
                var result = dialog.ShowModal();
                if (result != true)
                {
                    return null; // User cancelled or dialog failed
                }

                return dialog.SelectedHandler;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: NavigationUI: Error showing handler selection dialog: {ex.Message}");
                return null;
            }
        }

        public async Task ShowErrorMessageAsync(string message, string title)
        {
            try
            {
                await VS.MessageBox.ShowErrorAsync(title, message);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: NavigationUI: Error showing message: {ex.Message}");

                // Fallback to console if UI fails
                System.Diagnostics.Debug.WriteLine($"ERROR - {title}: {message}");
            }
        }

        public async Task ShowInfoMessageAsync(string message, string title)
        {
            try
            {
                await VS.MessageBox.ShowAsync(title, message);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: NavigationUI: Error showing info message: {ex.Message}");
            }
        }

        public async Task ShowWarningMessageAsync(string message, string title)
        {
            try
            {
                await VS.MessageBox.ShowWarningAsync(title, message);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: NavigationUI: Error showing warning message: {ex.Message}");
            }
        }
    }
}