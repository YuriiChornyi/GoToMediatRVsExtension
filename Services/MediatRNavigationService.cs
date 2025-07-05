using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.Shell;
using VSIXExtention.Models;

namespace VSIXExtention.Services
{
    public enum NavigationFailureReason
    {
        Success,
        FileNotFound,
        InvalidLocation,
        NavigationError
    }

    public class NavigationResult
    {
        public bool Success { get; set; }
        public NavigationFailureReason FailureReason { get; set; }
        public string ErrorMessage { get; set; }

        public static NavigationResult CreateSuccess() => new NavigationResult { Success = true, FailureReason = NavigationFailureReason.Success };
        public static NavigationResult CreateFailure(NavigationFailureReason reason, string message = null) => new NavigationResult { Success = false, FailureReason = reason, ErrorMessage = message };
    }

    public class MediatRNavigationService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly NavigationUiService _uiService;

        public MediatRNavigationService(IServiceProvider serviceProvider, NavigationUiService uiService)
        {
            _uiService = uiService;
            _serviceProvider = serviceProvider;
        }

        public async Task<bool> NavigateToHandlersAsync(List<MediatRHandlerInfo> handlers, bool isNotification)
        {
            if (!handlers.Any())
            {
                System.Diagnostics.Debug.WriteLine("MediatRNavigationExtension: MediatRNavigationService: No handlers to navigate to");
                return false;
            }

            if (handlers.Count == 1)
            {
                // Single handler - navigate directly
                var navigationResult = await NavigateToLocationAsync(handlers[0].Location);
                if (!navigationResult.Success)
                {
                    // If navigation failed due to file not found, clear cache for this request type
                    if (navigationResult.FailureReason == NavigationFailureReason.FileNotFound)
                    {
                        System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: MediatRNavigationService: File not found for handler, clearing cache for: {handlers[0].RequestTypeName}");
                        MediatRPatternMatcher.ClearCacheForRequestType(handlers[0].HandlerSymbol);
                    }

                    // Try to recover by showing error message
                    await _uiService.ShowErrorMessageAsync(
                        $"Could not navigate to handler '{handlers[0].HandlerTypeName}'.\n\n" +
                        "The handler may have been moved, renamed, or deleted. " +
                        "Try rebuilding your solution or check if the handler still exists.",
                        "MediatR Extension - Navigation Failed");
                }
                return navigationResult.Success;
            }

            // Multiple handlers - show selection dialog
            var result = await NavigateToMultipleHandlersAsync(handlers, isNotification);
            
            // Handle cancellation vs failure differently
            if (result == null) // Cancellation
            {
                return true; // Don't show error for cancellation
            }
            
            return result ?? false;
        }

        public async Task<NavigationResult> NavigateToLocationAsync(Location location)
        {
            if (location?.SourceTree?.FilePath == null)
            {
                System.Diagnostics.Debug.WriteLine("MediatRNavigationExtension: MediatRNavigationService: Location or FilePath is null");
                return NavigationResult.CreateFailure(NavigationFailureReason.InvalidLocation, "Location or FilePath is null");
            }

            var filePath = location.SourceTree.FilePath;
            var lineSpan = location.GetLineSpan();
            
            System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: MediatRNavigationService: Navigating to {filePath} at line {lineSpan.StartLinePosition.Line + 1}");
            
            // Check if file exists first
            if (!System.IO.File.Exists(filePath))
            {
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: MediatRNavigationService: File not found: {filePath}");
                return NavigationResult.CreateFailure(NavigationFailureReason.FileNotFound, $"File not found: {filePath}");
            }
            
            var success = await OpenDocumentAndNavigate(filePath, lineSpan.StartLinePosition);
            System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: MediatRNavigationService: Navigation result: {success}");
            
            if (success)
            {
                return NavigationResult.CreateSuccess();
            }
            else
            {
                return NavigationResult.CreateFailure(NavigationFailureReason.NavigationError, $"Failed to navigate to {filePath}");
            }
        }

        private async Task<bool?> NavigateToMultipleHandlersAsync(
            List<MediatRHandlerInfo> handlers, 
            bool isNotification)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // Check if we have mixed handler types (both request and notification)
                var requestHandlers = handlers.Where(h => !h.IsNotificationHandler).ToList();
                var notificationHandlers = handlers.Where(h => h.IsNotificationHandler).ToList();
                bool hasMixedTypes = requestHandlers.Any() && notificationHandlers.Any();

                var handlerDisplayInfo = handlers.Select(h => new HandlerDisplayInfo
                {
                    Handler = h,
                    DisplayText = FormatHandlerDisplayText(h, hasMixedTypes)
                }).ToArray();

