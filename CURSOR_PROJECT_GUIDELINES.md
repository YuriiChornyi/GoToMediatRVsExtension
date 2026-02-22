# MediatR Navigation Extension — Project Guidelines

## Project Overview

**MediatR Navigation Extension** is a Visual Studio 2022–2026 extension (.VSIX) that provides Roslyn-powered bidirectional navigation between MediatR requests/notifications and their handlers, plus VS item templates for scaffolding MediatR types.

- **Go to MediatR Implementation** (`Ctrl+Alt+F12`) — from a request/notification (or a nested `_mediator.Send/Publish` call) to its handler(s)
- **Go to MediatR Send/Publish** (`Ctrl+Alt+F11`) — from a handler (or a nested call inside a handler) back to all `Send/Publish` call sites
- Commands appear only when the cursor is in a relevant context (context-aware visibility via `BeforeQueryStatus`)
- Multi-result selection dialog when more than one handler or usage is found

---

## Technology Stack

| Concern | Detail |
|---|---|
| Target framework | .NET Framework 4.8 |
| Visual Studio target | 2022–2026 (17.0–19.0, amd64) |
| Language | C# |
| Roslyn | `Microsoft.CodeAnalysis.CSharp` 4.14.0 + `Microsoft.VisualStudio.LanguageServices` 4.14.0 |
| VS SDK | `Microsoft.VisualStudio.SDK` 17.14.40265 |
| Threading | `Microsoft.VisualStudio.Threading` 17.14.15 (`JoinableTaskFactory`) |
| VS Toolkit | `Community.VisualStudio.Toolkit.17` 17.0.533 |
| UI | WPF (`HandlerSelectionDialog.xaml`) |
| Build | `Microsoft.VSSDK.BuildTools` 17.14.2094 |

---

## Project Structure

```
VSIXExtention/
├── Services/
│   ├── MediatRCommandHandler.cs       # Orchestration — coordinates the full navigation flow
│   ├── MediatRContextService.cs       # Context detection — what is the cursor positioned on?
│   ├── MediatRHandlerFinder.cs        # Handler discovery (thin wrapper over MediatRPatternMatcher)
│   ├── MediatRUsageFinder.cs          # Send/Publish call-site discovery
│   ├── MediatRNavigationService.cs    # VS navigation + multi-result selection UI
│   ├── NavigationUiService.cs         # Dialogs, progress bar, message boxes
│   └── WorkspaceService.cs            # Roslyn VisualStudioWorkspace access
├── Models/
│   ├── MediatRHandlerInfo.cs          # Handler metadata (implements Equals/GetHashCode)
│   ├── MediatRRequestInfo.cs          # Request/notification metadata
│   ├── MediatRUsageInfo.cs            # Usage call-site metadata (implements Equals/GetHashCode)
│   ├── HandlerDisplayInfo.cs          # UI display wrapper for handlers
│   └── UsageDisplayInfo.cs            # UI display wrapper for usages
├── Helpers/
│   └── RoslynSymbolHelper.cs          # Standalone utility: file path → (SemanticModel, INamedTypeSymbol)
├── Templates/                         # VS item templates (compiled to ZIP in VSIX)
│   ├── MediatRCommand.cs/.vstemplate
│   ├── MediatRHandler.cs/.vstemplate
│   ├── MediatRNotification.cs/.vstemplate
│   └── MediatRNotificationHandler.cs/.vstemplate
├── MediatRPatternMatcher.cs           # Core MediatR pattern recognition (static)
├── HandlerSelectionDialog.cs/.xaml    # WPF multi-result selection dialog
├── VSIXExtentionPackage.cs            # AsyncPackage entry point, command registration
├── VSPackage.vsct                     # Command/menu/keyboard shortcut definitions
└── source.extension.vsixmanifest      # Extension identity and metadata
```

---

## Architecture

```
VSIXExtentionPackage          ← VS entry point, constructs service graph
    └── MediatRCommandHandler ← orchestration
          ├── MediatRContextService    ← cursor context detection
          ├── MediatRHandlerFinder     ← handler discovery
          │     └── MediatRPatternMatcher (static)
          ├── MediatRUsageFinder       ← Send/Publish call-site discovery
          └── MediatRNavigationService ← navigation + UI
                └── NavigationUiService
                      └── HandlerSelectionDialog (WPF)

WorkspaceService  ← injected into all services that need Roslyn workspace
```

`VSIXExtentionPackage` is the composition root — it constructs all services in `InitializeAsync` and passes them down. Do not introduce a DI container.

---

## Supported MediatR Patterns

### Request types
- `IRequest` — command without response
- `IRequest<TResponse>` — query with response
- `INotification` — event/notification
- Dual implementation: a single class implementing both `IRequest` and `INotification`

