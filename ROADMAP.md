## Roadmap for MediatR Navigation VSIX

#### Phase 1 — Correctness and UX (High priority)
- **Cache invalidation correctness**
  - Change `MediatRNavigationService` to call `ClearCacheForRequestType` with the request symbol, not the handler symbol.
  - Add `INamedTypeSymbol RequestTypeSymbol` to `Models/MediatRHandlerInfo` and set it in `MediatRPatternMatcher.GetHandlerInfo`.
  - Acceptance: cache clears actually affect subsequent searches; no stale results after file moves.

- **Navigate to Handle method location**
  - In `MediatRPatternMatcher.GetHandlerInfo`, resolve the `Handle(...)` method symbol and set its `Location`; fallback to type if missing.
  - Acceptance: navigation lands on the method signature, not the class declaration, for request/notification handlers.

- **Symbol-based handler matching (avoid same-name collisions)**
  - Replace name-only checks in `FindHandlersInProject`/`FindNotificationHandlersInProject` with robust symbol equality (extract comparer from `MediatRUsageFinder.AreTypesEqual`).
  - Acceptance: distinct types with identical short names no longer cross-match.

- **Dynamic dialog title**
  - Update `HandlerSelectionDialog` to accept a title; set via `NavigationUiService` for usage vs handler selection.
  - Acceptance: dialog titles read “Select Usage Location” for usages, “Select MediatR Handler” for handlers.

- **Thread-safe workspace init**
  - Move `WorkspaceService.InitializeWorkspace()` from `VSIXExtentionPackage` constructor to `InitializeAsync` (UI thread), or switch to async service acquisition.
  - Acceptance: no threading warnings; reliable workspace availability.

- **Cancellation tokens**
  - Thread `CancellationToken` through `MediatRCommandHandler`, `MediatRHandlerFinder`, `MediatRUsageFinder`, `MediatRPatternMatcher` calls.
  - Acceptance: operations cancel cleanly while running, UI remains responsive.

#### Phase 2 — Performance (Medium priority)
- **Scope to MediatR projects**
  - Before per-project analysis, skip projects where `compilation.GetTypeByMetadataName("MediatR.IRequest")` is null.
  - Acceptance: reduced CPU/time on large solutions without MediatR.

- **Negative-result cache with TTL**
  - Store “no handlers found” per request symbol with short TTL (e.g., 60s) to avoid repetitive full scans.
  - Acceptance: repeated searches for the same missing type do not rescan immediately; fresh results appear after edits/builds.

#### Phase 3 — Feature coverage (Medium priority)
- **Streaming support**
  - Add `IStreamRequest<T>` and `IStreamRequestHandler<TRequest,TResponse>` to: request/handler detection, handler info extraction, project scans, and UI grouping.
  - Acceptance: streaming requests navigate to streaming handlers; mixed lists show with correct prefixes.

- **Enhanced usage detection**
  - In `MediatRUsageFinder`, handle interface-typed variables/parameters better using `semanticModel.GetOperation` to find the concrete creation or flow.
  - Acceptance: usages found when sending interface-typed variables e.g., `IRequest<Foo>`.

- **VS navigation robustness**
  - Prefer `IVsUIShellOpenDocument`/editor services over raw DTE in `MediatRNavigationService.OpenDocumentAndNavigate`.
  - Acceptance: navigation works reliably across editor states and pinned tabs.

- **Grouped selection UI**
  - In multi-handler/usages, visually group by Request vs Notification, and Send vs Publish with headings; show short signature/context preview.
  - Acceptance: clearer selection in mixed results.

#### Phase 4 — Templates and docs (Low–medium priority)
- **Fix handler template signature**
  - Update `Templates/MediatRHandler.cs` to `IRequestHandler<$requestname$, Unit>` and return `Unit.Value`.
  - Add an additional template for `IRequest<TResponse>`.
  - Acceptance: new items compile against current MediatR conventions.

- **Update docs and bump manifest**
  - Update `Resources/README.md` and `Templates/README.md`; increment `Version` in `source.extension.vsixmanifest`.
  - Acceptance: version reflects changes; docs match behavior.

#### Phase 5 — Optional telemetry/logging (Low priority)
- **Lightweight debug logging**
  - Add a debug flag and central log helper; time scans and counts; keep off by default in release.
  - Acceptance: switchable logs aid support without impacting users.

### Sequencing and effort
- Week 1: Phase 1 (cache fix, `Handle` navigation, symbol equality, titles, workspace init, cancellation).
- Week 2: Phase 2 (project scoping, negative-result cache) + start streaming support.
- Week 3: Phase 3 (streaming complete, usage enhancements, robust navigation) + begin UI grouping.
- Week 4: Phase 4 (templates/docs/version), optional Phase 5.

### Risks/considerations
- Roslyn API usage for operations flow may need careful guards on older code patterns.
- Changing navigation APIs (DTE → VS services) requires UI-thread correctness; test on various editor states.
- Caching: ensure invalidation stays accurate after edits and solution reloads.

### Acceptance criteria (summary)
- Correct cache invalidation and `Handle` method navigation.
- Accurate matching across namespaces/generics; no collisions.
- Searches cancellable; scoped to MediatR projects.
- Streaming requests/handlers fully supported.
- Usage detection covers interface-typed scenarios.
- Dialogs have correct titles; grouped lists when mixed.
- Templates compile out-of-the-box; manifest version bumped.