                string message;
                if (hasMixedTypes)
                {
                    // Mixed types - provide detailed information
                    var requestTypeName = handlers.First().RequestTypeName;
                    message = $"Multiple handlers found for '{requestTypeName}':\n";
                    
                    if (requestHandlers.Any())
                    {
                        message += $"• {requestHandlers.Count} Request Handler(s)\n";
                    }
                    if (notificationHandlers.Any())
                    {
                        message += $"• {notificationHandlers.Count} Notification Handler(s)\n";
                    }
                    message += "\nPlease select one:";
                }
                else
                {
                    // Single type - standard message
                    string handlerType = isNotification ? "notification handler" : "handler";
                    message = $"Multiple {handlerType}s found. Please select one:";
                }

                var selectedHandlerName = _uiService.ShowHandlerSelectionDialog(handlerDisplayInfo, false);
                
                if (selectedHandlerName == null)
                {
                    return null; // User cancelled
                }

                var selectedHandler = handlerDisplayInfo.FirstOrDefault(hdi => hdi.DisplayText == selectedHandlerName)?.Handler;
                if (selectedHandler != null)
                {
                    var navigationResult = await NavigateToLocationAsync(selectedHandler.Location);
                    if (!navigationResult.Success)
                    {
                        // If navigation failed due to file not found, clear cache for this request type
                        if (navigationResult.FailureReason == NavigationFailureReason.FileNotFound)
                        {
                            System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: MediatRNavigationService: File not found for handler, clearing cache for: {selectedHandler.RequestTypeName}");
                            MediatRPatternMatcher.ClearCacheForRequestType(selectedHandler.HandlerSymbol);
                        }

                        await _uiService.ShowErrorMessageAsync(
                            $"Could not navigate to handler '{selectedHandler.HandlerTypeName}'.\n\n" +
                            "The handler may have been moved, renamed, or deleted. " +
                            "Try rebuilding your solution or check if the handler still exists.",
                            "MediatR Extension - Navigation Failed");
                    }
                    return navigationResult.Success;
                }

                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in NavigateToMultipleHandlersAsync: {ex.Message}");
                return false;
            }
        }

        private string FormatHandlerDisplayText(MediatRHandlerInfo handler, bool includePrefixes)
        {
            try
            {
                string prefix = "";
                if (includePrefixes)
                {
                    prefix = handler.IsNotificationHandler ? "[Notification] " : "[Request] ";
                }

                var filePath = handler.Location?.SourceTree?.FilePath;
                if (string.IsNullOrEmpty(filePath))
                {
                    return $"{prefix}{handler.HandlerTypeName}";
                }

                // Extract last 2-3 folders for context
                var pathParts = filePath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (pathParts.Length <= 2)
                {
                    return $"{prefix}{handler.HandlerTypeName} ({string.Join("/", pathParts)})";
                }

                var relevantParts = pathParts.Skip(Math.Max(0, pathParts.Length - 3)).ToArray();
                return $"{prefix}{handler.HandlerTypeName} ({string.Join("/", relevantParts)})";
            }
            catch
            {
                string prefix = "";
                if (includePrefixes)
                {
                    prefix = handler.IsNotificationHandler ? "[Notification] " : "[Request] ";
                }
                return $"{prefix}{handler.HandlerTypeName}";
            }
        }

        private async Task<bool> OpenDocumentAndNavigate(string filePath, Microsoft.CodeAnalysis.Text.LinePosition linePosition)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: MediatRNavigationService: Attempting to open file: {filePath} at line {linePosition.Line + 1}");

                var dte = _serviceProvider.GetService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                if (dte?.ItemOperations == null)
                {
                    System.Diagnostics.Debug.WriteLine("MediatRNavigationExtension: MediatRNavigationService: Failed to get DTE or ItemOperations");
                    return false;
                }

                var window = dte.ItemOperations.OpenFile(filePath);
                if (window?.Document?.Object("TextDocument") is EnvDTE.TextDocument textDocument)
                {
                    var selection = textDocument.Selection;
                    selection.MoveToLineAndOffset(linePosition.Line + 1, linePosition.Character + 1, false);
                    System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: MediatRNavigationService: Successfully navigated to {filePath} at line {linePosition.Line + 1}");
                    return true;
                }
                
                // Alternative approach if the above fails
                if (window?.Document != null)
                {
                    System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: MediatRNavigationService: Trying alternative approach for {filePath}");
                    var document = window.Document;
                    var altTextDocument = document.Object("TextDocument") as EnvDTE.TextDocument;
                    if (altTextDocument != null)
                    {
                        var selection = altTextDocument.Selection;
                        selection.MoveToLineAndOffset(linePosition.Line + 1, linePosition.Character + 1, false);
                        System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: MediatRNavigationService: Successfully navigated to {filePath} at line {linePosition.Line + 1} (alternative approach)");
                        return true;
                    }
                }

                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: MediatRNavigationService: Failed to get TextDocument from window for {filePath}");
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: MediatRNavigationService: Error opening document {filePath}: {ex.Message}");
                return false;
            }
        }
    }
} 