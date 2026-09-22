namespace Conveyor.Web.Services;

public sealed class ConnectedUsers
{
    public const string CookieName = "conveyor-browser";
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _connections = [];

    public int Count
    {
        get { lock (_gate) return _connections.Values.Distinct().Count(); }
    }

    public void Connect(string connectionId, string browserId)
    {
        lock (_gate) _connections[connectionId] = browserId;
    }

    public void Disconnect(string connectionId)
    {
        lock (_gate) _connections.Remove(connectionId);
    }
}
