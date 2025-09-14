# MediatR Templates

This folder contains Visual Studio item templates for creating MediatR classes.

## Templates Included

### 1. MediatR Command Template
- **Name**: `MediatR Command`
- **Files**: `MediatRCommand.cs` + `MediatRCommand.vstemplate`
- **Description**: Creates a command class implementing `IRequest`
- **Usage**: Right-click in Solution Explorer → Add → New Item → MediatR Command

### 2. MediatR Handler Template
- **Name**: `MediatR Handler`
- **Files**: `MediatRHandler.cs` + `MediatRHandler.vstemplate`
- **Description**: Creates a handler class implementing `IRequestHandler<T>`
- **Usage**: Right-click in Solution Explorer → Add → New Item → MediatR Handler

### 3. MediatR Notification Template
- **Name**: `MediatR Notification`
- **Files**: `MediatRNotification.cs` + `MediatRNotification.vstemplate`
- **Description**: Creates a notification class implementing `INotification`
- **Usage**: Right-click in Solution Explorer → Add → New Item → MediatR Notification

### 4. MediatR Notification Handler Template
- **Name**: `MediatR Notification Handler`
- **Files**: `MediatRNotificationHandler.cs` + `MediatRNotificationHandler.vstemplate`
- **Description**: Creates a notification handler class implementing `INotificationHandler<T>`
- **Usage**: Right-click in Solution Explorer → Add → New Item → MediatR Notification Handler

## How It Works

1. The templates are packaged as ZIP files during build
2. They're included in the VSIX as ItemTemplate assets
3. After installing the extension, they appear in the "Add New Item" dialog
4. Visual Studio replaces template parameters like `$safeitemname$` and `$rootnamespace$` with actual values

## Template Parameters

- `$safeitemname$`: The name of the file/class being created
- `$rootnamespace$`: The namespace of the current project
- `$requestname$`: Custom parameter for specifying the request type name (in handlers)
- `$notificationname$`: Custom parameter for specifying the notification type name (in notification handlers)
- `$responsename$`: Custom parameter for specifying the response type name (in stream and exception handlers)
- `$exceptionname$`: Custom parameter for specifying the exception type name (in exception handlers and actions)

## Complete MediatR Workflow

With these templates, your extension now provides a complete MediatR development experience:

1. **Create** → Use templates to quickly create MediatR classes
2. **Navigate** → Use the existing "Go to MediatR Implementation" command to jump between requests and handlers
3. **Maintain** → Continue using navigation as your codebase grows 