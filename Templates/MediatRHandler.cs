using MediatR;
using System.Threading;
using System.Threading.Tasks;

namespace $rootnamespace$
{
    public class $safeitemname$ : IRequestHandler<$requestname$>
    {
        public async Task Handle($requestname$ request, CancellationToken cancellationToken)
        {
            // Implement your handler logic here
            
            // Example:
            // await SomeServiceMethod(request);
        }
    }
} 