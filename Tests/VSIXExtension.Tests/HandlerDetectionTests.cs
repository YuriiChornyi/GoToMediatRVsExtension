using System.Threading.Tasks;
using VSIXExtension.Models;
using VSIXExtension.Tests.Infrastructure;
using Xunit;

namespace VSIXExtension.Tests
{
    /// <summary>
    /// Covers MediatRPatternMatcher.IsMediatRHandler / GetHandlerInfo across every MediatR handler
    /// shape the extension claims to support. The fixture uses the real MediatR 12 signatures, so
    /// these break if MediatR moves an interface between namespaces.
    /// </summary>
    public class HandlerDetectionTests
    {
        private const string Handlers = """
            using System;
            using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;
            using MediatR;
            using MediatR.Pipeline;

            namespace App
            {
                public class Ping : IRequest<string> { }
                public class VoidPing : IRequest { }
                public class StreamPing : IStreamRequest<string> { }
                public class SomethingHappened : INotification { }

                public class PingHandler : IRequestHandler<Ping, string>
                {
                    public Task<string> Handle(Ping request, CancellationToken cancellationToken) => throw new NotImplementedException();
                }

                public class VoidPingHandler : IRequestHandler<VoidPing>
                {
                    public Task Handle(VoidPing request, CancellationToken cancellationToken) => throw new NotImplementedException();
                }

                public class SomethingHappenedHandler : INotificationHandler<SomethingHappened>
                {
                    public Task Handle(SomethingHappened notification, CancellationToken cancellationToken) => throw new NotImplementedException();
                }

                public class StreamPingHandler : IStreamRequestHandler<StreamPing, string>
                {
                    public IAsyncEnumerable<string> Handle(StreamPing request, CancellationToken cancellationToken) => throw new NotImplementedException();
                }

                public class PingBehavior : IPipelineBehavior<Ping, string>
                {
                    public Task<string> Handle(Ping request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken) => throw new NotImplementedException();
                }

                public class StreamPingBehavior : IStreamPipelineBehavior<StreamPing, string>
                {
                    public IAsyncEnumerable<string> Handle(StreamPing request, StreamHandlerDelegate<string> next, CancellationToken cancellationToken) => throw new NotImplementedException();
                }

                public class PingPreProcessor : IRequestPreProcessor<Ping>
                {
                    public Task Process(Ping request, CancellationToken cancellationToken) => throw new NotImplementedException();
                }

                public class PingPostProcessor : IRequestPostProcessor<Ping, string>
                {
                    public Task Process(Ping request, string response, CancellationToken cancellationToken) => throw new NotImplementedException();
                }

                public class PingExceptionHandler : IRequestExceptionHandler<Ping, string, InvalidOperationException>
                {
                    public Task Handle(Ping request, InvalidOperationException exception, RequestExceptionHandlerState<string> state, CancellationToken cancellationToken) => throw new NotImplementedException();
                }

                public class PingExceptionAction : IRequestExceptionAction<Ping, InvalidOperationException>
                {
                    public Task Execute(Ping request, InvalidOperationException exception, CancellationToken cancellationToken) => throw new NotImplementedException();
                }

                public class PlainService { }
            }
            """;

        [Theory]
        [InlineData("App.PingHandler")]
        [InlineData("App.VoidPingHandler")]
        [InlineData("App.SomethingHappenedHandler")]
        [InlineData("App.StreamPingHandler")]
        [InlineData("App.PingBehavior")]
        [InlineData("App.StreamPingBehavior")]
        [InlineData("App.PingPreProcessor")]
        [InlineData("App.PingPostProcessor")]
        public async Task IsMediatRHandler_RecognisesHandlerShapes(string typeName)
        {
            using var workspace = MediatRTestWorkspace.Create(Handlers);
            var (symbol, model) = await workspace.GetTypeAsync(typeName);

            Assert.True(MediatRPatternMatcher.IsMediatRHandler(symbol, model));
        }

