using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Text.Editor;
using VSIXExtention.Interfaces;

namespace VSIXExtention.Services
{
    public class MediatRCommandHandler : IMediatRCommandHandler
    {
        private readonly IMediatRContextService _contextService;
        private readonly IMediatRHandlerFinder _handlerFinder;
        private readonly IMediatRNavigationService _navigationService;
        private readonly INavigationUIService _uiService;
        private readonly IWorkspaceService _workspaceService;

        public MediatRCommandHandler(
            IMediatRContextService contextService,
            IMediatRHandlerFinder handlerFinder,
            IMediatRNavigationService navigationService,
            INavigationUIService uiService,
            IWorkspaceService workspaceService)
        {
            _contextService = contextService ?? throw new ArgumentNullException(nameof(contextService));
            _handlerFinder = handlerFinder ?? throw new ArgumentNullException(nameof(handlerFinder));
            _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
            _uiService = uiService ?? throw new ArgumentNullException(nameof(uiService));
            _workspaceService = workspaceService ?? throw new ArgumentNullException(nameof(workspaceService));
        }

        public async Task<bool> ExecuteGoToImplementationAsync(ITextView textView, int position)
        {
            try
            {
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

                // Step 2: Get semantic model for the document
                var document = _workspaceService.GetDocumentFromTextView(textView);
                var semanticModel = document != null ? await document.GetSemanticModelAsync() : null;

                // Step 3: Check if this class implements both IRequest and INotification
                bool implementsBoth = MediatRPatternMatcher.ImplementsBothRequestAndNotification(typeSymbol, semanticModel);
                
                // Step 4: Find all handlers (both request and notification if applicable)
                var allHandlers = await _handlerFinder.FindAllHandlersAsync(typeSymbol, semanticModel);
                
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

                // Step 5: Provide user feedback about what was found
                if (implementsBoth && allHandlers.Count > 0)
                {
                    var requestHandlers = allHandlers.Where(h => !h.IsNotificationHandler).ToList();
                    var notificationHandlers = allHandlers.Where(h => h.IsNotificationHandler).ToList();
                    
                    System.Diagnostics.Debug.WriteLine($"MediatR Extension: Found {requestHandlers.Count} request handler(s) and {notificationHandlers.Count} notification handler(s) for {typeSymbol.Name}");
                }

                // Step 6: Navigate to handlers
                // For mixed handlers, we'll let the navigation service handle the display
                // The navigation service will show them grouped by type
                return await _navigationService.NavigateToHandlersAsync(allHandlers, false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatR Command Handler: Error executing go-to-implementation: {ex.Message}");
                await _uiService.ShowErrorMessageAsync($"An error occurred: {ex.Message}", "MediatR Extension Error");
                return false;
            }
        }
    }
} 