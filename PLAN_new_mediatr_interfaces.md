# Plan: Add Support for Additional MediatR Interfaces

## Context

The extension currently supports navigation (Go to Handler, Go to Usage) and CodeLens for `IRequestHandler`, `INotificationHandler`, `IStreamRequestHandler`, `IRequestExceptionHandler`, and `IRequestExceptionAction`. The goal is to add support for four more MediatR interfaces: `IPipelineBehavior`, `IStreamPipelineBehavior`, `IRequestPreProcessor`, and `IRequestPostProcessor`.

> **Note:** `INotificationHandler` is **already fully supported** — no changes needed for it.

---

## Files to Modify (6 files)

### 1. `Models/MediatRHandlerInfo.cs`
Add 4 new values to the `MediatRHandlerType` enum (after line 10):
```csharp
PipelineBehavior,
StreamPipelineBehavior,
RequestPreProcessor,
RequestPostProcessor
```

---

### 2. `MediatRPatternMatcher.cs`

**a) Add 4 new constants** (after line 22):
```csharp
private const string PipelineBehaviorInterface = "IPipelineBehavior";
private const string StreamPipelineBehaviorInterface = "IStreamPipelineBehavior";
private const string RequestPreProcessorInterface = "IRequestPreProcessor";
private const string RequestPostProcessorInterface = "IRequestPostProcessor";
```

**b) Expand `IsMediatRHandler()` OR chain** (lines 112–116) to add:
```csharp
i.Name == PipelineBehaviorInterface ||
i.Name == StreamPipelineBehaviorInterface ||
i.Name == RequestPreProcessorInterface ||
i.Name == RequestPostProcessorInterface
```
Also update namespace checks so pre/post processors are matched from `MediatR.Pipeline` (not only `MediatR`).
Recommended shape:
```csharp
(i.ContainingNamespace?.ToDisplayString() == "MediatR" &&
 (i.Name == RequestHandlerInterface ||
  i.Name == NotificationHandlerInterface ||
  i.Name == StreamRequestHandlerInterface ||
  i.Name == RequestExceptionHandlerInterface ||
  i.Name == RequestExceptionActionInterface ||
  i.Name == PipelineBehaviorInterface ||
  i.Name == StreamPipelineBehaviorInterface))
||
(i.ContainingNamespace?.ToDisplayString() == "MediatR.Pipeline" &&
 (i.Name == RequestPreProcessorInterface ||
  i.Name == RequestPostProcessorInterface))
```

**c) Add 4 `else if` branches in `GetHandlerInfo()`** (before `return null` at line 233).
Each maps to the correct method name: `Handle` for `IPipelineBehavior` and `IStreamPipelineBehavior`; `Process` for `IRequestPreProcessor` and `IRequestPostProcessor`.
- `IPipelineBehavior<TRequest, TResponse>` → 2 type args, method "Handle", type `PipelineBehavior`, `IsStreamHandler=false`
- `IStreamPipelineBehavior<TRequest, TResponse>` → 2 type args, method "Handle", type `StreamPipelineBehavior`, `IsStreamHandler=true`
- `IRequestPreProcessor<TRequest>` → 1 type arg, method "Process", type `RequestPreProcessor`, `ResponseTypeName=null`
- `IRequestPostProcessor<TRequest, TResponse>` → 2 type args, method "Process", type `RequestPostProcessor`

Important: extend the interface iteration filter to include both namespaces:
```csharp
i.ContainingNamespace?.ToDisplayString() == "MediatR" ||
i.ContainingNamespace?.ToDisplayString() == "MediatR.Pipeline"
```

**d) Add cases to `GetHandlerTypeDescription()` switch** (before `default`):
```csharp
case MediatRHandlerType.PipelineBehavior: return "pipeline behavior";
case MediatRHandlerType.StreamPipelineBehavior: return "stream pipeline behavior";
case MediatRHandlerType.RequestPreProcessor: return "pre-processor";
case MediatRHandlerType.RequestPostProcessor: return "post-processor";
```

**e) Extend `hasMediatR` check in `FindHandlersInSolutionBySymbol()`** (lines 295–302), append:
```csharp
|| compilation.GetTypeByMetadataName("MediatR.IPipelineBehavior`2") != null
|| compilation.GetTypeByMetadataName("MediatR.IStreamPipelineBehavior`2") != null
|| compilation.GetTypeByMetadataName("MediatR.Pipeline.IRequestPreProcessor`1") != null
|| compilation.GetTypeByMetadataName("MediatR.Pipeline.IRequestPostProcessor`2") != null
```

---

### 3. `Services/MediatRCommandHandler.cs`

**Line 235+** — update namespace filter in `GetRequestTypeFromContext()`:
```csharp
// From:
if (@interface.ContainingNamespace?.ToDisplayString() == "MediatR")

