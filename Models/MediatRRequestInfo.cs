using Microsoft.CodeAnalysis;

namespace VSIXExtention.Models
{
    public class MediatRRequestInfo
    {
        public string RequestTypeName { get; set; }
        public string ResponseTypeName { get; set; }
        public INamedTypeSymbol RequestSymbol { get; set; }
        public bool HasResponse { get; set; }
        public bool IsNotification { get; set; }
    }
}
