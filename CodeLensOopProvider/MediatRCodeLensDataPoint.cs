using CodeLensOopProvider.Models;
using Microsoft.VisualStudio.Language.CodeLens;
using Microsoft.VisualStudio.Language.CodeLens.Remoting;
using Microsoft.VisualStudio.Threading;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace CodeLensOopProvider
{
    public class MediatRCodeLensDataPoint : IAsyncCodeLensDataPoint
    {
        private static readonly Guid CommandSetGuid = new Guid("cf38f10f-fa64-4c4b-9ebc-6d7d897607eb");
        private const int CodeLensNavigateCommandId = 0x0105;

        private readonly ICodeLensCallbackService _callbackService;

        public MediatRCodeLensDataPoint(CodeLensDescriptor descriptor, ICodeLensCallbackService callbackService)
        {
            Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
            Debug.WriteLine($"MediatRNavigationExtension: MediatRCodeLensDataPoint: constructor called.");

            _callbackService = callbackService ?? throw new ArgumentNullException(nameof(callbackService));
        }

        public CodeLensDescriptor Descriptor { get; }

        public event AsyncEventHandler InvalidatedAsync;

        public async Task<CodeLensDataPointDescriptor> GetDataAsync(CodeLensDescriptorContext context, CancellationToken token)
        {
            try
            {
                Debug.WriteLine($"MediatRNavigationExtension: CodeLensDataPoint: GetDataAsync for '{Descriptor.ElementDescription}' in '{Descriptor.FilePath}'");

                // Check if CodeLens is enabled via options (RPC to VS process)
                try
                {
                    var enabled = await _callbackService.InvokeAsync<bool>(
                        this,
                        "IsCodeLensEnabled",
                        Array.Empty<object>(),
                        token).ConfigureAwait(false);

                    MediatRCodeLensProvider.IsCodeLensEnabled = enabled;

                    if (!enabled)
                    {
                        Debug.WriteLine("MediatRNavigationExtension: CodeLensDataPoint: CodeLens disabled in options, returning null");
                        return null;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"MediatRNavigationExtension: CodeLensDataPoint: IsCodeLensEnabled RPC failed: {ex.Message}");
                    if (!MediatRCodeLensProvider.IsCodeLensEnabled)
                        return null;
                }

                var result = await _callbackService.InvokeAsync<MediatRCodeLensResult>(
                    this,
                    "GetMediatRCodeLensData",
                    new object[] { Descriptor.FilePath, Descriptor.ElementDescription, Descriptor.Kind.ToString() },
                    token).ConfigureAwait(false);

                if (result == null)
                {
                    Debug.WriteLine($"MediatRNavigationExtension: CodeLensDataPoint: GetMediatRCodeLensData returned null for '{Descriptor.ElementDescription}'");
                    return null;
                }

                if (!result.IsMediatRType)
                {
                    Debug.WriteLine($"MediatRNavigationExtension: CodeLensDataPoint: '{Descriptor.ElementDescription}' is not a MediatR type, suppressing");
                    return null;
                }

                Debug.WriteLine($"MediatRNavigationExtension: CodeLensDataPoint: SUCCESS — '{Descriptor.ElementDescription}' => '{result.Description}'");

                return new CodeLensDataPointDescriptor
                {
                    Description = result.Description,
                    TooltipText = result.IsRequest
                        ? $"MediatR: {result.HandlerCount} handler(s), {result.UsageCount} usage(s)"
                        : $"MediatR: handles {result.HandledRequestName}, {result.UsageCount} usage(s)",
                    IntValue = result.HandlerCount + result.UsageCount,
                    ImageId = null
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MediatRNavigationExtension: CodeLensDataPoint: ERROR in GetDataAsync for '{Descriptor.ElementDescription}': {ex}");
                return null;
            }
        }

        public async Task<CodeLensDetailsDescriptor> GetDetailsAsync(CodeLensDescriptorContext context, CancellationToken token)
        {
            try
            {
                Debug.WriteLine($"MediatRNavigationExtension: CodeLensDataPoint: GetDetailsAsync for '{Descriptor.ElementDescription}'");

                var result = await _callbackService.InvokeAsync<MediatRCodeLensDetailResult>(
                    this,
                    "GetMediatRCodeLensDetails",
                    new object[] { Descriptor.FilePath, Descriptor.ElementDescription, Descriptor.Kind.ToString() },
                    token).ConfigureAwait(false);

                if (result?.Entries == null || result.Entries.Count == 0)
                {
                    Debug.WriteLine($"MediatRNavigationExtension: CodeLensDataPoint: No detail entries for '{Descriptor.ElementDescription}'");
                    return null;
                }

                Debug.WriteLine($"MediatRNavigationExtension: CodeLensDataPoint: Got {result.Entries.Count} detail entries for '{Descriptor.ElementDescription}'");

                var headers = new List<CodeLensDetailHeaderDescriptor>
                {
                    new CodeLensDetailHeaderDescriptor { UniqueName = "Category", DisplayName = "Category", Width = 0.2 },
                    new CodeLensDetailHeaderDescriptor { UniqueName = "Name", DisplayName = "Name", Width = 0.3 },
                    new CodeLensDetailHeaderDescriptor { UniqueName = "File", DisplayName = "File", Width = 0.35 },
                    new CodeLensDetailHeaderDescriptor { UniqueName = "Line", DisplayName = "Line", Width = 0.15 }
                };

                var entries = new List<CodeLensDetailEntryDescriptor>();
                foreach (var entry in result.Entries)
                {
                    var fileName = System.IO.Path.GetFileName(entry.FilePath);
                    var fields = new List<CodeLensDetailEntryField>
                    {
                        new CodeLensDetailEntryField { Text = entry.Category },
                        new CodeLensDetailEntryField { Text = entry.TypeName },
                        new CodeLensDetailEntryField { Text = fileName },
                        new CodeLensDetailEntryField { Text = entry.Line.ToString() }
                    };

                    entries.Add(new CodeLensDetailEntryDescriptor
                    {
                        Fields = fields,
                        Tooltip = entry.Context,
                        NavigationCommand = new CodeLensDetailEntryCommand
                        {
                            CommandSet = CommandSetGuid,
                            CommandId = CodeLensNavigateCommandId
                        },
                        NavigationCommandArgs = new List<object> { $"{entry.FilePath}|{entry.Line}|{entry.Column}" }
                    });
                }

                return new CodeLensDetailsDescriptor
                {
                    Headers = headers,
                    Entries = entries,
                    PaneNavigationCommands = null,
                    CustomData = null
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MediatRNavigationExtension: CodeLensDataPoint: ERROR in GetDetailsAsync for '{Descriptor.ElementDescription}': {ex}");
                return null;
            }
        }
    }
}
