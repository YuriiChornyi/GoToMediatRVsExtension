using Microsoft.VisualStudio.Language.CodeLens;
using Microsoft.VisualStudio.Language.CodeLens.Remoting;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Utilities;
using System;
using System.Collections.Concurrent;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
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

        // OOP-side negative cache: key = "{filePath}|{elementDescription}", value = file last-write ticks when cached.
        // Populated by data points when they confirm a type is NOT MediatR.
        // Auto-invalidated when the file changes (timestamp no longer matches).
        internal static readonly ConcurrentDictionary<string, long> _oopNegativeCache
            = new ConcurrentDictionary<string, long>();

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

            bool syntaxMatch =
                descriptor.Kind == CodeElementKinds.Type ||
                (descriptor.Kind == CodeElementKinds.Method && IsLikelyHandlerMethod(descriptor.ElementDescription));

            if (!syntaxMatch)
                return Task.FromResult(false);

            // Check OOP-side negative cache (populated by data points after semantic confirmation).
            // The cache entry is keyed by file + element and validated against the file's last-write
            // timestamp, so saving a file automatically invalidates its stale cache entries.
            var cacheKey = $"{descriptor.FilePath}|{descriptor.ElementDescription}";
            if (_oopNegativeCache.TryGetValue(cacheKey, out long cachedTicks))
            {
                try
                {
                    long currentTicks = File.GetLastWriteTimeUtc(descriptor.FilePath).Ticks;
                    if (currentTicks == cachedTicks)
                        return Task.FromResult(false); // confirmed non-MediatR, file unchanged
                }
                catch { /* can't read timestamp — fall through and let data point re-check */ }

                // File changed or timestamp unreadable — remove stale entry and re-evaluate
                _oopNegativeCache.TryRemove(cacheKey, out _);
            }

            Debug.WriteLine($"MediatRNavigationExtension: CodeLensProvider: CanCreate=true for {descriptor.Kind} '{descriptor.ElementDescription}' in '{descriptor.FilePath}'");
            return Task.FromResult(true);
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
