using System;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.CodeLens;
using Microsoft.VisualStudio.Language.CodeLens.Remoting;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Utilities;

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
            bool canCreate = descriptor.Kind == CodeElementKinds.Type
                          || descriptor.Kind == CodeElementKinds.Method;

            if (canCreate)
            {
                Debug.WriteLine($"MediatRNavigationExtension: CodeLensProvider: CanCreate=true for {descriptor.Kind} '{descriptor.ElementDescription}' in '{descriptor.FilePath}'");
            }

            return Task.FromResult(canCreate);
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
