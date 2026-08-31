using System.Linq;
using System.Threading.Tasks;
using VSIXExtension.Models;
using VSIXExtension.Tests.Infrastructure;
using Xunit;

namespace VSIXExtension.Tests
{
    /// <summary>
    /// Covers MediatRPatternMatcher.FindAllHandlersForTypeSymbol - the solution-wide search behind
    /// "Go To Implementation", including which syntax trees get scanned and which candidates are
    /// filtered out before the user sees them.
    /// </summary>
    public class HandlerDiscoveryTests
    {
        private const string PingRequest = """
            using MediatR;

            namespace App
            {
                public class Ping : IRequest<string> { }
            }
            """;

        private static string HandlerFor(string className, string baseTypeOrInterface = "IRequestHandler<Ping, string>") => $$"""
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using MediatR;

            namespace App
            {
                public class {{className}} : {{baseTypeOrInterface}}
                {
                    public Task<string> Handle(Ping request, CancellationToken cancellationToken) => throw new NotImplementedException();
                }
            }
            """;

        [Fact]
        public async Task FindsHandler_InTheSameProject()
        {
            using var workspace = MediatRTestWorkspace.Create(PingRequest, HandlerFor("PingHandler"));
            var (ping, model) = await workspace.GetTypeAsync("App.Ping");

            var handlers = await MediatRPatternMatcher.FindAllHandlersForTypeSymbol(workspace.Solution, ping, model);

            var handler = Assert.Single(handlers);
            Assert.Equal("PingHandler", handler.HandlerTypeName);
            Assert.Equal("Ping", handler.RequestTypeName);
            Assert.Equal(MediatRHandlerType.RequestHandler, handler.HandlerType);
        }

        [Fact]
        public async Task FindsHandler_InAnotherProject()
        {
            // The request symbol comes from the Contracts compilation while the handler's request
            // symbol comes from the Handlers compilation - AreTypesEqual has to bridge the two.
            using var workspace = MediatRTestWorkspace.Create(
                new TestProject("Contracts", new TestSource("Ping.cs", PingRequest)),
                new TestProject("Handlers", new TestSource("PingHandler.cs", HandlerFor("PingHandler")))
                {
                    ProjectReferences = new[] { "Contracts" }
                });

            var (ping, model) = await workspace.GetTypeAsync("App.Ping");

            var handlers = await MediatRPatternMatcher.FindAllHandlersForTypeSymbol(workspace.Solution, ping, model);

            var handler = Assert.Single(handlers);
            Assert.Equal("PingHandler", handler.HandlerTypeName);
        }

        [Fact]
        public async Task IgnoresProjectsThatDoNotReferenceMediatR()
        {
            using var workspace = MediatRTestWorkspace.Create(
                new TestProject("App", new TestSource("Ping.cs", PingRequest), new TestSource("PingHandler.cs", HandlerFor("PingHandler"))),
                new TestProject("Unrelated", new TestSource("Helper.cs", "namespace Other { public class Helper { } }"))
                {
                    ReferencesMediatR = false
                });

            var (ping, model) = await workspace.GetTypeAsync("App.Ping");

            var handlers = await MediatRPatternMatcher.FindAllHandlersForTypeSymbol(workspace.Solution, ping, model);

            Assert.Single(handlers);
        }

        [Fact]
        public async Task ReturnsEmpty_WhenNothingHandlesTheRequest()
        {
            using var workspace = MediatRTestWorkspace.Create(PingRequest);
            var (ping, model) = await workspace.GetTypeAsync("App.Ping");

            var handlers = await MediatRPatternMatcher.FindAllHandlersForTypeSymbol(workspace.Solution, ping, model);

            Assert.Empty(handlers);
        }

