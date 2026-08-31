using System.Linq;
using System.Threading.Tasks;
using VSIXExtension.Tests.Infrastructure;
using Xunit;

namespace VSIXExtension.Tests
{
    /// <summary>
    /// Covers MediatRPatternMatcher.IsMediatRRequest / GetRequestInfo / GetAllRequestInfo /
    /// ImplementsBothRequestAndNotification - the checks that decide whether the caret is sitting
    /// on something the "Go To Implementation" command should light up for.
    /// </summary>
    public class RequestDetectionTests
    {
        private const string Requests = """
            using MediatR;

            namespace App
            {
                public class PingWithResponse : IRequest<string> { }

                public class PingWithoutResponse : IRequest { }

                public class SomethingHappened : INotification { }

                public record RecordPing(int Id) : IRequest<string>;

                public class BothRequestAndNotification : IRequest<string>, INotification { }

                public abstract class RequestBase : IRequest<string> { }

                public class InheritsRequest : RequestBase { }

                public class PlainClass { }
            }
            """;

        [Theory]
        [InlineData("App.PingWithResponse")]
        [InlineData("App.PingWithoutResponse")]
        [InlineData("App.SomethingHappened")]
        [InlineData("App.RecordPing")]
        [InlineData("App.BothRequestAndNotification")]
        [InlineData("App.InheritsRequest")]
        public async Task IsMediatRRequest_RecognisesMediatRTypes(string typeName)
        {
            using var workspace = MediatRTestWorkspace.Create(Requests);
            var (symbol, model) = await workspace.GetTypeAsync(typeName);

            Assert.True(MediatRPatternMatcher.IsMediatRRequest(symbol, model));
        }

        [Fact]
        public async Task IsMediatRRequest_ReturnsFalse_ForPlainClass()
        {
            using var workspace = MediatRTestWorkspace.Create(Requests);
            var (symbol, model) = await workspace.GetTypeAsync("App.PlainClass");

            Assert.False(MediatRPatternMatcher.IsMediatRRequest(symbol, model));
        }

        [Fact]
        public void IsMediatRRequest_ReturnsFalse_ForNullSymbol()
        {
            Assert.False(MediatRPatternMatcher.IsMediatRRequest(null, null));
        }

        [Fact]
        public async Task IsMediatRRequest_ReturnsFalse_ForHandler()
        {
            const string source = """
                using System.Threading;
                using System.Threading.Tasks;
                using MediatR;

                namespace App
                {
                    public class Ping : IRequest<string> { }

                    public class PingHandler : IRequestHandler<Ping, string>
                    {
                        public Task<string> Handle(Ping request, CancellationToken cancellationToken) => Task.FromResult("");
                    }
                }
                """;

            using var workspace = MediatRTestWorkspace.Create(source);
            var (symbol, model) = await workspace.GetTypeAsync("App.PingHandler");

            Assert.False(MediatRPatternMatcher.IsMediatRRequest(symbol, model));
        }

        [Fact]
        public async Task GetRequestInfo_ReportsResponseType()
        {
            using var workspace = MediatRTestWorkspace.Create(Requests);
            var (symbol, model) = await workspace.GetTypeAsync("App.PingWithResponse");

            var info = MediatRPatternMatcher.GetRequestInfo(symbol, model);

            Assert.NotNull(info);
            Assert.Equal("PingWithResponse", info.RequestTypeName);
            Assert.Equal("String", info.ResponseTypeName);
            Assert.True(info.HasResponse);
            Assert.False(info.IsNotification);
        }

        [Fact]
        public async Task GetRequestInfo_ForRequestWithoutResponse_HasNoResponse()
        {
            using var workspace = MediatRTestWorkspace.Create(Requests);
            var (symbol, model) = await workspace.GetTypeAsync("App.PingWithoutResponse");

            var info = MediatRPatternMatcher.GetRequestInfo(symbol, model);

            Assert.NotNull(info);
            Assert.False(info.HasResponse);
            Assert.Null(info.ResponseTypeName);
            Assert.False(info.IsNotification);
        }

        [Fact]
        public async Task GetRequestInfo_ForNotification_IsMarkedAsNotification()
        {
            using var workspace = MediatRTestWorkspace.Create(Requests);
            var (symbol, model) = await workspace.GetTypeAsync("App.SomethingHappened");

            var info = MediatRPatternMatcher.GetRequestInfo(symbol, model);

            Assert.NotNull(info);
            Assert.True(info.IsNotification);
            Assert.False(info.HasResponse);
        }

        [Fact]
        public async Task GetAllRequestInfo_ReturnsBothEntries_WhenTypeIsRequestAndNotification()
        {
            using var workspace = MediatRTestWorkspace.Create(Requests);
            var (symbol, model) = await workspace.GetTypeAsync("App.BothRequestAndNotification");

            var infos = MediatRPatternMatcher.GetAllRequestInfo(symbol, model);

            Assert.Equal(2, infos.Count);
            Assert.Single(infos, i => !i.IsNotification && i.HasResponse && i.ResponseTypeName == "String");
            Assert.Single(infos, i => i.IsNotification);
        }

        [Fact]
        public async Task GetAllRequestInfo_ReturnsEmpty_ForPlainClass()
        {
            using var workspace = MediatRTestWorkspace.Create(Requests);
            var (symbol, model) = await workspace.GetTypeAsync("App.PlainClass");

            Assert.Empty(MediatRPatternMatcher.GetAllRequestInfo(symbol, model));
        }

        [Fact]
        public async Task ImplementsBothRequestAndNotification_IsTrue_OnlyWhenBothAreImplemented()
        {
            using var workspace = MediatRTestWorkspace.Create(Requests);

            var (both, bothModel) = await workspace.GetTypeAsync("App.BothRequestAndNotification");
            var (requestOnly, requestModel) = await workspace.GetTypeAsync("App.PingWithResponse");
            var (notificationOnly, notificationModel) = await workspace.GetTypeAsync("App.SomethingHappened");

            Assert.True(MediatRPatternMatcher.ImplementsBothRequestAndNotification(both, bothModel));
            Assert.False(MediatRPatternMatcher.ImplementsBothRequestAndNotification(requestOnly, requestModel));
            Assert.False(MediatRPatternMatcher.ImplementsBothRequestAndNotification(notificationOnly, notificationModel));
        }

        [Fact]
        public async Task GetRequestInfo_ForRecordWithPrimaryConstructor_IsDetected()
        {
            using var workspace = MediatRTestWorkspace.Create(Requests);
            var (symbol, model) = await workspace.GetTypeAsync("App.RecordPing");

            var info = MediatRPatternMatcher.GetRequestInfo(symbol, model);

            Assert.NotNull(info);
            Assert.Equal("RecordPing", info.RequestTypeName);
            Assert.Equal("String", info.ResponseTypeName);
        }
    }
}
