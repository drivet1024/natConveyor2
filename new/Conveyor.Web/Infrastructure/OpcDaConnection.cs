using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Conveyor.Web.Options;
using TitaniumAS.Opc.Client.Common;
using TitaniumAS.Opc.Client.Da;
using TitaniumAS.Opc.Client.Interop.Common;

namespace Conveyor.Web.Infrastructure;

internal sealed record OpcDaReading(string Tag, object? Value, bool Good, DateTimeOffset Timestamp, string? Error = null);

internal interface IOpcDaConnection : IDisposable
{
    bool IsConnected { get; }
    event Action<IReadOnlyList<OpcDaReading>>? ValuesChanged;
    void Connect();
    Task<IReadOnlyList<OpcDaReading>> ReadAsync(CancellationToken token);
    Task WriteAsync(string tag, int value, CancellationToken token);
}

internal sealed class OpcDaConnection(PlcOptions options, string[] monitoredTags) : IOpcDaConnection
{
    private static readonly MethodInfo SetComObject = typeof(OpcDaServer)
        .GetProperty(nameof(OpcDaServer.ComObject), BindingFlags.Instance | BindingFlags.Public)!
        .GetSetMethod(nonPublic: true)!;
    private static readonly Lazy<bool> Initialized = new(() =>
    {
        // The library's legacy Bootstrap requests anonymous COM security. Use
        // packet integrity for current Windows DCOM authentication instead.
        Marshal.ThrowExceptionForHR(CoInitializeSecurity(IntPtr.Zero, -1, IntPtr.Zero, IntPtr.Zero,
            (uint)RpcAuthnLevel.PktIntegrity, (uint)RpcImpLevel.Identify, IntPtr.Zero, 0, IntPtr.Zero));
        ComProxyBlanket.Default = new ComProxyBlanket
        {
            RpcAuthnLevel = RpcAuthnLevel.PktIntegrity,
            RpcImpLevel = RpcImpLevel.Identify,
            RpcAuthService = RpcAuthService.RPC_C_AUTHN_DEFAULT,
            RpcAuthType = RpcAuthType.RPC_C_AUTHZ_DEFAULT,
            DwCapabilities = RpcDwCapabilities.EOAC_NONE
        };
        return true;
    });

    [DllImport("ole32.dll")]
    private static extern int CoInitializeSecurity(IntPtr securityDescriptor, int authenticationServiceCount,
        IntPtr authenticationServices, IntPtr reserved, uint authenticationLevel, uint impersonationLevel,
        IntPtr authenticationList, uint capabilities, IntPtr reserved3);
    private readonly Dictionary<string, string[]> _tagsByItemId = monitoredTags
        .GroupBy(tag => ItemId(options.OpcTopic, tag), StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
    private readonly Dictionary<string, OpcDaItem> _items = new(StringComparer.Ordinal);
    private OpcDaServer? _server;
    private OpcDaGroup? _group;
    private OpcDaItem[] _readItems = [];
    public event Action<IReadOnlyList<OpcDaReading>>? ValuesChanged;
    public bool IsConnected => _server?.IsConnected == true;

    // Called before hosted services start so COM security is initialized once,
    // before any OPC COM object is created. COM operations run on MTA workers.
    internal static void Initialize() => _ = Initialized.Value;

    internal static string ItemId(string topic, string tag) =>
        tag.StartsWith('[') || string.IsNullOrWhiteSpace(topic) ? tag : $"[{topic.Trim()}]{tag}";

    internal static bool IsLocalHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return true;
        var value = host.Trim();
        return value is "." or "127.0.0.1" or "::1" ||
               value.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
               value.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    }

