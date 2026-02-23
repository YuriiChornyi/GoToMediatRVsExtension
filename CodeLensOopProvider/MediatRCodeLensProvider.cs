using Microsoft.VisualStudio.Language.CodeLens;
using Microsoft.VisualStudio.Language.CodeLens.Remoting;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Utilities;
using System;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace CodeLensOopProvider
{
    [Export(typeof(IAsyncCodeLensDataPointProvider))]
    [Name(Id)]
    [ContentType("CSharp")]
    [LocalizedName(typeof(Resources), Id)]
    [Priority(210)]
    public class MediatRCodeLensProvider : IAsyncCodeLensDataPointProvider
    {
        internal const string Id = "MediatRCodeLens";
        internal static volatile bool IsCodeLensEnabled = true;
        private static long _lastEnabledCheckTicks = 0;
        private static readonly long RecheckIntervalTicks = 30 * TimeSpan.TicksPerSecond;
        private readonly Lazy<ICodeLensCallbackService> _callbackService;

        [ImportingConstructor]
        public MediatRCodeLensProvider(Lazy<ICodeLensCallbackService> callbackService)
        {
            _callbackService = callbackService;
            Debug.WriteLine("MediatRNavigationExtension: CodeLensProvider: Constructor called — provider loaded in OOP process");
        }

        public Task<bool> CanCreateDataPointAsync(CodeLensDescriptor descriptor,
            CodeLensDescriptorContext context, CancellationToken token)
        {
            if (!IsCodeLensEnabled)
            {
                var now = DateTime.UtcNow.Ticks;
                if (now - Interlocked.Read(ref _lastEnabledCheckTicks) < RecheckIntervalTicks)
                    return Task.FromResult(false);
                Interlocked.Exchange(ref _lastEnabledCheckTicks, now);
            }

            bool canCreate = false;

            if (descriptor.Kind == CodeElementKinds.Type)
            {
                canCreate = true;
            }
            else if (descriptor.Kind == CodeElementKinds.Method)
            {
                canCreate = IsLikelyHandlerMethod(descriptor.ElementDescription);
            }

            if (canCreate)
            {
                Debug.WriteLine($"MediatRNavigationExtension: CodeLensProvider: CanCreate=true for {descriptor.Kind} '{descriptor.ElementDescription}' in '{descriptor.FilePath}'");
            }

            return Task.FromResult(canCreate);
        }

        private static readonly string[] HandlerMethodNames = { "Handle", "Execute" };

        private static bool IsLikelyHandlerMethod(string elementDescription)
        {
            if (string.IsNullOrEmpty(elementDescription))
                return false;

            foreach (var name in HandlerMethodNames)
            {
                if (elementDescription == name)
                    return true;

                // "Type.Handle(...)" or "Handle(...)"
                if (elementDescription.StartsWith(name + "(") || elementDescription.Contains("." + name + "("))
                    return true;

                // No-parens formats: description ends with ".Handle" or is "Namespace.Type.Handle"
                if (elementDescription.EndsWith("." + name))
                    return true;
            }

            return false;
        }

        public Task<IAsyncCodeLensDataPoint> CreateDataPointAsync(CodeLensDescriptor descriptor,
            CodeLensDescriptorContext context, CancellationToken token)
        {
            Debug.WriteLine($"MediatRNavigationExtension: CodeLensProvider: CreateDataPoint for '{descriptor.ElementDescription}' in '{descriptor.FilePath}'");

            try
            {
                var dataPoint = new MediatRCodeLensDataPoint(descriptor, _callbackService.Value);
                return Task.FromResult<IAsyncCodeLensDataPoint>(dataPoint);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MediatRNavigationExtension: CodeLensProvider: ERROR creating data point: {ex}");
                return Task.FromResult<IAsyncCodeLensDataPoint>(null);
            }
        }
    }
}
