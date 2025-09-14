# MediatR Navigation Extension - Project Analysis & Cursor Guidelines

## Project Overview

**MediatR Navigation Extension** is a Visual Studio 2022-2026 extension (.VSIX) that provides intelligent navigation between MediatR requests/notifications and their corresponding handlers. The extension uses Roslyn for code analysis and offers bidirectional navigation with context-aware commands.

### Core Functionality
- **Go to MediatR Implementation**: Navigate from requests/notifications to their handlers (Ctrl+Alt+F12)
- **Go to MediatR Send/Publish**: Navigate from handlers to usage locations (Ctrl+Alt+F11)
- **Item Templates**: VS templates for creating MediatR commands, handlers, and notifications
- **Smart Context Detection**: Commands only appear when relevant to current cursor position
- **Multi-Handler Support**: Selection dialog when multiple handlers/usages exist

## Architecture Overview

### Technology Stack
- **Target Framework**: .NET Framework 4.8
- **Visual Studio**: 2022-2026 (17.0-18.x)
- **Language**: C# 
- **Key Dependencies**:
  - Microsoft.VisualStudio.SDK (17.14.40265)
  - Microsoft.CodeAnalysis.CSharp (4.14.0)
  - Microsoft.VisualStudio.LanguageServices (4.14.0)
  - Community.VisualStudio.Toolkit.17 (17.0.533)

### Project Structure
```
GoToMediatRVsExtension/
├── Services/                    # Core business logic
│   ├── MediatRCommandHandler.cs       # Main command orchestration
│   ├── MediatRNavigationService.cs    # Navigation & UI coordination
│   ├── MediatRHandlerFinder.cs        # Handler discovery
│   ├── MediatRUsageFinder.cs          # Usage location discovery
│   ├── MediatRContextService.cs       # Context detection
│   ├── NavigationUiService.cs         # UI dialogs & progress
│   └── WorkspaceService.cs            # Roslyn workspace management
├── Models/                      # Data transfer objects
│   ├── MediatRHandlerInfo.cs          # Handler metadata
│   ├── MediatRRequestInfo.cs          # Request metadata
│   ├── MediatRUsageInfo.cs            # Usage metadata
│   ├── HandlerDisplayInfo.cs          # UI display data
│   └── UsageDisplayInfo.cs            # UI display data
├── Helpers/
│   └── RoslynSymbolHelper.cs          # Roslyn utility methods
├── Templates/                   # VS item templates
│   ├── MediatRCommand.cs/.vstemplate
│   ├── MediatRHandler.cs/.vstemplate
│   ├── MediatRNotification.cs/.vstemplate
│   └── MediatRNotificationHandler.cs/.vstemplate
├── MediatRPatternMatcher.cs     # Core pattern recognition
├── HandlerSelectionDialog.xaml  # Multi-handler selection UI
├── VSIXExtentionPackage.cs      # VS package entry point
├── VSPackage.vsct              # Command definitions
└── source.extension.vsixmanifest # Extension metadata
```

## Core Components Deep Dive

### 1. MediatRPatternMatcher.cs
**Purpose**: Central pattern recognition and type analysis
**Key Methods**:
- `IsMediatRRequest()`: Identifies IRequest/INotification implementations
- `IsMediatRHandler()`: Identifies handler implementations
- `GetAllRequestInfo()`: Handles dual-interface implementations (IRequest + INotification)
- `FindAllHandlersForTypeSymbol()`: Main handler discovery with deduplication
- `AreTypesEqual()`: Robust type comparison across assemblies

**Critical Features**:
- Supports both IRequest and INotification on same class
- Navigates to Handle method location (not just class)
- Symbol-based matching to avoid name collisions
- Parallel project processing for performance

### 2. Services Layer
**MediatRCommandHandler**: Orchestrates the entire navigation flow
- Handles both "Go to Implementation" and "Go to Usage" commands
- Manages progress reporting and error handling
- Determines context (direct request vs nested call)

**MediatRNavigationService**: Manages actual navigation and UI
- Single vs multiple handler logic
- File existence validation
- DTE-based document opening and positioning
- Context-aware dialog titles

**MediatRContextService**: Smart context detection
- `IsInMediatRRequestContextAsync()`: Detects request/notification context
- `IsInMediatRHandlerContextAsync()`: Detects handler context  
- `IsInNestedMediatRCallContextAsync()`: Detects nested calls within handlers
- Supports mixed contexts (nested calls)

