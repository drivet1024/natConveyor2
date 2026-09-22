using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Conveyor.Web.Services;

public sealed class UserPresenceCircuit(ConnectedUsers users) : CircuitHandler, IDisposable
{
    private readonly string _connectionId = Guid.NewGuid().ToString("N");
    private readonly object _gate = new();
    private string? _browserId;
    private bool _connected;

    public void Identify(string browserId)
    {
        if (!Guid.TryParseExact(browserId, "N", out _)) return;
        lock (_gate)
        {
            _browserId = browserId;
            if (_connected) users.Connect(_connectionId, browserId);
        }
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _connected = true;
            if (_browserId is not null) users.Connect(_connectionId, _browserId);
        }
        return Task.CompletedTask;
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        Disconnect();
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        Disconnect();
        return Task.CompletedTask;
    }

    private void Disconnect()
    {
        lock (_gate)
        {
            _connected = false;
            users.Disconnect(_connectionId);
        }
    }

    public void Dispose() => Disconnect();
}