        [Fact]
        public async Task FindsEveryNotificationHandler()
        {
            const string notification = """
                using System;
                using System.Threading;
                using System.Threading.Tasks;
                using MediatR;

                namespace App
                {
                    public class SomethingHappened : INotification { }

                    public class FirstHandler : INotificationHandler<SomethingHappened>
                    {
                        public Task Handle(SomethingHappened notification, CancellationToken cancellationToken) => throw new NotImplementedException();
                    }

                    public class SecondHandler : INotificationHandler<SomethingHappened>
                    {
                        public Task Handle(SomethingHappened notification, CancellationToken cancellationToken) => throw new NotImplementedException();
                    }
                }
                """;

            using var workspace = MediatRTestWorkspace.Create(notification);
            var (symbol, model) = await workspace.GetTypeAsync("App.SomethingHappened");

            var handlers = await MediatRPatternMatcher.FindAllHandlersForTypeSymbol(workspace.Solution, symbol, model);

            Assert.Equal(2, handlers.Count);
            Assert.All(handlers, h => Assert.True(h.IsNotificationHandler));
            Assert.Contains(handlers, h => h.HandlerTypeName == "FirstHandler");
            Assert.Contains(handlers, h => h.HandlerTypeName == "SecondHandler");
        }

        [Fact]
        public async Task ScansGeneratedRazorFiles()
        {
            // Razor components reach Roslyn as generated ".razor.g.cs" trees, which the ".cs"
            // extension filter has to keep. This is the guard for the .razor support.
            using var workspace = MediatRTestWorkspace.Create(
                new TestSource("Ping.cs", PingRequest),
                new TestSource("Counter.razor.g.cs", HandlerFor("CounterHandler")));

            var (ping, model) = await workspace.GetTypeAsync("App.Ping");

            var handlers = await MediatRPatternMatcher.FindAllHandlersForTypeSymbol(workspace.Solution, ping, model);

            Assert.Single(handlers, h => h.HandlerTypeName == "CounterHandler");
        }

        [Fact]
        public async Task SkipsDocumentsThatAreNotCSharpFiles()
        {
            // The type is still in the compilation, but the tree is filtered out by extension.
            using var workspace = MediatRTestWorkspace.Create(
                new TestSource("Ping.cs", PingRequest),
                new TestSource("PingHandler.txt", HandlerFor("PingHandler")));

            var (ping, model) = await workspace.GetTypeAsync("App.Ping");

            var handlers = await MediatRPatternMatcher.FindAllHandlersForTypeSymbol(workspace.Solution, ping, model);

            Assert.Empty(handlers);
        }

        // ---------------------------------------------------------------------------------------
        // Abstract / base-class handling. These lock in the behaviour added for handlers that sit
        // on an abstract base - MediatR can only ever instantiate the concrete type.
        // ---------------------------------------------------------------------------------------

        private const string AbstractBaseWithOverride = """
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

        [Fact]
        public async Task DropsAbstractBase_WhenAConcreteHandlerExists()
        {
            using var workspace = MediatRTestWorkspace.Create(AbstractBaseWithOverride);
            var (ping, model) = await workspace.GetTypeAsync("App.Ping");

            var handlers = await MediatRPatternMatcher.FindAllHandlersForTypeSymbol(workspace.Solution, ping, model);

            var handler = Assert.Single(handlers);
            Assert.Equal("PingHandler", handler.HandlerTypeName);
        }

        [Fact]
        public async Task KeepsAbstractBase_WhenItIsTheOnlyCandidate()
        {
            // The concrete handler may live outside the solution; navigating to the base is still
            // better than reporting nothing.
            const string abstractOnly = """
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
                }
                """;

            using var workspace = MediatRTestWorkspace.Create(abstractOnly);
            var (ping, model) = await workspace.GetTypeAsync("App.Ping");

            var handlers = await MediatRPatternMatcher.FindAllHandlersForTypeSymbol(workspace.Solution, ping, model);

            var handler = Assert.Single(handlers);
            Assert.Equal("PingHandlerBase", handler.HandlerTypeName);
        }

        [Fact]
        public async Task FiltersAbstractCandidatesPerHandlerKind()
        {
            // Dropping the abstract request handler must not drop an unrelated pipeline behaviour,
            // which is filtered within its own group.
            const string mixed = """
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