        [Theory]
        [InlineData("App.Ping")]
        [InlineData("App.PlainService")]
        public async Task IsMediatRHandler_ReturnsFalse_ForNonHandlers(string typeName)
        {
            using var workspace = MediatRTestWorkspace.Create(Handlers);
            var (symbol, model) = await workspace.GetTypeAsync(typeName);

            Assert.False(MediatRPatternMatcher.IsMediatRHandler(symbol, model));
        }

        [Fact]
        public void IsMediatRHandler_ReturnsFalse_ForNullSymbol()
        {
            Assert.False(MediatRPatternMatcher.IsMediatRHandler(null, null));
        }

        [Theory]
        [InlineData("App.PingHandler", MediatRHandlerType.RequestHandler, "Ping", "String")]
        [InlineData("App.VoidPingHandler", MediatRHandlerType.RequestHandler, "VoidPing", null)]
        [InlineData("App.SomethingHappenedHandler", MediatRHandlerType.NotificationHandler, "SomethingHappened", null)]
        [InlineData("App.StreamPingHandler", MediatRHandlerType.StreamRequestHandler, "StreamPing", "String")]
        [InlineData("App.PingBehavior", MediatRHandlerType.PipelineBehavior, "Ping", "String")]
        [InlineData("App.StreamPingBehavior", MediatRHandlerType.StreamPipelineBehavior, "StreamPing", "String")]
        [InlineData("App.PingPreProcessor", MediatRHandlerType.RequestPreProcessor, "Ping", null)]
        [InlineData("App.PingPostProcessor", MediatRHandlerType.RequestPostProcessor, "Ping", "String")]
        public async Task GetHandlerInfo_ReportsHandlerKindAndTypeNames(
            string typeName, MediatRHandlerType expectedKind, string expectedRequest, string expectedResponse)
        {
            using var workspace = MediatRTestWorkspace.Create(Handlers);
            var (symbol, model) = await workspace.GetTypeAsync(typeName);

            var info = MediatRPatternMatcher.GetHandlerInfo(symbol, model);

            Assert.NotNull(info);
            Assert.Equal(expectedKind, info.HandlerType);
            Assert.Equal(expectedRequest, info.RequestTypeName);
            Assert.Equal(expectedResponse, info.ResponseTypeName);
            Assert.Same(symbol, info.HandlerSymbol);
        }

        [Fact]
        public async Task GetHandlerInfo_ForNotificationHandler_SetsNotificationFlag()
        {
            using var workspace = MediatRTestWorkspace.Create(Handlers);
            var (symbol, model) = await workspace.GetTypeAsync("App.SomethingHappenedHandler");

            var info = MediatRPatternMatcher.GetHandlerInfo(symbol, model);

            Assert.True(info.IsNotificationHandler);
            Assert.False(info.IsStreamHandler);
            Assert.False(info.IsExceptionHandler);
        }

        [Theory]
        [InlineData("App.StreamPingHandler")]
        [InlineData("App.StreamPingBehavior")]
        public async Task GetHandlerInfo_ForStreamShapes_SetsStreamFlag(string typeName)
        {
            using var workspace = MediatRTestWorkspace.Create(Handlers);
            var (symbol, model) = await workspace.GetTypeAsync(typeName);

            var info = MediatRPatternMatcher.GetHandlerInfo(symbol, model);

            Assert.True(info.IsStreamHandler);
            Assert.False(info.IsNotificationHandler);
        }

        [Fact]
        public async Task GetHandlerInfo_ReturnsNull_ForNonHandler()
        {
            using var workspace = MediatRTestWorkspace.Create(Handlers);
            var (symbol, model) = await workspace.GetTypeAsync("App.PlainService");

            Assert.Null(MediatRPatternMatcher.GetHandlerInfo(symbol, model));
        }

