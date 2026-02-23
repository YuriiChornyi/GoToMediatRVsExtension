using System.Collections.Generic;

namespace VSIXExtension.Models
{
    public class MediatRCodeLensResult
    {
        public bool IsMediatRType { get; set; }
        public bool IsRequest { get; set; }
        public bool IsHandler { get; set; }
        public string Description { get; set; }
        public int HandlerCount { get; set; }
        public int UsageCount { get; set; }
        public string HandledRequestName { get; set; }
    }

    public class MediatRCodeLensDetailResult
    {
        public List<CodeLensDetailEntry> Entries { get; set; } = new List<CodeLensDetailEntry>();
    }

    public class CodeLensDetailEntry
    {
        public string Category { get; set; }
        public string TypeName { get; set; }
        public string FilePath { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }
        public string Context { get; set; }
    }
}
