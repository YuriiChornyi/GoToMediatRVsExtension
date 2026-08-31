using System.Linq;
using System.Threading.Tasks;
using VSIXExtension.Services;
using VSIXExtension.Tests.Infrastructure;
using Xunit;

namespace VSIXExtension.Tests
{
    /// <summary>
    /// Covers MediatRUsageFinder - the search behind "Go To Usage", which walks every invocation
    /// looking for Send/Publish calls that carry the request type.
    /// </summary>
    public class UsageDiscoveryTests
    {
        private const string Contracts = """
            using MediatR;

            namespace App
            {
                public class Ping : IRequest<string> { }
                public class SomethingHappened : INotification { }
                public class Unused : IRequest<string> { }
            }
            """;

        private const string Callers = """
            using System.Threading.Tasks;
            using MediatR;

            namespace App
            {
                public class MediatorCaller
                {
                    private readonly IMediator _mediator;

                    public MediatorCaller(IMediator mediator) => _mediator = mediator;

                    public async Task<string> Get() => await _mediator.Send(new Ping());

                    public Task Notify() => _mediator.Publish(new SomethingHappened());
                }

                public class SenderCaller
                {
                    private readonly ISender _sender;

                    public SenderCaller(ISender sender) => _sender = sender;

                    public async Task<string> Run() => await _sender.Send(new Ping());
                }

                public class PublisherCaller
                {
                    private readonly IPublisher _publisher;

                    public PublisherCaller(IPublisher publisher) => _publisher = publisher;

                    public Task Run() => _publisher.Publish(new SomethingHappened());
                }
            }
            """;

        private static MediatRUsageFinder FinderFor(MediatRTestWorkspace workspace)
            => new MediatRUsageFinder(new WorkspaceService(workspace.Workspace));

        [Fact]
        public async Task FindsSendCall()
        {
            using var workspace = MediatRTestWorkspace.Create(
                new TestSource("Contracts.cs", Contracts),
                new TestSource("Callers.cs", Callers));

            var (ping, _) = await workspace.GetTypeAsync("App.Ping");

            var usages = await FinderFor(workspace).FindUsagesAsync(ping);

            Assert.Equal(2, usages.Count);
            Assert.All(usages, u =>
            {
                Assert.Equal("Ping", u.RequestTypeName);
                Assert.Equal("Send", u.UsageType);
                Assert.False(u.IsNotificationUsage);
            });
        }

        [Fact]
        public async Task FindsPublishCall_AndMarksItAsNotification()
        {
            using var workspace = MediatRTestWorkspace.Create(
                new TestSource("Contracts.cs", Contracts),
                new TestSource("Callers.cs", Callers));

            var (notification, _) = await workspace.GetTypeAsync("App.SomethingHappened");

            var usages = await FinderFor(workspace).FindUsagesAsync(notification);

            Assert.Equal(2, usages.Count);
            Assert.All(usages, u =>
            {
                Assert.Equal("Publish", u.UsageType);
                Assert.True(u.IsNotificationUsage);
            });
        }

        [Fact]
        public async Task FindsCallsThroughISenderAndIPublisher()
        {
            using var workspace = MediatRTestWorkspace.Create(
                new TestSource("Contracts.cs", Contracts),
                new TestSource("Callers.cs", Callers));

            var (ping, _) = await workspace.GetTypeAsync("App.Ping");
            var (notification, _) = await workspace.GetTypeAsync("App.SomethingHappened");

            var sendUsages = await FinderFor(workspace).FindUsagesAsync(ping);
            var publishUsages = await FinderFor(workspace).FindUsagesAsync(notification);

            Assert.Contains(sendUsages, u => u.ClassName == "SenderCaller");
            Assert.Contains(publishUsages, u => u.ClassName == "PublisherCaller");
        }