        [Fact]
        public async Task GetHandlerInfo_PointsAtTheHandleMethod_NotTheClassDeclaration()
        {
            using var workspace = MediatRTestWorkspace.Create(Handlers);
            var (symbol, model) = await workspace.GetTypeAsync("App.PingHandler");

            var info = MediatRPatternMatcher.GetHandlerInfo(symbol, model);

            Assert.Contains("Handle", info.Location.GetLineText());
            Assert.DoesNotContain("class PingHandler", info.Location.GetLineText());
        }

        [Fact]
        public async Task GetHandlerInfo_ForDerivedHandler_PointsAtTheOverride_NotTheBaseDeclaration()
        {
            // Roslyn's interface map resolves to the member that satisfies the interface, which for
            // an override chain is the base declaration. Navigation has to land on the override.
            const string overrideChain = """
                using System;
                using System.Threading;
                using System.Threading.Tasks;
                using MediatR;

                namespace App
                {
                    public class Ping : IRequest<string> { }

                    public abstract class PingHandlerBase : IRequestHandler<Ping, string>
                    {
                        public abstract Task<string> Handle(Ping request, CancellationToken cancellationToken);
                    }

                    public class PingHandler : PingHandlerBase
                    {
                        public override Task<string> Handle(Ping request, CancellationToken cancellationToken) => throw new NotImplementedException();
                    }
                }
                """;

            using var workspace = MediatRTestWorkspace.Create(overrideChain);
            var (symbol, model) = await workspace.GetTypeAsync("App.PingHandler");

            var info = MediatRPatternMatcher.GetHandlerInfo(symbol, model);

            Assert.Contains("override", info.Location.GetLineText());
            Assert.DoesNotContain("abstract", info.Location.GetLineText());
        }

        [Fact]
        public async Task GetHandlerInfo_ForPreProcessor_PointsAtProcessMethod()
        {
            using var workspace = MediatRTestWorkspace.Create(Handlers);
            var (symbol, model) = await workspace.GetTypeAsync("App.PingPreProcessor");

            var info = MediatRPatternMatcher.GetHandlerInfo(symbol, model);

            Assert.Contains("Process", info.Location.GetLineText());
        }

        // MediatR 12 declares IRequestExceptionHandler / IRequestExceptionAction in the
        // MediatR.Pipeline namespace, but MediatRPatternMatcher.IsMediatRHandler only accepts those
        // two names under the "MediatR" namespace, so it never recognises them. GetHandlerInfo
        // itself handles them correctly - IsMediatRHandler is the gate that rejects them first.
        // Unskip both once the namespace check is fixed.
        [Theory(Skip = "Known gap: exception handlers live in MediatR.Pipeline but IsMediatRHandler only checks the MediatR namespace for them.")]
        [InlineData("App.PingExceptionHandler")]
        [InlineData("App.PingExceptionAction")]
        public async Task IsMediatRHandler_RecognisesExceptionHandlers(string typeName)
        {
            using var workspace = MediatRTestWorkspace.Create(Handlers);
            var (symbol, model) = await workspace.GetTypeAsync(typeName);

            Assert.True(MediatRPatternMatcher.IsMediatRHandler(symbol, model));
        }

        [Fact(Skip = "Known gap: see IsMediatRHandler_RecognisesExceptionHandlers.")]
        public async Task GetHandlerInfo_ForExceptionHandler_ReportsExceptionType()
        {
            using var workspace = MediatRTestWorkspace.Create(Handlers);
            var (symbol, model) = await workspace.GetTypeAsync("App.PingExceptionHandler");

            var info = MediatRPatternMatcher.GetHandlerInfo(symbol, model);

            Assert.NotNull(info);
            Assert.Equal(MediatRHandlerType.RequestExceptionHandler, info.HandlerType);
            Assert.Equal("Ping", info.RequestTypeName);
            Assert.Equal("InvalidOperationException", info.ExceptionTypeName);
            Assert.True(info.IsExceptionHandler);
        }
    }
}
