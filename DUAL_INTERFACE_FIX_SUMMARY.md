# MediatR Extension - Dual Interface Support Fix

## Problem
Classes that implement both `IRequest<T>` and `INotification` were not being handled correctly. For example:

```csharp
public class Product : EntityBase, IRequest<Response>, INotification, IValidatable
```

The extension would only find request handlers and ignore notification handlers, even though both types of handlers could exist.

## Root Cause
The original `GetRequestInfo` method in `MediatRPatternMatcher` used a `foreach` loop that returned immediately when it found the first interface (`IRequest`), never checking for `INotification`.

## Solution Implemented

### 1. Updated MediatRPatternMatcher.cs
- **Added `GetAllRequestInfo()`**: Returns all MediatR interface implementations for a class
- **Added `ImplementsBothRequestAndNotification()`**: Checks if a class implements both interfaces  
- **Added `FindAllHandlersForTypeSymbol()`**: Finds all handlers (both request and notification) for a type
- **Kept `GetRequestInfo()` for backward compatibility**: Now calls `GetAllRequestInfo().FirstOrDefault()`

### 2. Updated Interfaces
- **Enhanced `IMediatRHandlerFinder`**: Added `FindAllHandlersAsync()` method to support dual interface detection

### 3. Updated Services
- **Created `MediatRHandlerFinderService`**: Implements the new interface with proper caching support
- **Updated `MediatRCommandHandler`**: Now detects dual interface classes and finds all handlers
- **Enhanced error messages**: Different messages for single vs dual interface classes

### 4. Updated MediatRGoToImplementationProvider.cs
- **Uses new `FindAllHandlersForTypeSymbol()`**: Finds both request and notification handlers
- **Enhanced handler selection dialog**: Shows handler type `[Request]` or `[Notification]` prefixes
- **Improved user feedback**: Better messages when multiple handler types are found

## How It Works Now

### For Single Interface Classes
```csharp
public class GetUserQuery : IRequest<User> // Only IRequest
```
- Finds request handlers only (same as before)
- Shows "Multiple handlers found" if multiple request handlers exist

### For Dual Interface Classes  
```csharp
public class Product : IRequest<Response>, INotification // Both interfaces
```
- Finds **both** request handlers AND notification handlers
- Shows enhanced dialog: "Multiple handlers found for 'Product': • 1 Request Handler(s) • 2 Notification Handler(s)"
- Handler list shows `[Request] ProductHandler` and `[Notification] ProductNotificationHandler`

### Enhanced User Experience
- **Clear handler type identification**: `[Request]` and `[Notification]` prefixes
- **Better error messages**: Specific feedback for dual interface scenarios
- **Comprehensive logging**: Debug output shows exactly what handlers were found

## Example Output

### Debug Log for Dual Interface Class
```
MediatR Provider: Found 3 total handlers for Product:
  - 1 request handler(s): ProductHandler
  - 2 notification handler(s): ProductCreatedHandler, ProductUpdatedHandler
```

### Enhanced Selection Dialog
```
Multiple handlers found for 'Product':
• 1 Request Handler(s)
• 2 Notification Handler(s)

Please select one:
[Request] ProductHandler (Application/Handlers/ProductHandler.cs)
[Notification] ProductCreatedHandler (Application/Notifications/ProductCreatedHandler.cs)  
[Notification] ProductUpdatedHandler (Application/Notifications/ProductUpdatedHandler.cs)
```

## Benefits

1. **Complete Handler Discovery**: No more missing notification handlers for dual interface classes
2. **Clear Visual Distinction**: Handler type prefixes help users understand what they're selecting
3. **Backward Compatibility**: Existing single interface classes work exactly as before
4. **Better Error Messages**: More helpful feedback based on what interfaces are implemented
5. **Comprehensive Logging**: Better debugging information for troubleshooting

## Testing
To test this fix:

1. Create a class implementing both interfaces:
   ```csharp
   public class TestCommand : IRequest<string>, INotification
   ```

2. Create corresponding handlers:
   ```csharp
   public class TestCommandHandler : IRequestHandler<TestCommand, string>
   public class TestCommandNotificationHandler : INotificationHandler<TestCommand>
   ```

3. Use "Go to MediatR Implementation" on the `TestCommand` class
4. Verify both handlers are found and displayed with appropriate prefixes

The extension now correctly handles all MediatR patterns, including classes that serve dual purposes as both commands/queries and domain events. 