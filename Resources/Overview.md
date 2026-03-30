# MediatR Navigation Extension (Visual Studio 2022+)

Bidirectional navigation between MediatR requests, handlers, and call sites — plus CodeLens showing handler and usage counts inline.

## Commands

| Command | From | To | Shortcut |
|---------|------|----|----------|
| **Go to MediatR Implementation** | Request/Command/Query/Notification | Handler(s) | `Ctrl+Alt+F12` |
| **Go to MediatR Send/Publish** | Handler | Send/Publish call sites | `Ctrl+Alt+F11` |

Commands are context-aware — only the relevant command appears based on cursor position.

**Go to MediatR Implementation**
![Animation.gif](Animation.gif)

**Go to MediatR Send/Publish**
![Animation2.gif](Animation2.gif)

## Features

### Go to MediatR Implementation (Request → Handler)
- Detects `IRequest`, `IRequest<T>`, and `INotification` implementations
- Navigates directly to the handler's `Handle`/`Execute` method signature
- Multi-handler selection dialog when multiple handlers exist
- Also works when cursor is on a nested `_mediator.Send(new MyQuery())` call inside a handler

### Go to MediatR Send/Publish (Handler → Usage)
- Finds all `Send()`, `SendAsync()`, `Publish()`, and `PublishAsync()` call sites in the solution
- Detects usages through `IMediator`, `ISender`, and `IPublisher` interfaces
- Handles direct instantiation, variable references, and conditional access (`?.`) patterns
- Multi-usage selection dialog with Send vs Publish grouping

### CodeLens
- Shows handler count and usage count inline above every MediatR type declaration
- Also shows on individual `Handle`, `Execute`, and `Process` method signatures
- Click to expand a detail pane listing all handlers and usages with file and line info
- Configurable via **Tools → Options → MediatR Navigation**

### Smart Context Detection
- Commands appear only when the cursor is on a relevant MediatR symbol
- Works on request/notification classes, handler classes, handler methods, and nested Send/Publish calls

## Supported MediatR Patterns

### Request types
- `IRequest` — command without response
- `IRequest<TResponse>` — query with response
- `INotification` — event/notification
- A single class implementing both `IRequest` and `INotification`

### Handler types
| Interface | Method | Notes |
|-----------|--------|-------|
| `IRequestHandler<TRequest>` / `IRequestHandler<TRequest, TResponse>` | `Handle` | |
| `INotificationHandler<TNotification>` | `Handle` | Multiple handlers supported |
| `IStreamRequestHandler<TRequest, TResponse>` | `Handle` | |
| `IRequestExceptionHandler<TRequest, TResponse, TException>` | `Handle` | |
| `IRequestExceptionAction<TRequest, TException>` | `Execute` | |

### Usage patterns detected
- `_mediator.Send(new MyRequest())`
- `_mediator.SendAsync(request)`
- `_mediator.Publish(new MyNotification())`
- `_mediator.PublishAsync(notification)`
- `_mediator?.Send(...)` (conditional access)
- Variable and field references passed to Send/Publish

## Usage

### Request → Handler
1. Position cursor on a MediatR request/command/query/notification class name
2. Press `Ctrl+Alt+F12`, or use **Edit** menu, or right-click context menu
3. If multiple handlers exist, a selection dialog appears

### Handler → Usage
1. Position cursor on a handler class name or inside a `Handle`/`Execute` method
2. Press `Ctrl+Alt+F11`, or use **Edit** menu, or right-click context menu
3. If multiple usages exist, a selection dialog appears

### Nested navigation
When inside a handler method that contains a nested `_mediator.Send(new OtherQuery())` call:
- `Ctrl+Alt+F12` navigates to `OtherQuery`'s handler
- `Ctrl+Alt+F11` navigates to where the current handler's request is sent

## Settings

**Tools → Options → MediatR Navigation**

| Setting | Default | Description |
|---------|---------|-------------|
| Enable CodeLens | true | Show/hide CodeLens annotations |
| CodeLens Refresh Delay (seconds) | 3 | Debounce delay after workspace changes |
| Enable Go to Implementation | true | Enable/disable the command |
| Enable Go to Usage | true | Enable/disable the command |

## Item Templates

**Add New Item → MediatR** — four scaffolding templates:
- **MediatR Command** — `IRequest` class
- **MediatR Handler** — `IRequestHandler<T>` class
- **MediatR Notification** — `INotification` class
- **MediatR Notification Handler** — `INotificationHandler<T>` class

## Requirements

- Visual Studio 2022–2026 (17.0–19.0)
- .NET Framework 4.7.2+
- Project references MediatR

## Installation

Install from the [Visual Studio Marketplace](https://marketplace.visualstudio.com) or via **Extensions → Manage Extensions** (search "MediatR Navigation").

## Troubleshooting

**Commands not appearing:** Ensure you're in a C# file, the caret is on a relevant symbol, the solution builds, and MediatR is referenced.

**Handler not found:** Rebuild the solution; verify the handler implements the correct interface and is in the same solution.

**Usage not found:** Confirm the request is sent via `_mediator.Send()` or `_mediator.Publish()`; rebuild the solution.

## License

MIT