    public void Connect()
    {
        Initialize();
        var serverId = options.OpcProgId.Trim();
        _server = IsLocalHost(options.OpcHost)
            ? CreateLocalServer(serverId)
            : new OpcDaServer(UrlBuilder.Build(serverId, options.OpcHost.Trim()));
        try
        {
            if (!_server.IsConnected) _server.Connect();
            _group = _server.AddGroup("Conveyor-" + Guid.NewGuid().ToString("N"));
            _group.UpdateRate = TimeSpan.FromMilliseconds(options.OpcUpdateRateMs);
            _group.ValuesChanged += OnValuesChanged;
            _readItems = _tagsByItemId.Keys.Select(itemId => AddItem(itemId, true)).ToArray();
            _group.IsActive = true;
            _group.IsSubscribed = true;
            if (!_group.IsSubscribed) throw new IOException("Le serveur OPC DA n’a pas activé les notifications de lecture.");
        }
        catch { Dispose(); throw; }
    }

    private static OpcDaServer CreateLocalServer(string serverId)
    {
        // TitaniumAS always supplies a COSERVERINFO structure to CoCreateInstanceEx,
        // including for localhost. RSLinx Classic Single Node can reject that path as
        // a remote activation. Resolve and activate the registered COM class locally,
        // then let TitaniumAS manage the OPC groups, items, reads and writes.
        var serverType = Guid.TryParse(serverId, out var clsid)
            ? Type.GetTypeFromCLSID(clsid, throwOnError: true)!
            : Type.GetTypeFromProgID(serverId, throwOnError: true)!;
        clsid = serverType.GUID;
        var server = new OpcDaServer(clsid);
        var comObject = Activator.CreateInstance(serverType)
            ?? throw new COMException($"Le serveur OPC DA local {serverId} n’a retourné aucun objet COM.");
        try
        {
            SetComObject.Invoke(server, [comObject]);
            return server;
        }
        catch
        {
            if (Marshal.IsComObject(comObject)) Marshal.FinalReleaseComObject(comObject);
            server.Dispose();
            throw;
        }
    }

    private OpcDaItem AddItem(string itemId, bool active)
    {
        if (_items.TryGetValue(itemId, out var item)) return item;
        var result = _group!.AddItems([new OpcDaItemDefinition { ItemId = itemId, IsActive = active }]).Single();
        if (result.Error.Failed) throw new IOException($"OPC DA : impossible d’ajouter le tag {itemId} ({result.Error}).");
        _items.Add(itemId, result.Item);
        return result.Item;
    }

    private void OnValuesChanged(object? sender, OpcDaItemValuesChangedEventArgs args)
    {
        if (!ReferenceEquals(sender, _group)) return;
        ValuesChanged?.Invoke(ConvertReadings(args.Values));
    }

    private OpcDaReading[] ConvertReadings(IEnumerable<OpcDaItemValue> values) => values
        .Where(value => _tagsByItemId.ContainsKey(value.Item.ItemId))
        .SelectMany(value => _tagsByItemId[value.Item.ItemId].Select(tag => new OpcDaReading(tag, value.Value,
            !value.Error.Failed && ((short)value.Quality & 0xC0) == 0xC0, value.Timestamp,
            value.Error.Failed ? value.Error.ToString() : value.Quality.ToString())))
        .ToArray();

    public async Task<IReadOnlyList<OpcDaReading>> ReadAsync(CancellationToken token) =>
        ConvertReadings(await _group!.ReadAsync(_readItems, token));

    public async Task WriteAsync(string tag, int value, CancellationToken token)
    {
        var item = AddItem(ItemId(options.OpcTopic, tag), false);
        // Respect the server's scalar type, notably BOOL for start/stop and faults.
        object converted = item.CanonicalDataType is { } type && type != typeof(object)
            ? Convert.ChangeType(value, type, CultureInfo.InvariantCulture) : value;
        var result = (await _group!.WriteAsync([item], [converted], token)).Single();
        if (result.Failed) throw new IOException($"Écriture OPC DA refusée pour {item.ItemId} ({result}).");
    }

    public void Dispose()
    {
        var server = _server;
        _server = null;
        if (_group is not null) _group.ValuesChanged -= OnValuesChanged;
        _group = null;
        _items.Clear();
        _readItems = [];
        server?.Dispose();
    }
}