                    public class PingBehavior : IPipelineBehavior<Ping, string>
                    {
                        public Task<string> Handle(Ping request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken) => throw new NotImplementedException();
                    }
                }
                """;

            using var workspace = MediatRTestWorkspace.Create(mixed);
            var (ping, model) = await workspace.GetTypeAsync("App.Ping");

            var handlers = await MediatRPatternMatcher.FindAllHandlersForTypeSymbol(workspace.Solution, ping, model);

            Assert.Equal(2, handlers.Count);
            Assert.Contains(handlers, h => h.HandlerTypeName == "PingHandler" && h.HandlerType == MediatRHandlerType.RequestHandler);
            Assert.Contains(handlers, h => h.HandlerTypeName == "PingBehavior" && h.HandlerType == MediatRHandlerType.PipelineBehavior);
            Assert.DoesNotContain(handlers, h => h.HandlerTypeName == "PingHandlerBase");
        }

        [Fact]
        public async Task PointsAtTheOverride_NotTheAbstractDeclaration()
        {
            using var workspace = MediatRTestWorkspace.Create(AbstractBaseWithOverride);
            var (ping, model) = await workspace.GetTypeAsync("App.Ping");

            var handlers = await MediatRPatternMatcher.FindAllHandlersForTypeSymbol(workspace.Solution, ping, model);

            var handler = Assert.Single(handlers);
            Assert.Contains("override", handler.Location.GetLineText());
            Assert.DoesNotContain("abstract", handler.Location.GetLineText());
        }

        [Fact]
        public async Task FallsBackToTheClassDeclaration_WhenHandleIsInheritedRatherThanDeclared()
        {
            // The base implements Handle once and delegates to a template method. Sending the user
            // into that shared boilerplate is useless, so navigation lands on the handler itself.
            const string templateMethodBase = """
                using System;
                using System.Threading;
                using System.Threading.Tasks;
                using MediatR;

                namespace App
                {
                    public class Ping : IRequest<string> { }

                    public abstract class PingHandlerBase : IRequestHandler<Ping, string>
                    {
                        public Task<string> Handle(Ping request, CancellationToken cancellationToken) => HandleCore(request);

                        protected abstract Task<string> HandleCore(Ping request);
                    }

                    public class PingHandler : PingHandlerBase
                    {
                        protected override Task<string> HandleCore(Ping request) => throw new NotImplementedException();
                    }
                }
                """;

            using var workspace = MediatRTestWorkspace.Create(templateMethodBase);
            var (ping, model) = await workspace.GetTypeAsync("App.Ping");

            var handlers = await MediatRPatternMatcher.FindAllHandlersForTypeSymbol(workspace.Solution, ping, model);

            var handler = Assert.Single(handlers);
            Assert.Equal("PingHandler", handler.HandlerTypeName);
            Assert.Contains("class PingHandler", handler.Location.GetLineText());
        }

        [Fact]
        public async Task DeduplicatesIdenticalHandlerEntries()
        {
            // A partial class contributes the same handler twice to the type-declaration walk.
            const string partialHandler = """
                using System;
                using System.Threading;
                using System.Threading.Tasks;
                using MediatR;

                namespace App
                {
                    public class Ping : IRequest<string> { }

                    public partial class PingHandler : IRequestHandler<Ping, string>
                    {
                        public Task<string> Handle(Ping request, CancellationToken cancellationToken) => throw new NotImplementedException();
                    }

                    public partial class PingHandler
                    {
                        private int _unused;
                    }
                }
                """;

            using var workspace = MediatRTestWorkspace.Create(partialHandler);
            var (ping, model) = await workspace.GetTypeAsync("App.Ping");

            var handlers = await MediatRPatternMatcher.FindAllHandlersForTypeSymbol(workspace.Solution, ping, model);

            Assert.Single(handlers);
        }
    }
}