        [Fact]
        public async Task ResolvesConcreteRequestType_FromAVariableTypedAsTheInterface()
        {
            // The argument's static type is IRequest<string>; only the IOperation walk can recover
            // the concrete Ping it was assigned.
            const string indirectCaller = """
                using System.Threading.Tasks;
                using MediatR;

                namespace App
                {
                    public class IndirectCaller
                    {
                        private readonly IMediator _mediator;

                        public IndirectCaller(IMediator mediator) => _mediator = mediator;

                        public async Task<string> Run()
                        {
                            IRequest<string> command = new Ping();
                            return await _mediator.Send(command);
                        }
                    }
                }
                """;

            using var workspace = MediatRTestWorkspace.Create(
                new TestSource("Contracts.cs", Contracts),
                new TestSource("IndirectCaller.cs", indirectCaller));

            var (ping, _) = await workspace.GetTypeAsync("App.Ping");

            var usages = await FinderFor(workspace).FindUsagesAsync(ping);

            var usage = Assert.Single(usages);
            Assert.Equal("IndirectCaller", usage.ClassName);
            Assert.Equal("Run", usage.MethodName);
        }

        [Fact]
        public async Task IgnoresSendCallsOnServicesThatAreNotMediatR()
        {
            const string lookalike = """
                using System.Threading.Tasks;

                namespace App
                {
                    public class Emailer
                    {
                        public Task Send(Ping message) => Task.CompletedTask;
                    }

                    public class LookalikeCaller
                    {
                        private readonly Emailer _emailer = new Emailer();

                        public Task Run() => _emailer.Send(new Ping());
                    }
                }
                """;

            using var workspace = MediatRTestWorkspace.Create(
                new TestSource("Contracts.cs", Contracts),
                new TestSource("Lookalike.cs", lookalike));

            var (ping, _) = await workspace.GetTypeAsync("App.Ping");

            var usages = await FinderFor(workspace).FindUsagesAsync(ping);

            Assert.Empty(usages);
        }

        [Fact]
        public async Task ReturnsEmpty_WhenTheRequestIsNeverSent()
        {
            using var workspace = MediatRTestWorkspace.Create(
                new TestSource("Contracts.cs", Contracts),
                new TestSource("Callers.cs", Callers));

            var (unused, _) = await workspace.GetTypeAsync("App.Unused");

            var usages = await FinderFor(workspace).FindUsagesAsync(unused);

            Assert.Empty(usages);
        }

        [Fact]
        public async Task ReportsTheCallSiteContext()
        {
            using var workspace = MediatRTestWorkspace.Create(
                new TestSource("Contracts.cs", Contracts),
                new TestSource("Callers.cs", Callers));

            var (ping, _) = await workspace.GetTypeAsync("App.Ping");

            var usages = await FinderFor(workspace).FindUsagesAsync(ping);
            var usage = usages.Single(u => u.ClassName == "MediatorCaller");

            Assert.Equal("Get", usage.MethodName);
            Assert.Equal("Get() method in MediatorCaller", usage.ContextDescription);
            Assert.EndsWith("Callers.cs", usage.FilePath);
            Assert.Contains("_mediator.Send(new Ping())", usage.Location.GetLineText());
            Assert.Equal(usage.Location.GetLineNumber(), usage.LineNumber);
        }

        [Fact]
        public async Task ScansGeneratedRazorFiles()
        {
            const string razorComponent = """
                using System.Threading.Tasks;
                using MediatR;

                namespace App
                {
                    public class Counter
                    {
                        private readonly IMediator _mediator;

                        public Counter(IMediator mediator) => _mediator = mediator;

                        public async Task<string> Load() => await _mediator.Send(new Ping());
                    }
                }
                """;

            using var workspace = MediatRTestWorkspace.Create(
                new TestSource("Contracts.cs", Contracts),
                new TestSource("Counter.razor.g.cs", razorComponent));

            var (ping, _) = await workspace.GetTypeAsync("App.Ping");

            var usages = await FinderFor(workspace).FindUsagesAsync(ping);

            var usage = Assert.Single(usages);
            Assert.EndsWith("Counter.razor.g.cs", usage.FilePath);
        }

        [Fact]
        public async Task IgnoresDocumentsThatAreNeitherCSharpNorRazor()
        {
            using var workspace = MediatRTestWorkspace.Create(
                new TestSource("Contracts.cs", Contracts),
                new TestSource("Callers.txt", Callers));

            var (ping, _) = await workspace.GetTypeAsync("App.Ping");

            var usages = await FinderFor(workspace).FindUsagesAsync(ping);

            Assert.Empty(usages);
        }
    }
}
