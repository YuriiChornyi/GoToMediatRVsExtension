using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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
        private readonly MediatRUsageFinder _usageFinder;
        private readonly MediatRNavigationService _navigationService;

        public MediatRCommandHandler(WorkspaceService workspaceService)
        {
            _workspaceService = workspaceService;
            _contextService = new MediatRContextService(workspaceService);
            _uiService = new NavigationUiService();
            _handlerFinder = new MediatRHandlerFinder(workspaceService);
            _usageFinder = new MediatRUsageFinder(workspaceService);
            _navigationService = new MediatRNavigationService(ServiceProvider.GlobalProvider, _uiService);
        }

        public async Task<bool> ExecuteGoToImplementationAsync(ITextView textView, int position, System.Threading.CancellationToken cancellationToken = default)
        {
            try
            {
                // Show progress for long operations
                using (var progress = await _uiService.ShowProgressAsync("Searching for MediatR handlers...", "Finding handlers in solution"))
                {
                    progress.Report(0.1, "Analyzing current position...");
                    
                    // Step 1: Determine the request type to navigate to (handles both direct requests and nested calls)
                    cancellationToken.ThrowIfCancellationRequested();
                    var targetRequestType = await GetTargetRequestTypeForImplementation(textView, position);
                    if (targetRequestType == null)
                    {
                        await _uiService.ShowErrorMessageAsync(
                            "Could not find MediatR request/command at the current position.\n\n" +
                            "Make sure:\n" +
                            "• You're positioned on a MediatR IRequest implementation\n" +
                            "• Or you're positioned on a nested MediatR call inside a handler\n" +
                            "• The cursor is on the type name or identifier",
                            "MediatR Extension");
                        return false;
                    }

                    progress.Report(0.3, "Getting semantic model...");
                    
                    // Step 2: Get semantic model for the document
                    var document = _workspaceService.GetDocumentFromTextView(textView);
                    var semanticModel = document != null ? await document.GetSemanticModelAsync(cancellationToken) : null;

                    progress.Report(0.5, "Searching for handlers...");
                    
                    // Step 3: Check if this type implements both IRequest and INotification
                    bool implementsBoth = MediatRPatternMatcher.ImplementsBothRequestAndNotification(targetRequestType, semanticModel);
                    
                    // Step 4: Find all handlers (both request and notification if applicable)
                    cancellationToken.ThrowIfCancellationRequested();
                    var allHandlers = await _handlerFinder.FindAllHandlersAsync(targetRequestType, semanticModel, cancellationToken);
                    
                    progress.Report(0.8, "Processing results...");
                    
                    if (allHandlers?.Count == 0)
                    {
                        var requestTypeName = targetRequestType.Name;
                        string message;
                        
                        if (implementsBoth)
                        {
                            message = $"Could not find any handlers for '{requestTypeName}'.\n\n" +
                                     $"This type implements both IRequest and INotification, but no handlers were found.\n\n" +
                                     "Make sure:\n" +
                                     "• The corresponding IRequestHandler exists in the solution\n" +
                                     "• The corresponding INotificationHandler(s) exist in the solution\n" +
                                     "• The solution is compiled without errors";
                        }
                        else
                        {
                            var allRequestInfo = MediatRPatternMatcher.GetAllRequestInfo(targetRequestType, semanticModel);
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
                        
                        System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: MediatRCommandHandler: Found {requestHandlers.Count} request handler(s) and {notificationHandlers.Count} notification handler(s) for {targetRequestType.Name}");
                    }

                    progress.Report(1.0, "Complete!");
                    
                    // Step 6: Navigate to handlers
                    // For mixed handlers, we'll let the navigation service handle the display
                    // The navigation service will show them grouped by type
                    var navigationSuccess = await _navigationService.NavigateToHandlersAsync(allHandlers, false);
                    
                    // If navigation failed, just return the result (no cache retry)
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

        public async Task<bool> ExecuteGoToUsageAsync(ITextView textView, int position, System.Threading.CancellationToken cancellationToken = default)
        {
            try
            {
                // Show progress for long operations
                using (var progress = await _uiService.ShowProgressAsync("Searching for MediatR usages...", "Finding where request is sent/published"))
                {
                    progress.Report(0.1, "Analyzing current position...");
                    
                    // Step 1: Determine the request type to find usages for (handles both handlers and nested calls)
                    cancellationToken.ThrowIfCancellationRequested();
                    var requestTypeToFind = await GetTargetRequestTypeForUsage(textView, position);
                    if (requestTypeToFind == null)
                    {
                        await _uiService.ShowErrorMessageAsync(
                            "Could not find MediatR request/handler at the current position.\n\n" +
                            "Make sure:\n" +
                            "• You're positioned on a MediatR handler or request type\n" +
                            "• Or you're positioned inside a handler method\n" +
                            "• The cursor is on the type name or identifier",
                            "MediatR Extension");
                        return false;
                    }

                    progress.Report(0.3, "Getting semantic model...");
                    
                    // Step 2: Get semantic model for the document
                    var document = _workspaceService.GetDocumentFromTextView(textView);
                    var semanticModel = document != null ? await document.GetSemanticModelAsync(cancellationToken) : null;

                    progress.Report(0.4, "Verifying request type...");
                    // Step 3: Verify we have a valid request type
                    if (semanticModel == null || !MediatRPatternMatcher.IsMediatRRequest(requestTypeToFind, semanticModel))
                    {
                        await _uiService.ShowErrorMessageAsync(
                            $"'{requestTypeToFind?.Name ?? "Unknown"}' is not a valid MediatR request type.\n\n" +
                            "Make sure:\n" +
                            "• The request implements IRequest or INotification\n" +
                            "• You're positioned correctly in the code\n" +
                            "• The solution is compiled without errors",
                            "MediatR Extension");
                        return false;
                    }

                    progress.Report(0.5, "Searching for usages...");
                    
                    // Step 4: Find all usages of this request type
                    cancellationToken.ThrowIfCancellationRequested();
                    var usages = await _usageFinder.FindUsagesAsync(requestTypeToFind, cancellationToken);
                    
                    progress.Report(0.8, "Processing results...");
                    
                    if (usages?.Count == 0)
                    {
                        var requestTypeName = requestTypeToFind.Name;
                        string message = $"Could not find any usages of '{requestTypeName}'.\n\n" +
                                       "Make sure:\n" +
                                       "• The request is being sent somewhere in the solution using _mediator.Send() or _mediator.Publish()\n" +
                                       "• The solution is compiled without errors\n" +
                                       "• You have the correct MediatR references";
                        
                        await _uiService.ShowErrorMessageAsync(message, "MediatR Extension");
                        return false;
                    }

                    progress.Report(0.9, "Navigating to usages...");
                    
                    // Step 5: Provide user feedback about what was found
                    System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: MediatRCommandHandler: Found {usages.Count} usage(s) for {requestTypeToFind.Name}");
                    
                    // Log usage details
                    foreach (var usage in usages)
                    {
                        System.Diagnostics.Debug.WriteLine($"  - Found usage: {usage.UsageType} in {usage.ContextDescription} at {usage.FilePath}:{usage.LineNumber}");
                    }

                    progress.Report(1.0, "Complete!");
                    
                    // Step 6: Navigate to usages
                    var navigationSuccess = await _navigationService.NavigateToUsagesAsync(usages, requestTypeToFind.Name);
                    
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

        private Microsoft.CodeAnalysis.INamedTypeSymbol GetRequestTypeFromContext(Microsoft.CodeAnalysis.INamedTypeSymbol typeSymbol, Microsoft.CodeAnalysis.SemanticModel semanticModel)
        {
            // Check if this is a handler - extract the request type from the handler
            if (MediatRPatternMatcher.IsMediatRHandler(typeSymbol, semanticModel))
            {
                var handlerInfo = MediatRPatternMatcher.GetHandlerInfo(typeSymbol, semanticModel);
                if (handlerInfo != null)
                {
                    // Find the request type symbol from the handler's generic arguments
                    foreach (var @interface in typeSymbol.AllInterfaces)
                    {
                        if (@interface.ContainingNamespace?.ToDisplayString() == "MediatR")
                        {
                            if ((@interface.Name == "IRequestHandler" || @interface.Name == "INotificationHandler") && @interface.TypeArguments.Length > 0)
                            {
                                var requestTypeSymbol = @interface.TypeArguments[0] as Microsoft.CodeAnalysis.INamedTypeSymbol;
                                return requestTypeSymbol;
                            }
                        }
                    }
                }
                return null;
            }
            
            // Check if this is a request type itself
            if (MediatRPatternMatcher.IsMediatRRequest(typeSymbol, semanticModel))
            {
                return typeSymbol;
            }
            
            return null;
        }

        /// <summary>
        /// Determines the target request type for implementation navigation.
        /// In mixed contexts (nested calls), returns the nested request type.
        /// </summary>
        private async Task<Microsoft.CodeAnalysis.INamedTypeSymbol> GetTargetRequestTypeForImplementation(ITextView textView, int position)
        {
            // First check if we're in a nested MediatR call context
            bool isInNestedContext = await _contextService.IsInNestedMediatRCallContextAsync(textView);
            
            if (isInNestedContext)
            {
                // For nested calls, we want to navigate to the nested request's handler
                // Use the specialized method to extract the nested request type
                var nestedRequestType = await _contextService.GetNestedRequestTypeAsync(textView, position);
                if (nestedRequestType != null)
                {
                    return nestedRequestType;
                }
                
                // If that fails, fall back to the general method
                var typeSymbol = await _contextService.GetMediatRTypeSymbolAsync(textView, position);
                
                var document = _workspaceService.GetDocumentFromTextView(textView);
                var semanticModel = document != null ? await document.GetSemanticModelAsync() : null;
                
                if (typeSymbol != null && semanticModel != null && MediatRPatternMatcher.IsMediatRRequest(typeSymbol, semanticModel))
                {
                    return typeSymbol;
                }
            }
            
            // Fall back to the standard logic for direct request contexts
            return await _contextService.GetMediatRTypeSymbolAsync(textView, position);
        }

        /// <summary>
        /// Determines the target request type for usage navigation.
        /// In mixed contexts (nested calls), returns the current handler's request type.
        /// </summary>
        private async Task<Microsoft.CodeAnalysis.INamedTypeSymbol> GetTargetRequestTypeForUsage(ITextView textView, int position)
        {
            var document = _workspaceService.GetDocumentFromTextView(textView);
            var semanticModel = document != null ? await document.GetSemanticModelAsync() : null;
            
            // First check if we're in a nested MediatR call context
            bool isInNestedContext = await _contextService.IsInNestedMediatRCallContextAsync(textView);
            
            if (isInNestedContext)
            {
                // For nested calls in usage context, we want to find the current handler's request type (shallow navigation)
                var syntaxTree = await document.GetSyntaxTreeAsync();
                if (syntaxTree != null)
                {
                    var root = await syntaxTree.GetRootAsync();
                    var node = root.FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(position, 0), getInnermostNodeForTie: true);
                    
                    // Find the containing handler type (class/record)
                    var typeDeclaration = node.FirstAncestorOrSelf<TypeDeclarationSyntax>();
                    if (typeDeclaration != null && semanticModel != null)
                    {
                        var typeSymbol = semanticModel.GetDeclaredSymbol(typeDeclaration) as Microsoft.CodeAnalysis.INamedTypeSymbol;
                        if (typeSymbol != null && MediatRPatternMatcher.IsMediatRHandler(typeSymbol, semanticModel))
                        {
                            // Extract the request type from the handler
                            return GetRequestTypeFromContext(typeSymbol, semanticModel);
                        }
                        else
                        {
                            // If we're not in a handler type (e.g., controller), 
                            // we can't provide "shallow" navigation, so return null to skip usage navigation
                            System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: In nested context but not in handler type ({typeSymbol?.Name}), skipping usage navigation");
                            return null;
                        }
                    }
                }
            }
            
            // Fall back to the standard logic
            var typeSymbolAtPosition = await _contextService.GetMediatRTypeSymbolAsync(textView, position);
            return GetRequestTypeFromContext(typeSymbolAtPosition, semanticModel);
        }
    }
} 