using Microsoft.CodeAnalysis;

namespace VSIXExtention.Models
{
    public class MediatRHandlerInfo
    {
        public string HandlerTypeName { get; set; }
        public string RequestTypeName { get; set; }
        public string ResponseTypeName { get; set; }
        public INamedTypeSymbol RequestTypeSymbol { get; set; }
        public INamedTypeSymbol HandlerSymbol { get; set; }
        public Location Location { get; set; }
        public bool IsNotificationHandler { get; set; }

        // Add equality comparison to prevent duplicates
        public override bool Equals(object obj)
        {
            if (obj is MediatRHandlerInfo other)
            {
                return HandlerTypeName == other.HandlerTypeName &&
                       RequestTypeName == other.RequestTypeName &&
                       ResponseTypeName == other.ResponseTypeName &&
                       IsNotificationHandler == other.IsNotificationHandler &&
                       Location?.SourceTree?.FilePath == other.Location?.SourceTree?.FilePath;
            }
            return false;
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 23 + (HandlerTypeName?.GetHashCode() ?? 0);
                hash = hash * 23 + (RequestTypeName?.GetHashCode() ?? 0);
                hash = hash * 23 + (ResponseTypeName?.GetHashCode() ?? 0);
                hash = hash * 23 + IsNotificationHandler.GetHashCode();
                hash = hash * 23 + (Location?.SourceTree?.FilePath?.GetHashCode() ?? 0);
                return hash;
            }
        }
    }
}
