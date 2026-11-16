using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Npgsql;
using NKeeper.Plugins.Packaging;
using NKeeper.Testing;

namespace NKeeper.Plugins.WakeOnLan.Tests;

/// <summary>
/// docs/architecture.md §7.12 (run actions), §7.12.1 (plugin audit events) and §7.15: the wake-on-lan plugin,
/// packaged by its build and loaded into NKeeper's test server like any plugin, waking a host through its run action.
/// </summary>
public sealed class WakeOnLanTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_plugin_contributes_its_service_type_and_run_action()
    {
        var (server, connectionString) = await ServerAsync();
        await using var _ = server;
        using var admin = await server.SignedInClientAsync(Token);
        var plugins = await admin.Http.GetFromJsonAsync<JsonElement>(new Uri("/api/plugins", UriKind.Relative), Token);
        var plugin = plugins.EnumerateArray().Single(p => p.GetProperty("id").GetString() == "wake-on-lan");
        Assert.Equal("loaded", plugin.GetProperty("state").GetString());
        var serviceType = Assert.Single(plugin.GetProperty("serviceTypes").EnumerateArray());
        Assert.Equal(("wake-on-lan", 9, "macAddress"), (serviceType.GetProperty("id").GetString(), serviceType.GetProperty("defaultPort").GetInt32(), serviceType.GetProperty("settings")[0].GetProperty("key").GetString()));
        var action = Assert.Single(plugin.GetProperty("hostActions").EnumerateArray());
        Assert.Equal(("wake-on-lan.wake", "wake", JsonValueKind.Null, "none"), (
            action.GetProperty("id").GetString(),
            action.GetProperty("endpoint").GetString(),
            action.GetProperty("sessionType").ValueKind,
            action.GetProperty("credential").GetString()));
        Assert.Equal(["wake-on-lan"], action.GetProperty("serviceTypes").EnumerateArray().Select(t => t.GetString()));
    }

    [Fact]
    public async Task Waking_a_host_sends_the_magic_packet_and_is_audited()
    {
        var (server, connectionString) = await ServerAsync();
        await using var _ = server;
        using var admin = await server.SignedInClientAsync(Token);
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
        var host = await CreateHostAsync(admin, port, "00:1A:2b:3c:4d:5e");

        using var woken = await admin.SendAsync(HttpMethod.Post, $"/api/p/wake-on-lan/hosts/{Id(host)}/wake", new { serviceId = ServiceId(host) }, Token);
        Assert.True(woken.StatusCode == HttpStatusCode.OK, await woken.Content.ReadAsStringAsync(Token));
        Assert.StartsWith("Sent a Wake-on-LAN packet to 00:1a:2b:3c:4d:5e.", (await woken.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("message").GetString(), StringComparison.Ordinal);

        // The packet sent to the address itself: six 0xFF bytes, then the MAC address 16 times.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var packet = (await listener.ReceiveAsync(timeout.Token)).Buffer;
        Assert.Equal(102, packet.Length);
        Assert.All(packet[..6], b => Assert.Equal(0xFF, b));
        Assert.All(packet[6..].Chunk(6), mac => Assert.Equal(new byte[] { 0x00, 0x1a, 0x2b, 0x3c, 0x4d, 0x5e }, mac));

        var sent = Assert.Single(await AuditEventsAsync(connectionString, "action = 'wake-on-lan.send'"));
        Assert.Equal((Id(host).ToString(), TestServer.AdminUserName), (sent.Target, sent.Actor));
        Assert.Contains($"00:1a:2b:3c:4d:5e to ", sent.Detail, StringComparison.Ordinal);
        Assert.Contains($"127.0.0.1:{port}", sent.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_bad_MAC_address_or_another_service_is_refused()
    {
        var (server, connectionString) = await ServerAsync();
        await using var _ = server;
        using var admin = await server.SignedInClientAsync(Token);
        var host = await CreateHostAsync(admin, 9, "not-a-mac");

        using var badMac = await admin.SendAsync(HttpMethod.Post, $"/api/p/wake-on-lan/hosts/{Id(host)}/wake", new { serviceId = ServiceId(host) }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, badMac.StatusCode);
        Assert.Contains("isn’t a MAC address", await badMac.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);

        using var otherService = await admin.SendAsync(HttpMethod.Post, $"/api/p/wake-on-lan/hosts/{Id(host)}/wake", new { serviceId = Guid.NewGuid() }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, otherService.StatusCode);
    }

    [Fact]
    public async Task A_plugin_records_only_events_named_after_itself()
    {
        using var key = PluginSigningKey.Generate();
        var directory = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "inputs", Guid.NewGuid().ToString("N"))).FullName;
        var trustFile = Path.Combine(directory, "dev.trust.json");
        await File.WriteAllBytesAsync(trustFile, TrustedKey.Development(key.PublicKey.Span).ToKeyFile(), Token);
        var connectionString = await postgres.CreateDatabaseAsync();
        var options = new TestServerOptions { ConnectionString = connectionString, DevelopmentTrustFiles = [trustFile] };
        options.PluginPackages.Add(TestPlugins.Package(key).WriteTo(directory));
        await using var server = await TestServer.StartAsync(options, Token);
        using var admin = await server.SignedInClientAsync(Token);
        using var created = await admin.SendAsync(HttpMethod.Post, "/api/hosts", new { name = "probe", ipAddress = "192.0.2.10" }, Token);
        var hostId = Id(await created.Content.ReadFromJsonAsync<JsonElement>(Token));

        async Task<HttpStatusCode> RecordAsync(string action)
        {
            using var response = await admin.SendAsync(HttpMethod.Post, $"/api/p/hello/hosts/{hostId}/audit", new { action }, Token);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await RecordAsync("hello.probe"));
        Assert.Equal(HttpStatusCode.BadRequest, await RecordAsync("host.update"));
        Assert.Equal(HttpStatusCode.BadRequest, await RecordAsync("wake-on-lan.send"));
        Assert.Equal(HttpStatusCode.BadRequest, await RecordAsync("hello.Probe Now"));
        Assert.Equal(HttpStatusCode.BadRequest, await RecordAsync("hello."));

        var events = await AuditEventsAsync(connectionString, "action LIKE 'hello.%' OR action = 'wake-on-lan.send'");
        Assert.Equal("hello.probe", Assert.Single(events).Action);
    }

    private static async Task<JsonElement> CreateHostAsync(TestServerClient admin, int port, string mac)
    {
        using var response = await admin.SendAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = "sleeper",
            ipAddress = "127.0.0.1",
            services = new[] { new { type = "wake-on-lan", port, settings = new { macAddress = mac } } },
        }, Token);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Token));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Token);
    }

    private static Guid Id(JsonElement host) => host.GetProperty("id").GetGuid();

    private static Guid ServiceId(JsonElement host) => host.GetProperty("services")[0].GetProperty("id").GetGuid();

    /// <summary>Starts the test server with the package this repository's build wrote to artifacts/plugins.</summary>
    private async Task<(TestServer Server, string ConnectionString)> ServerAsync()
    {
        var options = new TestServerOptions { ConnectionString = await postgres.CreateDatabaseAsync() };
        foreach (var package in DevelopmentKeys.BuiltPackages())
        {
            options.PluginPackages.Add(package);
        }

        return (await TestServer.StartAsync(options, Token), options.ConnectionString);
    }

    /// <summary>The audit events of core's <c>audit_events</c> table that match <paramref name="condition"/> (SQL written by the tests).</summary>
    private static async Task<List<(string Actor, string Action, string? Target, string? Detail)>> AuditEventsAsync(string connectionString, string condition)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Token);
        await using var command = new NpgsqlCommand($"SELECT actor, action, target, detail FROM core.audit_events WHERE {condition}", connection);
        await using var reader = await command.ExecuteReaderAsync(Token);
        var events = new List<(string, string, string?, string?)>();
        while (await reader.ReadAsync(Token))
        {
            events.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return events;
    }
}