### 3. VS Integration
**VSIXExtentionPackage.cs**: Main VS package
- Async initialization pattern
- Command registration with BeforeQueryStatus handlers
- UI thread management with JoinableTaskFactory
- Service acquisition and workspace setup

**VSPackage.vsct**: Command definitions
- Edit menu and context menu integration
- Keyboard shortcuts (Ctrl+Alt+F12, Ctrl+Alt+F11)
- Dynamic visibility flags

## Supported MediatR Patterns

### Request Types
- `IRequest` (command without response)
- `IRequest<TResponse>` (query with response)
- `INotification` (event)
- **Dual implementations** (same class implements both IRequest and INotification)

### Handler Types
- `IRequestHandler<TRequest>`
- `IRequestHandler<TRequest, TResponse>`
- `INotificationHandler<TNotification>`
- `IStreamRequestHandler<TRequest, TResponse>` (streaming)
- `IRequestExceptionHandler<TRequest, TResponse, TException>` (exception handling)
- `IRequestExceptionAction<TRequest, TException>` (exception actions)
- **Multiple handlers** per request/notification (supported)

### Usage Patterns
- Direct calls: `_mediator.Send(request)`, `_mediator.Publish(notification)`
- Variable usage: `var result = await _mediator.Send(myRequest)`
- Parameter passing: method calls with MediatR requests as parameters

## Current Status & Known Issues

### Completed Features ✅
- Basic request → handler navigation
- Handler → usage navigation
- Multi-handler selection dialog
- Context-aware command visibility
- Nested call support (mixed contexts)
- Handle method navigation (not just class)
- Symbol-based type matching
- Item templates integration
- Parallel project processing

### Roadmap Items (from ROADMAP.md) 📋
**Phase 1 - Correctness & UX (High Priority)**:
- Cache invalidation fixes
- Navigate to Handle method location
- Symbol-based handler matching
- Dynamic dialog titles
- Thread-safe workspace initialization
- Cancellation token support

**Phase 2 - Performance (Medium Priority)**:
- Scope searches to MediatR projects only
- Negative-result caching with TTL

**Phase 3 - Feature Coverage (Medium Priority)**:
- Streaming support (`IStreamRequest<T>`, `IStreamRequestHandler<T,R>`)
- Enhanced usage detection (interface-typed variables)
- VS navigation robustness (IVsUIShellOpenDocument vs DTE)
- Grouped selection UI

**Phase 4 - Templates & Docs (Low-Medium Priority)**:
- Fix handler template signatures
- Documentation updates
- Version bumping

## Cursor Guidelines for Future Development

### 1. Code Standards & Patterns

#### Threading & Async
- **MANDATORY**: All Roslyn work must be async with CancellationToken
- Use `ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync()` **only** for:
  - UI operations (dialogs, InfoBars)
  - DTE/VS service calls
  - Command registration in InitializeAsync
- **NEVER** use `.Result`, `.Wait()`, or `ConfigureAwait(false)`
- Thread CancellationToken through all service methods

#### Roslyn Best Practices
- Prefer **symbol-first** approaches over syntax analysis
- Use `SymbolEqualityComparer.Default.Equals()` for type comparisons
- Handle: partial classes, nested types, file-scoped namespaces, global usings
- Always check `semanticModel != null` before use
- Use `GetDeclaredSymbol()` for type declarations
- Scope searches to projects that reference MediatR (`compilation.GetTypeByMetadataName("MediatR.IRequest")`)

#### Error Handling
- Use early returns and minimal nesting
- Handle edge cases first
- Provide meaningful error messages to users
- Log debug information with consistent prefixes: `"MediatRNavigationExtension: [Service]: [Message]"`

