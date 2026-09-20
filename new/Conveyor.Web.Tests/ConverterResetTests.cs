using System.Net;
using Conveyor.Web.Services;

namespace Conveyor.Web.Tests;

public sealed class ConverterResetTests
{
    [Theory]
    [InlineData("Old", "/cgi/login.cgi", "/cgi/reset.cgi?back=Reset&reset=ture")]
    [InlineData("New", "/index.htm", "/msgreboot.htm")]
    public async Task ResetUsesLegacyLoginAndRebootEndpoints(string type, string login, string reboot)
    {
        using var handler = new FakeHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1/") };
        await ConverterResetService.SendResetAsync(client, type, CancellationToken.None);
        Assert.Equal(2, handler.Requests.Count);
        Assert.StartsWith(login, handler.Requests[0].Path);
        Assert.Equal(type == "Old" ? HttpMethod.Get : HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal(reboot, handler.Requests[1].Path);
        Assert.Equal(HttpMethod.Get, handler.Requests[1].Method);
        if (type == "New") Assert.Contains("Login=Login", handler.Requests[0].Body);
    }

    [Theory]
    [InlineData("Old")]
    [InlineData("New")]
    public async Task RejectedLoginDoesNotSendReboot(string type)
    {
        using var handler = new FakeHandler { Reply = "Invalid User" };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1/") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => ConverterResetService.SendResetAsync(client, type, CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task UnknownModelDoesNotSendAnyRequest()
    {
        using var handler = new FakeHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1/") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => ConverterResetService.SendResetAsync(client, "Unknown", CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public string Reply { get; init; } = "ok";
        public List<(HttpMethod Method, string Path, string Body)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add((request.Method, request.RequestUri!.PathAndQuery, request.Content is null ? "" : await request.Content.ReadAsStringAsync(token)));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Reply) };
        }
    }
}