### Handler types (`MediatRHandlerType` enum)
| Enum value | Interface |
|---|---|
| `RequestHandler` | `IRequestHandler<TRequest>` / `IRequestHandler<TRequest, TResponse>` |
| `NotificationHandler` | `INotificationHandler<TNotification>` |
| `StreamRequestHandler` | `IStreamRequestHandler<TRequest, TResponse>` |
| `RequestExceptionHandler` | `IRequestExceptionHandler<TRequest, TResponse, TException>` |
| `RequestExceptionAction` | `IRequestExceptionAction<TRequest, TException>` |

### Usage patterns detected
- Direct: `_mediator.Send(request)`, `_mediator.Publish(notification)`
- Async: `await _mediator.SendAsync(...)`, `await _mediator.PublishAsync(...)`
- Conditional access: `_mediator?.Send(...)`
- Nested: cursor inside a method body that contains a `Send/Publish` call

---

## Coding Standards

### Threading — mandatory rules

- **All Roslyn work is async.** Every method that touches `SemanticModel`, `Compilation`, or `ISymbol` must be `async Task<T>` and accept a `CancellationToken`.
- **Switch to the UI thread only when necessary** — dialogs, DTE calls, `IVsStatusbar`, command registration:
  ```csharp
  await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
  ```
- **Never use `.Result`, `.Wait()`, or `Task.Run(...).Result`** — these deadlock VS extensions.
- **Never use `ConfigureAwait(false)`** in VS extension code — the VS threading model requires the joinable task context.
- Thread `CancellationToken` through every async method signature.

### Roslyn best practices

- **Symbol-first, not string-first.** Use `SymbolEqualityComparer.Default.Equals()` as the primary comparison. Fall back to fully-qualified display string, then metadata name + assembly name (the 3-tier pattern already in `AreTypesEqual()`).
- **Syntax before semantics for context detection.** Do cheap syntax checks (file extension, content type, method name string match) before acquiring a `SemanticModel`. See `MediatRContextService.IsValidContext()` and `IsNestedMediatRCall()`.
- **Scope to MediatR projects early.** Skip projects where `compilation.GetTypeByMetadataName("MediatR.IRequest") == null`.
- **Parallel project scanning.** Use `Task.WhenAll()` across projects — see `MediatRPatternMatcher.FindHandlersInSolutionBySymbol()`.
- Always null-check `semanticModel` and `symbol` before use.
- Use `GetDeclaredSymbol()` for type declarations, `GetSymbolInfo().Symbol` for references.
- Navigate to the `Handle`/`Execute` **method location**, not just the class — see `GetHandlerInfo()` using `FindImplementationForInterfaceMember`.

### Error handling

- Use early returns and guard clauses — minimize nesting.
- Return `NavigationResult.CreateFailure(reason, message)` rather than throwing for expected failures.
- Show user-facing errors via `NavigationUiService.ShowErrorMessageAsync()` — never `MessageBox.Show()` directly.
- Log all debug output with the consistent prefix:
  ```
  MediatRNavigationExtension: [ServiceName]: [message]
  ```
  using `System.Diagnostics.Debug.WriteLine()`.

### Model equality

`MediatRHandlerInfo` and `MediatRUsageInfo` implement `Equals`/`GetHashCode` for use in `HashSet<T>` deduplication. When adding new fields to these models, update both methods.

---

## File Modification Guidelines

### `MediatRPatternMatcher.cs`
The single source of truth for all MediatR interface recognition. All interface name constants live here. When adding support for a new MediatR interface:
1. Add a constant for the interface name.
2. Extend `IsMediatRHandler()` / `GetHandlerInfo()` to recognize it.
3. Add a value to `MediatRHandlerType` if it's a new handler category.
4. Keep `AreTypesEqual()` unchanged — it is the canonical comparison method.

### `MediatRContextService.cs`
Context detection must remain performance-first:
1. `IsValidContext()` fast-exit first (no semantic work).
2. Syntax-level checks before `GetSemanticModelAsync()`.
3. Only acquire `SemanticModel` when syntax checks pass.

### `MediatRCommandHandler.cs`
Orchestration only — no Roslyn analysis, no UI. If you find yourself doing symbol work here, move it to `MediatRContextService`, `MediatRHandlerFinder`, or `MediatRUsageFinder`.

### `WorkspaceService.cs`
Dual-init pattern (explicit set from `InitializeAsync` + lazy fallback). Do not remove the lazy fallback — it is needed when the workspace is accessed before `InitializeAsync` completes.

### `VSIXExtentionPackage.cs`
- Register new commands in `RegisterCommandsAsync()` following the existing `OleMenuCommand` + `BeforeQueryStatus` pattern.
- All service construction happens here (composition root). Keep it that way.
- `GetActiveTextView()` and `GetCaretOrSelectionPosition()` are the canonical way to get the editor context — do not duplicate this logic.