// To:
var ns = @interface.ContainingNamespace?.ToDisplayString();
if (ns == "MediatR" || ns == "MediatR.Pipeline")
```

**Line 237** — expand hardcoded interface whitelist in `GetRequestTypeFromContext()`:
```csharp
// From:
if ((@interface.Name == "IRequestHandler" || @interface.Name == "INotificationHandler") && @interface.TypeArguments.Length > 0)

// To:
if ((@interface.Name == "IRequestHandler" ||
     @interface.Name == "INotificationHandler" ||
     @interface.Name == "IPipelineBehavior" ||
     @interface.Name == "IStreamPipelineBehavior" ||
     @interface.Name == "IRequestPreProcessor" ||
     @interface.Name == "IRequestPostProcessor") &&
    @interface.TypeArguments.Length > 0)
```
This fixes "Go to Usage" for handlers of the new types (`TRequest` is always type arg index 0).

---

### 4. `Services/MediatRNavigationService.cs`

**`GetHandlerTypePrefix()` switch** (after line 345, before `default`):
```csharp
case MediatRHandlerType.PipelineBehavior: return "[Pipeline] ";
case MediatRHandlerType.StreamPipelineBehavior: return "[StreamPipeline] ";
case MediatRHandlerType.RequestPreProcessor: return "[PreProcessor] ";
case MediatRHandlerType.RequestPostProcessor: return "[PostProcessor] ";
```

**`GetHandlerTypeDisplayName()` switch** (after line 364, before `default`):
```csharp
case MediatRHandlerType.PipelineBehavior: return "Pipeline Behavior";
case MediatRHandlerType.StreamPipelineBehavior: return "Stream Pipeline Behavior";
case MediatRHandlerType.RequestPreProcessor: return "Request Pre-Processor";
case MediatRHandlerType.RequestPostProcessor: return "Request Post-Processor";
```

---

### 5. `CodeLensOopProvider/MediatRCodeLensProvider.cs`

**Line 81** — add `"Process"` to the OOP-side method filter:
```csharp
private static readonly string[] HandlerMethodNames = { "Handle", "Execute", "Process" };
```
Without this, `IRequestPreProcessor.Process` and `IRequestPostProcessor.Process` methods are filtered before they reach the VS-side callback.

---

### 6. `Services/CodeLensCallbackService.cs`

**Line 333** — add `"Process"` to the VS-side method filter:
```csharp
private static readonly string[] HandlerMethodNames = { "Handle", "Execute", "Process" };
```

---

## Implementation Order

1. `Models/MediatRHandlerInfo.cs` (enum must exist first)
2. `MediatRPatternMatcher.cs` (core detection, drives everything)
3. `Services/MediatRCommandHandler.cs`
4. `Services/MediatRNavigationService.cs`
5. `CodeLensOopProvider/MediatRCodeLensProvider.cs`
6. `Services/CodeLensCallbackService.cs`

---

## Verification

Create test classes in a project that references MediatR:
```csharp
using MediatR;
using MediatR.Pipeline;

public class MyQuery : IRequest<string> { }

public class MyBehavior : IPipelineBehavior<MyQuery, string>
{
    public async Task<string> Handle(MyQuery request, RequestHandlerDelegate<string> next, CancellationToken ct) => await next();
}
public class MyStreamBehavior : IStreamPipelineBehavior<MyQuery, string> { /* Handle */ }
public class MyPreProcessor : IRequestPreProcessor<MyQuery> { /* Process */ }
public class MyPostProcessor : IRequestPostProcessor<MyQuery, string> { /* Process */ }
```

Verify:
1. "Go to Handler" from `MyQuery` finds all 4 new types
2. "Go to Usage" from each new handler navigates to Send call sites
3. CodeLens appears on handler class declarations AND on Handle/Process methods
4. Multi-handler dialog shows correct `[Pipeline]`, `[PreProcessor]`, `[PostProcessor]` prefixes
5. Regression: `INotificationHandler` still works
6. `IRequestPreProcessor` / `IRequestPostProcessor` are detected when imported from `MediatR.Pipeline`

---

## Assumptions / Known Behavior

- `GetHandlerInfo()` currently returns the first matching MediatR interface for a type. This plan keeps that behavior (no multi-interface-per-class expansion in this change).
