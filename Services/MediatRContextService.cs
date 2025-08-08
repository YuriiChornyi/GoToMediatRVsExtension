using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Text.Editor;

namespace VSIXExtention.Services
{
    public class MediatRContextService
    {
        private readonly WorkspaceService _workspaceService;
        private static readonly string[] MediatRSendMethods = { "Send", "SendAsync" };
        private static readonly string[] MediatRPublishMethods = { "Publish", "PublishAsync" };
        
        public MediatRContextService(WorkspaceService workspaceService)
        {
            _workspaceService = workspaceService;
        }

        public async Task<bool> IsInMediatRContextAsync(ITextView textView)
        {
            try
            {
                if (!IsValidContext(textView))
                    return false;

                var document = _workspaceService.GetDocumentFromTextView(textView);
                if (document == null)
                    return false;

                var typeSymbol = await GetMediatRTypeSymbolAsync(textView, textView.Caret.Position.BufferPosition.Position);
                return typeSymbol != null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: MediatRContext: Error checking context: {ex.Message}");
                return false;
            }
        }

        public async Task<INamedTypeSymbol> GetMediatRTypeSymbolAsync(ITextView textView, int position)
        {
            try
            {
                var document = _workspaceService.GetDocumentFromTextView(textView);
                if (document == null)
                    return null;

                var syntaxTree = await document.GetSyntaxTreeAsync();
                if (syntaxTree == null)
                    return null;

                var textSpan = GetTextSpan(textView, position);
                var root = await syntaxTree.GetRootAsync();
                var node = root.FindNode(textSpan, getInnermostNodeForTie: true);

                // Quick syntax check - must be in/on a type declaration (class/record) or identifier
                var typeDeclaration = node.FirstAncestorOrSelf<TypeDeclarationSyntax>();
                var identifierName = node as IdentifierNameSyntax ?? node.FirstAncestorOrSelf<IdentifierNameSyntax>();

                if (typeDeclaration == null && identifierName == null)
                    return null;

                // Only get semantic model if we passed syntax checks (expensive operation)
                var semanticModel = await document.GetSemanticModelAsync();
                if (semanticModel == null)
                    return null;

                // Check type declaration first (class, record, etc. - more common case)
                if (typeDeclaration != null)
                {
                    var typeSymbol = semanticModel.GetDeclaredSymbol(typeDeclaration) as INamedTypeSymbol;
                    if (IsValidMediatRType(typeSymbol, semanticModel))
                        return typeSymbol;
                }

                // Check identifier reference (less common)
                if (identifierName != null)
                {
                    var symbolInfo = semanticModel.GetSymbolInfo(identifierName);
                    if (symbolInfo.Symbol is INamedTypeSymbol namedTypeSymbol && IsValidMediatRType(namedTypeSymbol, semanticModel))
                        return namedTypeSymbol;
                }

                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: MediatRContext: Error getting type symbol: {ex.Message}");
                return null;
            }
        }

