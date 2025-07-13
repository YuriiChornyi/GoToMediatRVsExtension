using Microsoft.CodeAnalysis;

namespace VSIXExtention.Models
{
    public class MediatRUsageInfo
    {
        public string RequestTypeName { get; set; }
        public string MethodName { get; set; }
        public string ClassName { get; set; }
        public string FilePath { get; set; }
        public int LineNumber { get; set; }
        public Location Location { get; set; }
        public bool IsNotificationUsage { get; set; }
        public string UsageType { get; set; } // "Send", "Publish", "SendAsync", "PublishAsync"
        public string ContextDescription { get; set; } // e.g., "GetUser() method in UserController"

        // Add equality comparison to prevent duplicates
        public override bool Equals(object obj)
        {
            if (obj is MediatRUsageInfo other)
            {
                return RequestTypeName == other.RequestTypeName &&
                       MethodName == other.MethodName &&
                       ClassName == other.ClassName &&
                       FilePath == other.FilePath &&
                       LineNumber == other.LineNumber &&
                       UsageType == other.UsageType;
            }
            return false;
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 23 + (RequestTypeName?.GetHashCode() ?? 0);
                hash = hash * 23 + (MethodName?.GetHashCode() ?? 0);
                hash = hash * 23 + (ClassName?.GetHashCode() ?? 0);
                hash = hash * 23 + (FilePath?.GetHashCode() ?? 0);
                hash = hash * 23 + LineNumber.GetHashCode();
                hash = hash * 23 + (UsageType?.GetHashCode() ?? 0);
                return hash;
            }
        }
    }
} 