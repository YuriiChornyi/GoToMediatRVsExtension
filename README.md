# MediatR Visual Studio 2022 Extension

This Visual Studio 2022 extension provides "Go To Implementation" functionality for MediatR requests, commands, queries, and notifications. It helps developers quickly navigate from MediatR requests to their corresponding handlers.

## Features

- **Automatic Detection**: Detects when you're positioned on a MediatR `IRequest`, `IRequest<T>`, or `INotification` implementation
- **Smart Navigation**: Finds and navigates to the corresponding `IRequestHandler<T>`, `IRequestHandler<T, TResponse>`, or `INotificationHandler<T>` implementations
- **Multiple Handler Support**: When multiple handlers exist (common with notifications), provides a selection dialog
- **Context-Aware Menu**: The "Go to MediatR Implementation" command only appears when you're on a MediatR-related type

## Supported MediatR Patterns

### Requests (Commands/Queries)
- `IRequest` → `IRequestHandler<TRequest>`
- `IRequest<TResponse>` → `IRequestHandler<TRequest, TResponse>`

### Notifications (Events)
- `INotification` → `INotificationHandler<TNotification>` (supports multiple handlers)

## Usage

### Method 1: Keyboard Shortcut
1. Position your cursor on a MediatR request/command/query/notification class name
2. Press `Ctrl+Alt+F12` to navigate to the handler

### Method 2: Context Menu
1. Position your cursor on a MediatR request/command/query/notification class name
2. Go to **Edit** menu → **Go to MediatR Implementation**

### Method 3: Right-click Context Menu
1. Right-click on a MediatR request/command/query/notification class name
2. Select **Go to MediatR Implementation** (when available)

## Examples

### Example 1: Simple Request
```csharp
// Request class
public class GetUserQuery : IRequest<User>
{
    public int UserId { get; set; }
}

// Handler class (will be found by the extension)
public class GetUserQueryHandler : IRequestHandler<GetUserQuery, User>
{
    public async Task<User> Handle(GetUserQuery request, CancellationToken cancellationToken)
    {
        // Implementation
    }
}
```

### Example 2: Command without Response
```csharp
// Command class
public class DeleteUserCommand : IRequest
{
    public int UserId { get; set; }
}

// Handler class (will be found by the extension)
public class DeleteUserCommandHandler : IRequestHandler<DeleteUserCommand>
{
    public async Task Handle(DeleteUserCommand request, CancellationToken cancellationToken)
    {
        // Implementation
    }
}
```

### Example 3: Notification with Multiple Handlers
```csharp
// Notification class
public class UserCreatedNotification : INotification
{
    public User User { get; set; }
}

// Multiple handlers (extension will show selection dialog)
public class EmailNotificationHandler : INotificationHandler<UserCreatedNotification>
{
    public async Task Handle(UserCreatedNotification notification, CancellationToken cancellationToken)
    {
        // Send email
    }
}

public class LoggingNotificationHandler : INotificationHandler<UserCreatedNotification>
{
    public async Task Handle(UserCreatedNotification notification, CancellationToken cancellationToken)
    {
        // Log event
    }
}
```

## Installation

1. Build the solution to generate the VSIX file
2. Double-click the generated `.vsix` file to install the extension
3. Restart Visual Studio 2022
4. The extension will be active and ready to use

## Requirements

- Visual Studio 2022 (Version 17.0 or later)
- .NET Framework 4.7.2 or later
- Projects using MediatR package

## Troubleshooting

### Command Not Appearing
- Ensure you're positioned on a class that implements `IRequest`, `IRequest<T>`, or `INotification`
- Make sure the solution compiles without errors
- Verify that MediatR package is referenced in your project

### Handler Not Found
- Ensure the handler class implements the correct interface (`IRequestHandler<T>`, `IRequestHandler<T, TResponse>`, or `INotificationHandler<T>`)
- Verify that both the request and handler are in the same solution
- Check that the solution is compiled and up-to-date

### Multiple Handlers Selection
- For notifications, if multiple handlers exist, a dialog will appear
- Select the desired handler from the list and click OK
- Double-click on a handler name for quick selection

## Development

This extension uses:
- Visual Studio SDK 17.x
- Roslyn Code Analysis APIs
- MEF (Managed Extensibility Framework)

### Project Structure
- `VSIXExtentionPackage.cs` - Main package and command registration
- `MediatRPatternMatcher.cs` - Logic for detecting MediatR patterns
- `MediatRNavigationService.cs` - Navigation and handler location logic
- `MediatRGoToImplementationProvider.cs` - Integration with VS text editing
- `VSPackage.vsct` - Command definitions and keyboard shortcuts

## Contributing

Feel free to submit issues and enhancement requests!

## License

This project is licensed under the MIT License. 