using Community.VisualStudio.Toolkit;
using System.ComponentModel;

namespace VSIXExtension.Options
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

        [Category("Razor")]
        [DisplayName("Enable Razor Support")]
        [Description("Enable navigation commands in .razor Blazor component files. " +
                     "Enabled by default. Disable to turn off navigation in .razor files. " +
                     "CodeLens and .razor.cs code-behind files are supported regardless of this setting.")]
        [DefaultValue(true)]
        public bool EnableRazorSupport { get; set; } = true;
    }
}
