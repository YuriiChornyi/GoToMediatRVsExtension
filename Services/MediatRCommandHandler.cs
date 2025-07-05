using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Host;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;

namespace VSIXExtention.Services
{
    public class MediatRCommandHandler 
    {
        private readonly MediatRContextService _contextService;
        private readonly NavigationUiService _uiService;
        private readonly WorkspaceService _workspaceService;
        private readonly MediatRHandlerFinder _handlerFinder;
        private readonly MediatRNavigationService _navigationService;

        public MediatRCommandHandler(WorkspaceService workspaceService)
        {
            _workspaceService = workspaceService;
            _contextService = new MediatRContextService(workspaceService);
            _uiService = new NavigationUiService();
            _handlerFinder = new MediatRHandlerFinder(workspaceService);
            _navigationService = new MediatRNavigationService(ServiceProvider.GlobalProvider, _uiService);
        }

        public async Task<bool> ExecuteGoToImplementationAsync(ITextView textView, int position)
        {
            try
            {
                // Show progress for long operations
                using (var progress = await _uiService.ShowProgressAsync("Searching for MediatR handlers...", "Finding handlers in solution"))
                {
                    progress.Report(0.1, "Analyzing current position...");
                    
                    // Step 1: Get the MediatR type symbol
                    var typeSymbol = await _contextService.GetMediatRTypeSymbolAsync(textView, position);
                    if (typeSymbol == null)
                    {
                        await _uiService.ShowErrorMessageAsync(
                            "Could not find MediatR request/command at the current position.\n\n" +
                            "Make sure:\n" +
                            "• You're positioned on a MediatR IRequest implementation\n" +
                            "• The cursor is on the class name or identifier",
                            "MediatR Extension");
                        return false;
                    }

                    progress.Report(0.3, "Getting semantic model...");
                    
                    // Step 2: Get semantic model for the document
                    var document = _workspaceService.GetDocumentFromTextView(textView);
                    var semanticModel = document != null ? await document.GetSemanticModelAsync() : null;

                    progress.Report(0.5, "Searching for handlers...");
                    
                    // Step 3: Check if this class implements both IRequest and INotification
                    bool implementsBoth = MediatRPatternMatcher.ImplementsBothRequestAndNotification(typeSymbol, semanticModel);
                    
                    // Step 4: Find all handlers (both request and notification if applicable)
                    var allHandlers = await _handlerFinder.FindAllHandlersAsync(typeSymbol, semanticModel);
                    
                    progress.Report(0.8, "Processing results...");
                    
                    if (allHandlers?.Count == 0)
                    {
                        var requestTypeName = typeSymbol.Name;
                        string message;
                        
                        if (implementsBoth)
                        {
                            message = $"Could not find any handlers for '{requestTypeName}'.\n\n" +
                                     $"This class implements both IRequest and INotification, but no handlers were found.\n\n" +
                                     "Make sure:\n" +
                                     "• The corresponding IRequestHandler exists in the solution\n" +
                                     "• The corresponding INotificationHandler(s) exist in the solution\n" +
                                     "• The solution is compiled without errors";
                        }
                        else
                        {
                            var allRequestInfo = MediatRPatternMatcher.GetAllRequestInfo(typeSymbol, semanticModel);
                            var isNotification = allRequestInfo.FirstOrDefault()?.IsNotification ?? false;
                            var handlerType = isNotification ? "INotificationHandler" : "IRequestHandler";
                            
                            message = $"Could not find {handlerType} for '{requestTypeName}'.\n\n" +
                                     "Make sure:\n" +
                                     $"• The corresponding {handlerType} exists in the solution\n" +
                                     "• The solution is compiled without errors";
                        }
                        
                        await _uiService.ShowErrorMessageAsync(message, "MediatR Extension");
                        return false;
                    }

                    progress.Report(0.9, "Navigating to handlers...");
                    
                    // Step 5: Provide user feedback about what was found
                    if (implementsBoth && allHandlers.Count > 0)
                    {
                        var requestHandlers = allHandlers.Where(h => !h.IsNotificationHandler).ToList();
                        var notificationHandlers = allHandlers.Where(h => h.IsNotificationHandler).ToList();
                        
                        System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: MediatRCommandHandler: Found {requestHandlers.Count} request handler(s) and {notificationHandlers.Count} notification handler(s) for {typeSymbol.Name}");
                    }

                    progress.Report(1.0, "Complete!");
                    
                    // Step 6: Navigate to handlers
                    // For mixed handlers, we'll let the navigation service handle the display
                    // The navigation service will show them grouped by type
                    var navigationSuccess = await _navigationService.NavigateToHandlersAsync(allHandlers, false);
                    
                    // If navigation failed and we have handlers, try once more with fresh cache
                    if (!navigationSuccess && allHandlers.Count > 0)
                    {
                        System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: MediatRCommandHandler: Navigation failed, retrying with fresh cache for: {typeSymbol.Name}");
                        
                        // Clear cache and try to find handlers again
                        MediatRPatternMatcher.ClearCacheForRequestType(typeSymbol);
                        var freshHandlers = await _handlerFinder.FindAllHandlersAsync(typeSymbol, semanticModel);
                        
                        if (freshHandlers?.Count > 0)
                        {
                            System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: MediatRCommandHandler: Found {freshHandlers.Count} handlers after cache refresh");
                            return await _navigationService.NavigateToHandlersAsync(freshHandlers, false);
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: MediatRCommandHandler: No handlers found after cache refresh");
                        }
                    }
                    
                    return navigationSuccess;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: MediatRCommandHandler: {ex.Message}");
                await _uiService.ShowErrorMessageAsync($"An error occurred: {ex.Message}", "MediatR Extension Error");
                return false;
            }
        }
    }
} 