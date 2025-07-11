using MediatR;
using System.Threading;
using System.Threading.Tasks;

namespace $rootnamespace$
{
    public class $safeitemname$ : INotificationHandler<$notificationname$>
    {
        public async Task Handle($notificationname$ notification, CancellationToken cancellationToken)
        {
            // Implement your notification handler logic here
            
            // Example:
            // await LogNotificationAsync(notification);
            // await SendEmailAsync(notification);
        }
    }
} 