#### Performance
- Use parallel processing: `Task.WhenAll()` for project scanning
- Filter syntax trees early (C# files only)
- Check MediatR availability before processing projects
- Implement caching where appropriate (but ensure proper invalidation)

### 2. File Modification Guidelines

#### Core Services (`Services/*`)
- **Extend existing services** rather than creating new ones
- Follow established patterns in `MediatRCommandHandler`
- Maintain separation of concerns:
  - `MediatRCommandHandler`: Orchestration
  - `MediatRNavigationService`: Navigation & UI
  - `MediatRHandlerFinder`/`MediatRUsageFinder`: Discovery
  - `MediatRContextService`: Context detection

#### Pattern Matching (`MediatRPatternMatcher.cs`)
- Centralize all MediatR pattern logic here
- Keep interface checks consistent (`ContainingNamespace?.ToDisplayString() == "MediatR"`)
- Handle generic type arguments carefully
- Maintain backward compatibility with existing methods

#### VS Integration
- **VSPackage.vsct**: Add new commands to existing groups
- **VSIXExtentionPackage.cs**: Register commands in `RegisterCommandsAsync()`
- **source.extension.vsixmanifest**: Increment version for user-visible changes

#### UI Components
- **HandlerSelectionDialog.xaml**: Keep styling minimal and consistent
- Construct dialogs on UI thread only
- Populate ViewModels off-thread, then bind
- Validate XAML bindings and event names

### 3. Testing & Validation

#### Build Requirements
- Target: **AnyCPU**, **.NET Framework 4.8**
- Launch: Experimental instance with `/rootsuffix Exp`
- Test in VS 2022 (17.0+)

#### Functional Testing
- Test request → handler navigation (single & multiple handlers)
- Test handler → usage navigation (single & multiple usages)
- Test mixed contexts (nested calls within handlers)
- Test dual-interface implementations (IRequest + INotification)
- Verify context-sensitive command visibility
- Test cancellation behavior

#### Performance Testing
- Large solutions (100+ projects)
- Solutions without MediatR references
- Deeply nested namespace structures
- Generic type hierarchies

### 4. Common Pitfalls to Avoid

#### Threading Issues
- ❌ Don't call UI operations from background threads
- ❌ Don't block async operations with `.Result`/.Wait()`
- ❌ Don't use `ConfigureAwait(false)` in VS extensions

#### Roslyn Usage
- ❌ Don't rely on string-based type matching only
- ❌ Don't assume single interface implementation
- ❌ Don't ignore cancellation tokens
- ❌ Don't process non-C# projects

#### VS Integration
- ❌ Don't change target framework without approval
- ❌ Don't modify Publisher/Identity in manifest
- ❌ Don't break existing command IDs
- ❌ Don't skip version increments for user-visible changes

### 5. Development Workflow

#### Making Changes
1. **Read existing code** to understand patterns
2. **Extend existing services** rather than duplicating logic
3. **Test incrementally** with simple cases first
4. **Handle edge cases** (no handlers, multiple handlers, mixed types)
5. **Update documentation** if behavior changes

#### Code Review Checklist
- [ ] Async/await used correctly with CancellationToken
- [ ] UI thread switching only when necessary
- [ ] Symbol-based type comparisons
- [ ] Error handling with user-friendly messages
- [ ] Debug logging with consistent format
- [ ] No breaking changes to existing functionality
- [ ] Version incremented if user-visible changes
- [ ] Tests pass in experimental VS instance

### 6. Extension Points for New Features

#### Adding New MediatR Patterns
1. Update `MediatRPatternMatcher.cs` with new interface checks
2. Extend `GetAllRequestInfo()` or `GetHandlerInfo()` as needed
3. Add new handler types to `FindHandlersInProject()`/`FindNotificationHandlersInProject()`

#### Adding New Commands
1. Define command ID in `VSIXExtentionPackage.cs`
2. Add command definition to `VSPackage.vsct`
3. Register in `RegisterCommandsAsync()`
4. Implement in `MediatRCommandHandler.cs`
5. Add context detection to `MediatRContextService.cs`

#### Improving Performance
1. Add negative caching to `MediatRHandlerFinder`
2. Implement project-level MediatR detection
3. Add background pre-indexing
4. Optimize syntax tree filtering

### 7. Debugging & Diagnostics

#### Debug Output
- All services write to Debug output with consistent prefixes
- Use `System.Diagnostics.Debug.WriteLine()`
- Format: `"MediatRNavigationExtension: [ServiceName]: [Message]"`

#### Common Issues
- **Commands not appearing**: Check context detection logic
- **Navigation fails**: Verify file paths and locations
- **Performance issues**: Check if MediatR scoping is working
- **Threading exceptions**: Ensure proper UI thread switching

#### Diagnostic Tools
- VS Output window (Debug category)
- Experimental instance debugging
- Roslyn syntax visualizers
- VS SDK tools

## Conclusion

This extension provides a sophisticated MediatR navigation experience using modern Roslyn APIs and VS SDK patterns. The architecture is designed for maintainability, performance, and extensibility. When making changes, always prioritize correctness and user experience over implementation complexity.

The roadmap provides a clear path forward, with Phase 1 items being critical for production quality. Focus on completing high-priority items before adding new features.

For questions or clarifications, refer to the existing code patterns and this document. The extension follows established VS SDK conventions and should serve as a good reference for similar navigation extensions.
