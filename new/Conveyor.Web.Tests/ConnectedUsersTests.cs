using Conveyor.Web.Services;

namespace Conveyor.Web.Tests;

public sealed class ConnectedUsersTests
{
    [Fact]
    public void TabsOfTheSameBrowserCountOnceUntilTheLastTabDisconnects()
    {
        var users = new ConnectedUsers();
        users.Connect("tab1", "browser1");
        users.Connect("tab2", "browser1");
        users.Connect("tab3", "browser2");
        Assert.Equal(2, users.Count);
        users.Disconnect("tab1");
        Assert.Equal(2, users.Count);
        users.Disconnect("tab2");
        Assert.Equal(1, users.Count);
        users.Disconnect("tab3");
        users.Disconnect("tab3");
        Assert.Equal(0, users.Count);
    }

    [Fact]
    public async Task DisconnectionReconnectionAndDisposalUpdatePresence()
    {
        var users = new ConnectedUsers();
        using var presence = new UserPresenceCircuit(users);
        await presence.OnConnectionUpAsync(null!, default);
        Assert.Equal(0, users.Count);
        presence.Identify(Guid.NewGuid().ToString("N"));
        Assert.Equal(1, users.Count);
        await presence.OnConnectionDownAsync(null!, default);
        Assert.Equal(0, users.Count);
        await presence.OnConnectionUpAsync(null!, default);
        await presence.OnConnectionUpAsync(null!, default);
        Assert.Equal(1, users.Count);
        presence.Dispose();
        Assert.Equal(0, users.Count);
    }

    [Fact]
    public async Task IdentificationWhileDisconnectedWaitsForReconnection()
    {
        var users = new ConnectedUsers();
        using var presence = new UserPresenceCircuit(users);
        presence.Identify(Guid.NewGuid().ToString("N"));
        Assert.Equal(0, users.Count);
        await presence.OnConnectionUpAsync(null!, default);
        Assert.Equal(1, users.Count);
        await presence.OnCircuitClosedAsync(null!, default);
        Assert.Equal(0, users.Count);
    }
}
