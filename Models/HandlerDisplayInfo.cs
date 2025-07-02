namespace VSIXExtention.Models
{
    public class HandlerDisplayInfo
    {
        public MediatRPatternMatcher.MediatRHandlerInfo Handler { get; set; }
        public string DisplayText { get; set; }
    }
}