### `VSPackage.vsct`
- Add new commands to existing groups — do not create new groups unless adding a genuinely separate feature area.
- Command IDs are defined as constants in `VSIXExtentionPackage.cs` — keep them in sync.

### `source.extension.vsixmanifest`
- **Increment `Version`** for every user-visible change before opening a PR (the PR validation workflow enforces this).
- **Never change** `Publisher`, `Id`, or the `Identity` GUID — these are the marketplace identity.
- Version format is `X.Y` (e.g., `6.4`) — two-part, not three-part.

### Item Templates (`Templates/`)
- Templates use `$rootnamespace$` and `$safeitemname$` standard VS parameters.
- Handler templates use custom parameters (`$requestname$`, `$notificationname$`) with defaults.
- After editing a template `.cs` file, verify the corresponding `.vstemplate` `DefaultName` and `ProjectItem` reference are still correct.

---

## Adding New Features

### New MediatR interface support
1. Add interface name constant to `MediatRPatternMatcher`.
2. Extend `IsMediatRHandler()` or `IsMediatRRequest()`.
3. Extend `GetHandlerInfo()` to populate the new `MediatRHandlerType`.
4. Update `MediatRNavigationService.FormatHandlerDisplayText()` to show the new type prefix in the selection dialog.

### New command
1. Define the command ID constant in `VSIXExtentionPackage.cs`.
2. Add command + keyboard shortcut to `VSPackage.vsct`.
3. Register in `RegisterCommandsAsync()` with a `BeforeQueryStatus` handler.
4. Implement execution in `MediatRCommandHandler.cs`.
5. Add context detection method to `MediatRContextService.cs` if needed.

### New context detection scenario
Add a method to `MediatRContextService` following the existing pattern:
1. `IsValidContext()` guard first.
2. Syntax check (cheap).
3. Semantic check (expensive, only if syntax passes).
4. Wire it into the `BeforeQueryStatus` handler in `VSIXExtentionPackage.cs`.

---

## CI/CD Workflow

| Workflow | Trigger | What it does |
|---|---|---|
| `pr-validation.yml` | PR to `main` | Build (Debug) + verify version in manifest is higher than latest `v*` tag |
| `build-and-publish.yml` | Push to `main` | Build (Release) + create GitHub Release tagged `v{version}` with VSIX attached and commit messages as notes |
| `marketplace-publish.yml` | Release published or manual | Build + publish to VS Marketplace via `VsixPublisher.exe` |

**Required secret:** `MARKETPLACE_PAT` — Azure DevOps PAT with `Marketplace (Publish)` scope.

**Release flow:**
1. Bump `Version` in `source.extension.vsixmanifest` on your feature branch.
2. Open PR → `pr-validation.yml` runs and enforces the version bump.
3. Merge to `main` → `build-and-publish.yml` creates the GitHub Release automatically.
4. Optionally publish the release → `marketplace-publish.yml` pushes to the marketplace.

---

## Common Pitfalls

| Pitfall | Correct approach |
|---|---|
| `.Result` or `.Wait()` on async calls | `await` everywhere; use `JoinableTaskFactory.Run()` only at the outermost sync boundary |
| UI operations from background thread | `await SwitchToMainThreadAsync()` before any DTE/dialog/statusbar call |
| `ConfigureAwait(false)` | Never — VS threading model requires joinable task context |
| String-only type comparison | Use `AreTypesEqual()` (3-tier: symbol → display string → metadata+assembly) |
| Assuming one interface per class | Always call `GetAllRequestInfo()` — a class can implement both `IRequest` and `INotification` |
| Ignoring cancellation tokens | Pass `CancellationToken` through every async method |
| Processing non-C# / non-MediatR projects | Check `IsValidContext()` and MediatR availability before any semantic work |
| Changing manifest identity fields | `Publisher`, `Id`, and the GUID are the marketplace identity — never change them |
| Forgetting to bump the version | The PR will fail — bump `Version` in `source.extension.vsixmanifest` first |

---

## Debugging

- **Output window:** All services write to `Debug` output with the `MediatRNavigationExtension:` prefix — filter by this in the VS Output window.
- **Experimental instance:** Launch with `devenv.exe /rootsuffix Exp` (configured in the project debug settings).
- **Commands not appearing:** Check `BeforeQueryStatus` logic and the corresponding `IsIn*ContextAsync` method.
- **Navigation lands on wrong line:** Verify `GetHandlerInfo()` is resolving the `Handle` method location via `FindImplementationForInterfaceMember`, not the class declaration.
- **Performance slow on large solutions:** Confirm MediatR project scoping is active (`GetTypeByMetadataName("MediatR.IRequest") == null` early exit).
- **Threading exceptions:** Ensure `SwitchToMainThreadAsync()` is called before any UI/DTE operation.
