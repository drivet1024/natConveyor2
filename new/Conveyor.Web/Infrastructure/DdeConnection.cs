using NDde.Client;

namespace Conveyor.Web.Infrastructure;

internal interface IDdeConnection : IDisposable
{
    bool IsConnected { get; }
    event Action<string, string>? Advise;
    void Connect();
    void StartAdvise(string tag, int timeout);
    void StopAdvise(string tag, int timeout);
    string Request(string tag, int timeout);
    void Poke(string tag, string value, int timeout);
}

internal sealed class DdeConnection : IDdeConnection
{
    private readonly DdeClient _client;
    public event Action<string, string>? Advise;
    public DdeConnection(string service, string topic)
    {
        _client = new(service, topic);
        _client.Advise += OnAdvise;
    }
    private void OnAdvise(object? sender, DdeAdviseEventArgs args) => Advise?.Invoke(args.Item, args.Text);
    public bool IsConnected => _client.IsConnected;
    public void Connect() => _client.Connect();
    public void StartAdvise(string tag, int timeout) => _client.StartAdvise(tag, 1, true, timeout);
    public void StopAdvise(string tag, int timeout) => _client.StopAdvise(tag, timeout);
    public string Request(string tag, int timeout) => _client.Request(tag, timeout);
    public void Poke(string tag, string value, int timeout) => _client.Poke(tag, value, timeout);
    public void Dispose()
    {
        _client.Advise -= OnAdvise;
        _client.Dispose();
    }
}