        private bool IsValidContext(ITextView textView)
        {
            // Early bailout: check buffer properties first (fastest check)
            var textBuffer = textView?.TextBuffer;
            if (textBuffer == null)
                return false;

            // Quick content type check
            var contentType = textBuffer.ContentType;
            if (contentType?.TypeName != "CSharp")
                return false;

            var filePath = _workspaceService.GetFilePathFromTextView(textView);

            // Only process C# files
            if (string.IsNullOrEmpty(filePath) || !filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                return false;

            // Skip multiline selections for performance
            if (!textView.Selection.IsEmpty && IsMultilineSelection(textView))
                return false;

            return true;
        }

        private bool IsMultilineSelection(ITextView textView)
        {
            var selectionSpan = textView.Selection.SelectedSpans[0];
            var startLine = textView.TextSnapshot.GetLineFromPosition(selectionSpan.Start.Position);
            var endLine = textView.TextSnapshot.GetLineFromPosition(selectionSpan.End.Position);
            return endLine.LineNumber > startLine.LineNumber;
        }

        private TextSpan GetTextSpan(ITextView textView, int position)
        {
            if (!textView.Selection.IsEmpty)
            {
                var selectionSpan = textView.Selection.SelectedSpans[0];
                return new TextSpan(selectionSpan.Start.Position, selectionSpan.Length);
            }

            return new TextSpan(position, 0);
        }

        private bool IsValidMediatRType(INamedTypeSymbol typeSymbol, SemanticModel semanticModel)
        {
            if (typeSymbol == null)
                return false;

            // Check if it's a MediatR request (IRequest, INotification)
            if (MediatRPatternMatcher.GetRequestInfo(typeSymbol, semanticModel) != null)
                return true;

            // Check if it's a MediatR handler (IRequestHandler, INotificationHandler)
            if (MediatRPatternMatcher.IsMediatRHandler(typeSymbol, semanticModel))
                return true;

            return false;
        }

        public async Task<bool> IsInMediatRHandlerContextAsync(ITextView textView)
        {
            try
            {
                if (!IsValidContext(textView))
                    return false;

                var document = _workspaceService.GetDocumentFromTextView(textView);
                if (document == null)
                    return false;

                var position = textView.Caret.Position.BufferPosition.Position;
                var syntaxTree = await document.GetSyntaxTreeAsync();
                if (syntaxTree == null)
                    return false;

                var textSpan = GetTextSpan(textView, position);
                var root = await syntaxTree.GetRootAsync();
                var node = root.FindNode(textSpan, getInnermostNodeForTie: true);

                // Check if we're in a handler class or Handle method
                var typeDeclaration = node.FirstAncestorOrSelf<TypeDeclarationSyntax>();
                var methodDeclaration = node.FirstAncestorOrSelf<MethodDeclarationSyntax>();

                if (typeDeclaration == null)
                    return false;

                var semanticModel = await document.GetSemanticModelAsync();
                if (semanticModel == null)
                    return false;

                var typeSymbol = semanticModel.GetDeclaredSymbol(typeDeclaration) as INamedTypeSymbol;
                
                // Check if this is a MediatR handler
                if (IsValidMediatRHandler(typeSymbol, semanticModel))
                {
                    // If we're in a Handle method, that's definitely handler context
                    if (methodDeclaration?.Identifier.ValueText == "Handle")
                        return true;

                    // If we're on the class name or elsewhere in the handler class, also show
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: MediatRContext: Error checking handler context: {ex.Message}");
                return false;
            }
        }

        private bool IsValidMediatRHandler(INamedTypeSymbol typeSymbol, SemanticModel semanticModel)
        {
            return typeSymbol != null && MediatRPatternMatcher.IsMediatRHandler(typeSymbol, semanticModel);
        }

        public async Task<bool> IsInMediatRRequestContextAsync(ITextView textView)
        {
            try
            {
                if (!IsValidContext(textView))
                    return false;

                var document = _workspaceService.GetDocumentFromTextView(textView);
                if (document == null)
                    return false;

                var typeSymbol = await GetMediatRTypeSymbolAsync(textView, textView.Caret.Position.BufferPosition.Position);
                if (typeSymbol == null)
                    return false;

                var semanticModel = await document.GetSemanticModelAsync();
                if (semanticModel == null)
                    return false;

                // Check if this is a MediatR request (not a handler)
                return MediatRPatternMatcher.IsMediatRRequest(typeSymbol, semanticModel);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: MediatRContext: Error checking request context: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Determines if cursor is positioned on a nested MediatR call inside a handler method.
        /// This allows showing both "Go to Implementation" (for nested call) and "Go to Usage" (for current handler).
        /// </summary>
        public async Task<bool> IsInNestedMediatRCallContextAsync(ITextView textView)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Checking nested MediatR context at position {textView.Caret.Position.BufferPosition.Position}");
                
                // Performance optimization: early bailout checks
                if (!IsValidContext(textView))
                {
                    System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Not valid context (not C# file or multiline selection)");
                    return false;
                }

                var document = _workspaceService.GetDocumentFromTextView(textView);
                if (document == null)
                {
                    System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Document is null");
                    return false;
                }

                var position = textView.Caret.Position.BufferPosition.Position;
                var syntaxTree = await document.GetSyntaxTreeAsync();
                if (syntaxTree == null)
                {
                    System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Syntax tree is null");
                    return false;
                }

                var textSpan = GetTextSpan(textView, position);
                var root = await syntaxTree.GetRootAsync();
                var node = root.FindNode(textSpan, getInnermostNodeForTie: true);

                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Found node: {node?.GetType().Name} - '{node?.ToString()?.Trim().Replace("\n", " ").Replace("\r", "")}'");

                // Performance optimization: quick syntax-only checks first
                var methodDeclaration = node.FirstAncestorOrSelf<MethodDeclarationSyntax>();
                if (methodDeclaration == null)
                {
                    System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Not in any method");
                    return false;
                }
                
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: In method: {methodDeclaration.Identifier.ValueText}");

                var typeDeclaration = node.FirstAncestorOrSelf<TypeDeclarationSyntax>();
                if (typeDeclaration == null)
                {
                    System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Type declaration is null");
                    return false;
                }

                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: In method '{methodDeclaration.Identifier.ValueText}' of type: {typeDeclaration.Identifier.ValueText}");

                // Check for potential MediatR calls with syntax-only analysis first
                var invocationNode = node.FirstAncestorOrSelf<InvocationExpressionSyntax>();
                var objectCreationNode = node.FirstAncestorOrSelf<ObjectCreationExpressionSyntax>();
                
                // Quick syntax check: look for mediator method names
                bool hasPotentialMediatRCall = false;
                if (invocationNode != null)
                {
                    if (invocationNode.Expression is MemberAccessExpressionSyntax memberAccess)
                    {
                        var methodName = memberAccess.Name.Identifier.ValueText;
                        hasPotentialMediatRCall = IsKnownMediatRMethod(methodName);
                    }
                    else if (invocationNode.Expression is MemberBindingExpressionSyntax memberBinding &&
                             invocationNode.Parent is ConditionalAccessExpressionSyntax)
                    {
                        var methodName = memberBinding.Name.Identifier.ValueText;
                        hasPotentialMediatRCall = IsKnownMediatRMethod(methodName);
                    }
                }

                // Also check if we're clicking on a variable that might be passed to a MediatR call
                bool isVariableInMediatRCall = false;
                if (!hasPotentialMediatRCall && node is IdentifierNameSyntax identifier)
                {
                    System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Checking identifier '{identifier.Identifier.ValueText}' for variable in MediatR call");
                    
                    // Check if this identifier is inside an argument list of a potential MediatR call
                    var argumentList = identifier.FirstAncestorOrSelf<ArgumentListSyntax>();
                    if (argumentList?.Parent is InvocationExpressionSyntax parentInvocation &&
                        parentInvocation.Expression is MemberAccessExpressionSyntax parentMemberAccess)
                    {
                        var parentMethodName = parentMemberAccess.Name.Identifier.ValueText;
                        isVariableInMediatRCall = IsKnownMediatRMethod(parentMethodName);
                        
                        System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Parent method: {parentMethodName}, isVariableInMediatRCall: {isVariableInMediatRCall}");
                    }
                    else if (argumentList?.Parent is InvocationExpressionSyntax parentInvocation2 &&
                             parentInvocation2.Expression is MemberBindingExpressionSyntax parentMemberBinding &&
                             parentInvocation2.Parent is ConditionalAccessExpressionSyntax)
                    {
                        var parentMethodName = parentMemberBinding.Name.Identifier.ValueText;
                        isVariableInMediatRCall = IsKnownMediatRMethod(parentMethodName);
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: No argument list parent or not member access");
                    }
                }

                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Syntax checks - hasPotentialMediatRCall: {hasPotentialMediatRCall}, isVariableInMediatRCall: {isVariableInMediatRCall}, objectCreationNode: {objectCreationNode != null}");

                // If no potential MediatR calls found syntactically, no need for expensive semantic analysis
                if (!hasPotentialMediatRCall && !isVariableInMediatRCall && objectCreationNode == null)
                {
                    System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: No potential MediatR calls found syntactically");
                    return false;
                }

                // Only now get semantic model (expensive operation)
                var semanticModel = await document.GetSemanticModelAsync();
                if (semanticModel == null)
                    return false;

                var typeSymbol = semanticModel.GetDeclaredSymbol(typeDeclaration) as INamedTypeSymbol;

                // Check if this is a MediatR handler (preferred context)
                bool isHandlerClass = IsValidMediatRHandler(typeSymbol, semanticModel);
                
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: {typeSymbol?.Name ?? "null"} isHandlerClass: {isHandlerClass}");

                // Now do full semantic analysis
                if (invocationNode != null && IsNestedMediatRCall(invocationNode, semanticModel))
                {
                    System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Found nested MediatR call via invocation");
                    return true;
                }

                if (objectCreationNode != null && IsNestedMediatRRequestCreation(objectCreationNode, semanticModel))
                {
                    System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Found nested MediatR call via object creation");
                    return true;
                }

                // Check if we're on an identifier that represents a nested MediatR request
                // Only if we haven't found it through other means
                var requestTypeSymbol = await GetMediatRTypeSymbolAsync(textView, position);
                if (requestTypeSymbol != null && MediatRPatternMatcher.IsMediatRRequest(requestTypeSymbol, semanticModel))
                {
                    System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Found MediatR request type in nested context: {requestTypeSymbol.Name}");
                    return true;
                }

                // Fallback: scan the entire method for any MediatR invocations
                // This enables the menu even when the caret isn't directly on the call site
                foreach (var invocation in GetMediatRInvocations(methodDeclaration))
                {
                    if (IsNestedMediatRCall(invocation, semanticModel))
                    {
                        System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Found nested MediatR call elsewhere in method: {methodDeclaration.Identifier.ValueText}");
                        return true;
                    }
                }

                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: No nested MediatR context found");
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: MediatRContext: Error checking nested MediatR context: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Determines if cursor is positioned on a nested MediatR call inside a handler method.
        /// This is specifically for usage navigation (shallow) - only works inside handler classes.
        /// </summary>
        public async Task<bool> IsInNestedMediatRCallInHandlerContextAsync(ITextView textView)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Checking nested MediatR context IN HANDLER at position {textView.Caret.Position.BufferPosition.Position}");
                
                // First check if we're in nested context at all
                bool isInNestedContext = await IsInNestedMediatRCallContextAsync(textView);
                if (!isInNestedContext)
                {
                    System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Not in nested context");
                    return false;
                }

                // Now verify we're actually in a handler class
                var document = _workspaceService.GetDocumentFromTextView(textView);
                if (document == null)
                    return false;

                var position = textView.Caret.Position.BufferPosition.Position;
                var syntaxTree = await document.GetSyntaxTreeAsync();
                if (syntaxTree == null)
                    return false;

                var textSpan = GetTextSpan(textView, position);
                var root = await syntaxTree.GetRootAsync();
                var node = root.FindNode(textSpan, getInnermostNodeForTie: true);

                var typeDeclaration = node.FirstAncestorOrSelf<TypeDeclarationSyntax>();
                if (typeDeclaration == null)
                    return false;

                var semanticModel = await document.GetSemanticModelAsync();
                if (semanticModel == null)
                    return false;

                var typeSymbol = semanticModel.GetDeclaredSymbol(typeDeclaration) as INamedTypeSymbol;
                bool isHandlerClass = IsValidMediatRHandler(typeSymbol, semanticModel);
                
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Nested context in handler check - {typeSymbol?.Name} isHandlerClass: {isHandlerClass}");
                
                return isHandlerClass;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: MediatRContext: Error checking nested MediatR context in handler: {ex.Message}");
                return false;
            }
        }

        private bool IsNestedMediatRCall(InvocationExpressionSyntax invocation, SemanticModel semanticModel)
        {
            try
            {
                // Member access: _mediator.Send(...)
                if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
                {
                    var methodName = memberAccess.Name.Identifier.ValueText;
                    if (IsKnownMediatRMethod(methodName))
                    {
                        var typeInfo = semanticModel.GetTypeInfo(memberAccess.Expression);
                        if (typeInfo.Type?.ContainingNamespace?.ToDisplayString() == "MediatR")
                            return true;
                        if (typeInfo.Type?.AllInterfaces.Any(i => 
                            i.ContainingNamespace?.ToDisplayString() == "MediatR" && 
                            (i.Name == "IMediator" || i.Name == "ISender" || i.Name == "IPublisher")) == true)
                        {
                            return true;
                        }
                    }
                }

                // Conditional access: _mediator?.Send(...)
                if (invocation.Expression is MemberBindingExpressionSyntax memberBinding &&
                    invocation.Parent is ConditionalAccessExpressionSyntax conditional)
                {
                    var methodName = memberBinding.Name.Identifier.ValueText;
                    if (IsKnownMediatRMethod(methodName))
                    {
                        var targetExpr = conditional.Expression;
                        var typeInfo = semanticModel.GetTypeInfo(targetExpr);
                        if (typeInfo.Type?.ContainingNamespace?.ToDisplayString() == "MediatR")
                            return true;
                        if (typeInfo.Type?.AllInterfaces.Any(i => 
                            i.ContainingNamespace?.ToDisplayString() == "MediatR" && 
                            (i.Name == "IMediator" || i.Name == "ISender" || i.Name == "IPublisher")) == true)
                        {
                            return true;
                        }
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private bool IsNestedMediatRRequestCreation(ObjectCreationExpressionSyntax objectCreation, SemanticModel semanticModel)
        {
            try
            {
                var typeInfo = semanticModel.GetTypeInfo(objectCreation);
                if (typeInfo.Type is INamedTypeSymbol typeSymbol)
                {
                    return MediatRPatternMatcher.IsMediatRRequest(typeSymbol, semanticModel);
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Gets the specific request type from a nested MediatR call (e.g., the NewQuery from mediator.Send(new NewQuery())).
        /// This is more precise than GetMediatRTypeSymbolAsync for nested call contexts.
        /// </summary>
        public async Task<INamedTypeSymbol> GetNestedRequestTypeAsync(ITextView textView, int position)
        {
            try
            {
                if (!IsValidContext(textView))
                    return null;

                var document = _workspaceService.GetDocumentFromTextView(textView);
                if (document == null)
                    return null;

                var syntaxTree = await document.GetSyntaxTreeAsync();
                if (syntaxTree == null)
                    return null;

                var textSpan = GetTextSpan(textView, position);
                var root = await syntaxTree.GetRootAsync();
                var node = root.FindNode(textSpan, getInnermostNodeForTie: true);

                var semanticModel = await document.GetSemanticModelAsync();
                if (semanticModel == null)
                    return null;

                // Look for object creation expressions first (new NewQuery())
                var objectCreation = node.FirstAncestorOrSelf<ObjectCreationExpressionSyntax>();
                if (objectCreation != null)
                {
                    var typeInfo = semanticModel.GetTypeInfo(objectCreation);
                    if (typeInfo.Type is INamedTypeSymbol createdType && 
                        MediatRPatternMatcher.IsMediatRRequest(createdType, semanticModel))
                    {
                        return createdType;
                    }
                }

                // Look for invocation expressions (mediator.Send(...))
                var invocation = node.FirstAncestorOrSelf<InvocationExpressionSyntax>();
                if (invocation != null && IsNestedMediatRCall(invocation, semanticModel))
                {
                    var extracted = TryExtractRequestTypeFromInvocation(invocation, semanticModel);
                    if (extracted != null)
                        return extracted;
                }

                // Look for variable references in MediatR calls (e.g., clicking on 'query' in mediator.Send(query))
                var parentInvocation = node.FirstAncestorOrSelf<InvocationExpressionSyntax>();
                if (parentInvocation != null && IsNestedMediatRCall(parentInvocation, semanticModel))
                {
                    // Check if the current node is an identifier inside the argument list
                    var identifierInCall = node as IdentifierNameSyntax;
                    if (identifierInCall != null)
                    {
                        // Check if this identifier is within the argument list of the MediatR call
                        var argumentList = identifierInCall.FirstAncestorOrSelf<ArgumentListSyntax>();
                        if (argumentList != null && argumentList.Parent == parentInvocation)
                        {
                            // Get the type of the variable being passed
                            var symbolInfo = semanticModel.GetSymbolInfo(identifierInCall);
                            
                            System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Variable '{identifierInCall.Identifier.ValueText}' symbol: {symbolInfo.Symbol?.GetType().Name}");
                            
                            if (symbolInfo.Symbol is ILocalSymbol localSymbol && 
                                localSymbol.Type is INamedTypeSymbol variableType)
                            {
                                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Variable type: {variableType.Name}");
                                if (MediatRPatternMatcher.IsMediatRRequest(variableType, semanticModel))
                                {
                                    System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Found MediatR request type from variable: {variableType.Name}");
                                    return variableType;
                                }
                            }
                            
                            // Also handle field/property references
                            if (symbolInfo.Symbol is IFieldSymbol fieldSymbol && 
                                fieldSymbol.Type is INamedTypeSymbol fieldType)
                            {
                                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Field type: {fieldType.Name}");
                                if (MediatRPatternMatcher.IsMediatRRequest(fieldType, semanticModel))
                                {
                                    System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Found MediatR request type from field: {fieldType.Name}");
                                    return fieldType;
                                }
                            }
                            
                            if (symbolInfo.Symbol is IPropertySymbol propertySymbol && 
                                propertySymbol.Type is INamedTypeSymbol propertyType)
                            {
                                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Property type: {propertyType.Name}");
                                if (MediatRPatternMatcher.IsMediatRRequest(propertyType, semanticModel))
                                {
                                    System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Found MediatR request type from property: {propertyType.Name}");
                                    return propertyType;
                                }
                            }
                            
                            // Also try getting type info directly if symbol info fails
                            var typeInfo = semanticModel.GetTypeInfo(identifierInCall);
                            if (typeInfo.Type is INamedTypeSymbol directType)
                            {
                                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Direct type info: {directType.Name}");
                                if (MediatRPatternMatcher.IsMediatRRequest(directType, semanticModel))
                                {
                                    System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: Found MediatR request type from direct type info: {directType.Name}");
                                    return directType;
                                }
                            }
                        }
                    }
                }

                // Look for identifier names that represent request types
                var identifierName = node as IdentifierNameSyntax ?? node.FirstAncestorOrSelf<IdentifierNameSyntax>();
                if (identifierName != null)
                {
                    var symbolInfo = semanticModel.GetSymbolInfo(identifierName);
                    if (symbolInfo.Symbol is INamedTypeSymbol namedType && 
                        MediatRPatternMatcher.IsMediatRRequest(namedType, semanticModel))
                    {
                        return namedType;
                    }
                }

                // Fallback: scan the containing method for the first MediatR invocation
                var methodDeclaration = node.FirstAncestorOrSelf<MethodDeclarationSyntax>();
                if (methodDeclaration != null)
                {
                    foreach (var inv in GetMediatRInvocations(methodDeclaration))
                    {
                        if (!IsNestedMediatRCall(inv, semanticModel))
                            continue;

                        var extracted = TryExtractRequestTypeFromInvocation(inv, semanticModel);
                        if (extracted != null)
                            return extracted;
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: MediatRContext: Error getting nested request type: {ex.Message}");
                return null;
            }
        }

        private static bool IsKnownMediatRMethod(string methodName)
        {
            return MediatRSendMethods.Contains(methodName) || MediatRPublishMethods.Contains(methodName);
        }

        private static System.Collections.Generic.IEnumerable<InvocationExpressionSyntax> GetMediatRInvocations(MethodDeclarationSyntax method)
        {
            return method.DescendantNodes()
                         .OfType<InvocationExpressionSyntax>()
                         .Where(inv => inv.Expression is MemberAccessExpressionSyntax ma && IsKnownMediatRMethod(ma.Name.Identifier.ValueText));
        }

        private static INamedTypeSymbol TryExtractRequestTypeFromInvocation(InvocationExpressionSyntax invocation, SemanticModel semanticModel)
        {
            if (invocation?.ArgumentList?.Arguments.Count > 0)
            {
                var firstArgument = invocation.ArgumentList.Arguments[0];

                // Object creation: new SomeRequest()
                if (firstArgument.Expression is ObjectCreationExpressionSyntax created)
                {
                    var createdTypeInfo = semanticModel.GetTypeInfo(created).Type as INamedTypeSymbol;
                    if (createdTypeInfo != null && MediatRPatternMatcher.IsMediatRRequest(createdTypeInfo, semanticModel))
                        return createdTypeInfo;
                }

                // General expression type
                var argumentType = semanticModel.GetTypeInfo(firstArgument.Expression).Type as INamedTypeSymbol;
                if (argumentType != null && MediatRPatternMatcher.IsMediatRRequest(argumentType, semanticModel))
                    return argumentType;
            }

            return null;
        }
    }
} 