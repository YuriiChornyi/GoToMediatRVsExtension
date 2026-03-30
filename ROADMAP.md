## Roadmap for MediatR Navigation VSIX

### Completed

- **Navigate to Handle/Execute method location** — `GetHandlerInfo()` resolves method symbol via `FindImplementationForInterfaceMember`; navigation lands on the method signature, not the class declaration.
- **Symbol-based handler matching** — `AreTypesEqual()` uses 3-tier comparison (symbol equality → fully-qualified display string → metadata name + assembly name); no same-name collisions across namespaces.
- **Scope searches to MediatR projects** — `FindHandlersInSolutionBySymbol()` skips projects where MediatR types are not found in the compilation.
- **Parallel project scanning** — `Task.WhenAll()` across all projects in the solution.
- **Cancellation tokens** — threaded through `MediatRCommandHandler`, handler/usage finders, and pattern matcher.
- **Streaming handler support** — `IStreamRequestHandler<TRequest, TResponse>` fully supported in detection, navigation, and CodeLens.
- **Exception handler support** — `IRequestExceptionHandler<TRequest, TResponse, TException>` and `IRequestExceptionAction<TRequest, TException>` supported.
- **Nested call navigation** — cursor on `_mediator.Send(new MyQuery())` inside a handler body triggers navigation to `MyQuery`'s handler.
- **CodeLens** — shows handler count and usage count inline above MediatR type declarations and their `Handle`/`Execute` methods; expandable detail pane with file/line links; OOP provider with negative cache; configurable refresh delay.
- **Options page** — Tools → Options → MediatR Navigation (enable/disable CodeLens, refresh delay, enable/disable commands).
- **Item templates** — Command, Handler, Notification, Notification Handler scaffolding templates.
- **Dual `IRequest`+`INotification`** — handles types implementing both; navigates to both request and notification handlers.

---

### Upcoming

#### New MediatR interface support (next)
Add navigation and CodeLens for:
- `IPipelineBehavior<TRequest, TResponse>` — `Handle` method
- `IStreamPipelineBehavior<TRequest, TResponse>` — `Handle` method
- `IRequestPreProcessor<TRequest>` — `Process` method
- `IRequestPostProcessor<TRequest, TResponse>` — `Process` method

See `PLAN_new_mediatr_interfaces.md` for the detailed implementation plan.

#### UX improvements
- **Dynamic dialog title** — "Select MediatR Handler" vs "Select Usage Location" depending on context.
- **Grouped selection UI** — visual headings in multi-handler/usage dialogs grouping by handler type (Request/Notification/Pipeline) and usage type (Send/Publish).

#### Robustness
- **VS navigation API** — prefer `IVsUIShellOpenDocument` over raw DTE in `MediatRNavigationService.OpenDocumentAndNavigate` for better reliability across editor states.
- **Interface-typed usage detection** — improve `MediatRUsageFinder` to track through `IRequest<Foo>`-typed variables using `IOperation` flow analysis.

#### Templates
- **Fix handler template** — update `Templates/MediatRHandler.cs` to use `IRequestHandler<TRequest, Unit>` and `return Unit.Value` to match current MediatR conventions.
