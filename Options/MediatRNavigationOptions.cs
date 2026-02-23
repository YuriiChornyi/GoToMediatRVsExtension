using Community.VisualStudio.Toolkit;
using System.ComponentModel;

namespace VSIXExtention.Options
{
    public class MediatRNavigationOptions : BaseOptionModel<MediatRNavigationOptions>
    {
        [Category("CodeLens")]
        [DisplayName("Enable CodeLens integration")]
        [Description("Show handler and usage counts inline above MediatR request and handler types.")]
        [DefaultValue(true)]
        public bool EnableCodeLens { get; set; } = true;

        [Category("CodeLens")]
        [DisplayName("Refresh delay (seconds)")]
        [Description("How long to wait after a code change before refreshing CodeLens counts. Higher values reduce CPU usage.")]
        [DefaultValue(3)]
        public int CodeLensRefreshDelaySeconds { get; set; } = 3;

        [Category("Commands")]
        [DisplayName("Enable Go to Implementation command")]
        [Description("Show the 'Go to MediatR Implementation' command in menus and context menus.")]
        [DefaultValue(true)]
        public bool EnableGoToImplementation { get; set; } = true;

        [Category("Commands")]
        [DisplayName("Enable Go to Usage command")]
        [Description("Show the 'Go to MediatR Send/Publish' command in menus and context menus.")]
        [DefaultValue(true)]
        public bool EnableGoToUsage { get; set; } = true;
    }
}
