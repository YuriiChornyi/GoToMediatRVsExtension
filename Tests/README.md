# Tests

Unit tests for the Roslyn analysis code behind the extension's navigation commands.

```bash
dotnet test Tests/VSIXExtension.Tests/VSIXExtension.Tests.csproj
```

They run in seconds, need no Visual Studio, and run on every PR and every push to `main`
(see `.github/workflows/pr-validation.yml` and `build-and-publish.yml`). `scripts/build-local.ps1 -Test`
runs the same suite before building the VSIX. Open `Tests/VSIXExtension.Tests.sln` to use Test Explorer.

## How it is put together

The extension is a **net48 VSIX project** that only builds with the Visual Studio SDK installed, so a
normal `ProjectReference` would make the tests unrunnable in CI. Instead the test project **links**
the production sources that depend on Roslyn alone:

| Linked source | What it does |
| --- | --- |
| `MediatRPatternMatcher.cs` | Recognises requests and handlers, finds handlers across the solution |
| `Services/MediatRUsageFinder.cs` | Finds `Send`/`Publish` call sites for a request |
| `Models/*.cs` | The result types |

`MediatRUsageFinder` takes a `WorkspaceService` in its constructor, and the real one wraps
`VisualStudioWorkspace`. `Infrastructure/WorkspaceServiceStub.cs` declares a narrow stand-in with the
same name and namespace, exposing only the `GetWorkspaceAsync()` the finder actually calls. If the
finder starts using more of `WorkspaceService`, that file stops compiling — which is the signal to
widen the stub or extract a `Solution`-based seam in the production code.

`Infrastructure/MediatRTestWorkspace.cs` builds an in-memory `AdhocWorkspace` from source strings:
real compilations, real file paths, multiple projects with references between them, and the **real
MediatR 12 assemblies** as metadata references. Using the real package rather than hand-written
marker interfaces is deliberate — it means the tests notice when MediatR moves an interface between
namespaces. Fixtures that fail to compile throw with the compiler diagnostics rather than silently
reporting "no handlers found".

## What is covered

- `RequestDetectionTests` — `IsMediatRRequest`, `GetRequestInfo`, `GetAllRequestInfo`,
  `ImplementsBothRequestAndNotification`, including records and inherited requests.
- `HandlerDetectionTests` — `IsMediatRHandler` / `GetHandlerInfo` for every supported handler shape,
  and which line the returned `Location` points at.
- `HandlerDiscoveryTests` — the solution-wide search: cross-project discovery, the syntax-tree
  extension filter (including generated `.razor.g.cs`), deduplication, and the abstract-base rules.
- `UsageDiscoveryTests` — `Send`/`Publish` detection through `IMediator`/`ISender`/`IPublisher`,
  resolving a concrete request from a variable typed as the interface, and ignoring lookalike
  `Send` methods on non-MediatR services.

## What is NOT covered

Anything that touches `Microsoft.VisualStudio.*` cannot be linked here and has no automated
coverage: `WorkspaceService`, `MediatRContextService`, `MediatRCommandHandler`, `NavigationUiService`,
`HandlerSelectionDialog`, `CodeLensCallbackService`, `Options`, `VSIXExtentionPackage`, and
`CodeLensOopProvider`. Verify those by launching the experimental instance (`F5`).

Most of `MediatRContextService` is VS-dependent only at its edges — every public method resolves an
`ITextView` to a Roslyn `Document` plus a `TextSpan` and is pure Roslyn from there. Splitting those
methods into a thin `ITextView` adapter over a `(Document, TextSpan)` core would make the bulk of it
testable here too.

## Known gaps flagged by the suite

Two tests are `Skip`ped because they describe correct behaviour the code does not have yet: MediatR
declares `IRequestExceptionHandler` and `IRequestExceptionAction` in the `MediatR.Pipeline`
namespace, but `MediatRPatternMatcher.IsMediatRHandler` only accepts those two names under `MediatR`,
so exception handlers are never recognised. `GetHandlerInfo` already handles them correctly —
`IsMediatRHandler` is the gate that rejects them first. Unskip both once the namespace check is fixed.
