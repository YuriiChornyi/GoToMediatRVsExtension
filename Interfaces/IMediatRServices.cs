using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Text.Editor;

namespace VSIXExtention.Interfaces
{
    /// <summary>
    /// Centralized workspace management service
    /// </summary>
    public interface IWorkspaceService
    {
        VisualStudioWorkspace GetWorkspace();
        Document GetDocumentFromTextView(ITextView textView);
        string GetFilePathFromTextView(ITextView textView);
        event EventHandler<WorkspaceChangeEventArgs> WorkspaceChanged;
    }

    /// <summary>
    /// Service for detecting MediatR context
    /// </summary>
    public interface IMediatRContextService
    {
        Task<bool> IsInMediatRContextAsync(ITextView textView);
        Task<INamedTypeSymbol> GetMediatRTypeSymbolAsync(ITextView textView, int position);
    }

    /// <summary>
    /// Service for finding MediatR handlers
    /// </summary>
    public interface IMediatRHandlerFinder
    {
        Task<List<MediatRPatternMatcher.MediatRHandlerInfo>> FindHandlersAsync(INamedTypeSymbol requestTypeSymbol);
        Task<List<MediatRPatternMatcher.MediatRHandlerInfo>> FindAllHandlersAsync(INamedTypeSymbol requestTypeSymbol, SemanticModel semanticModel);
    }

    /// <summary>
    /// Cache service for MediatR handlers
    /// </summary>
    public interface IMediatRCacheService
    {
        List<MediatRPatternMatcher.MediatRHandlerInfo> GetCachedHandlers(string requestTypeName);
        void CacheHandlers(string requestTypeName, List<MediatRPatternMatcher.MediatRHandlerInfo> handlers);
        void InvalidateHandlersForRequestType(string requestTypeName);
        void ClearCache();
        Task SaveCacheAsync();
        Task InitializeAsync(Solution solution);
    }

    /// <summary>
    /// Pure navigation service
    /// </summary>
    public interface IMediatRNavigationService
    {
        Task<bool> NavigateToHandlersAsync(List<MediatRPatternMatcher.MediatRHandlerInfo> handlers, bool isNotification);
        Task<bool> NavigateToLocationAsync(Location location);
    }

    /// <summary>
    /// UI service for navigation dialogs
    /// </summary>
    public interface INavigationUIService
    {
        string ShowHandlerSelectionDialog(HandlerDisplayInfo[] handlers, bool isNotification);
        Task ShowErrorMessageAsync(string message, string title);
        Task ShowInfoMessageAsync(string message, string title);
        Task ShowWarningMessageAsync(string message, string title);
    }

    /// <summary>
    /// Document event handling service
    /// </summary>
    public interface IDocumentEventService
    {
        event EventHandler<string> DocumentSaved;
        void Initialize();
        void Dispose();
    }

    /// <summary>
    /// Main command handler service
    /// </summary>
    public interface IMediatRCommandHandler
    {
        Task<bool> ExecuteGoToImplementationAsync(ITextView textView, int position);
    }
} 