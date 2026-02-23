using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Threading.Tasks;
using VSIXExtension.Models;

namespace VSIXExtension.Services
{
    public interface IProgress : IDisposable
    {
        void Report(double value, string message);
    }

    public class ProgressReporter : IProgress
    {
        private readonly IVsStatusbar _statusBar;
        private uint _cookie;
        private bool _disposed = false;

        public ProgressReporter(IVsStatusbar statusBar, string title)
        {
            _statusBar = statusBar;
            ThreadHelper.ThrowIfNotOnUIThread();
            _statusBar.Progress(ref _cookie, 1, title, 0, 0);
        }

        public void Report(double value, string message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var progress = (uint)(value * 100);
            _statusBar.Progress(ref _cookie, 1, message, progress, 100);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                _statusBar.Progress(ref _cookie, 0, "", 0, 0);
                _disposed = true;
            }
        }
    }

    public class NavigationUiService
    {
        public string ShowHandlerSelectionDialog(HandlerDisplayInfo[] handlers, string message)
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                var handlerNames = new string[handlers.Length];
                for (int i = 0; i < handlers.Length; i++)
                {
                    handlerNames[i] = handlers[i].DisplayText;
                }

                var dialog = new HandlerSelectionDialog("Select MediatR Handler", message, handlerNames);

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

        public string ShowUsageSelectionDialog(UsageDisplayInfo[] usages, string message)
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                var usageNames = new string[usages.Length];
                for (int i = 0; i < usages.Length; i++)
                {
                    usageNames[i] = usages[i].DisplayText;
                }

                var dialog = new HandlerSelectionDialog("Select Usage Location", message, usageNames);

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
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: NavigationUI: Error showing usage selection dialog: {ex.Message}");
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

        public async Task<IProgress> ShowProgressAsync(string title, string initialMessage)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var statusBar = await VS.Services.GetStatusBarAsync();
            var vsStatusBar = statusBar as IVsStatusbar;

            if (vsStatusBar != null)
            {
                return new ProgressReporter(vsStatusBar, title);
            }

            // Fallback to a no-op progress reporter
            return new NoOpProgressReporter();
        }

        private class NoOpProgressReporter : IProgress, IDisposable
        {
            public void Report(double value, string message) { }
            public void Dispose() { }
        }

        public async Task ShowErrorMessageWithActionsAsync(string message, string title, Action[] actions)
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
    }
}