using System.Diagnostics.Tracing;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Shouldly;
using SqlDataPack.IntegrationTests.Harness;
using SqlDataPack.Models;
using Xunit;

namespace SqlDataPack.IntegrationTests.Tests;

/// <summary>
/// The only automated check on the "no network beyond the SQL Server you name" guarantee. A real export
/// and import run while an <see cref="EventListener"/> watches the runtime's sockets and name-resolution
/// event sources; afterwards every observed connect and every observed hostname resolution has to be the
/// container's own host and port, and at least one connect has to be, so the listener is demonstrably
/// wired up rather than silently blind.
///
/// This is not proof of isolation. Anything reaching the network below managed System.Net.Sockets
/// (P/Invoke, a native dependency) is invisible here, and so is a remote address the listener cannot
/// decode. Proving the negative outright needs a restricted container network or an egress firewall,
/// which this test does not do. The assembly forces Microsoft.Data.SqlClient onto managed networking
/// (see the RuntimeHostConfigurationOption in the .csproj) -- without it Windows uses native SNI, whose
/// socket connects never surface here and the test proves nothing at all.
/// </summary>
[Collection(nameof(SqlServerCollection))]
public sealed class NetworkIsolationTests {
    private const string Fixture = "core-commerce.sql";

    private readonly SqlServerContainerFixture _fixture;

    public NetworkIsolationTests(SqlServerContainerFixture fixture) {
        _fixture = fixture;
    }

    [Fact]
    public async Task RoundTrip_ContactsOnlyTheSqlServerEndpoint() {
        await using var source = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await source.ExecuteSqlAsync(SqlScriptLoader.LoadEmbeddedScript(Fixture));
        await using var target = await SqlServerFixtureDatabase.CreateAsync(_fixture);
        await TargetSchemaScripts.ApplySourceSchemaUnseededAsync(target, Fixture);
        await using var sqlite = new SqliteTempFileHarness();

        // One small table keeps the monitored window short; the code paths that could phone home
        // (schema read, bulk copy, package write) are the same whatever the table size.
        var options = new ExportOptions {
            TableSelection = ExportTableSelectionMode.Only,
            Tables = { "dbo.Countries" }
        };

        // Source and target live in the same container, so they share one host:port endpoint. Resolved
        // here, before the monitor starts, so this lookup is not itself observed traffic.
        var expected = SqlEndpoint.FromConnectionString(source.ConnectionString);

        var monitor = new OutboundConnectionMonitor(expected);
        SqlDataPackResult importResult;
        try {
            // The fixture setup above already opened (and pooled) connections to these exact connection
            // strings. Without clearing the pool the run reuses those sockets, the monitor observes no
            // ConnectStart at all, and the positive-attribution assertion below fails.
            SqlConnection.ClearAllPools();

            await new SqlDataPackExporter().ExportAsync(source.ConnectionString, sqlite.FilePath, options);
            importResult = await new SqlDataPackImporter().ImportAsync(sqlite.FilePath, target.ConnectionString);
        }
        finally {
            monitor.Dispose();
        }

        importResult.RowCount.ShouldBe(3);

        monitor.ConnectsToExpectedEndpoint.ShouldBeGreaterThan(0, $"The monitor observed no connect to the SQL Server endpoint {expected.Host}:{expected.Port}, even though the export and import demonstrably talked to it. The listener is blind (managed networking switch dropped from the .csproj? sockets event source renamed?) and this test is proving nothing.");
        monitor.UnexpectedConnects.ShouldBeEmpty($"Export and import opened socket connections to endpoints other than the SQL Server at {expected.Host}:{expected.Port}. Every entry below is an outbound connect that should not exist - a telemetry client, an update check, or a dependency that phones home.");
        monitor.UnexpectedResolutions.ShouldBeEmpty($"Export and import resolved hostnames other than the SQL Server host '{expected.Host}'. Every entry below is a name lookup that should not exist - a telemetry client, an update check, or a dependency that phones home.");
    }

    /// <summary>The one endpoint the run is allowed to talk to, taken from the connection string it was given.</summary>
    private sealed class SqlEndpoint {
        private readonly IPAddress[] _addresses;

        private SqlEndpoint(string host, int port, IPAddress[] addresses) {
            Host = host;
            Port = port;
            _addresses = addresses;
        }

        public string Host { get; }

        public int Port { get; }

        /// <summary>
        /// Testcontainers yields a DataSource like "127.0.0.1,49123" (or a remote Docker host when
        /// DOCKER_HOST points off-box). The port is the segment after the comma, 1433 when omitted.
        /// </summary>
        public static SqlEndpoint FromConnectionString(string connectionString) {
            var dataSource = new SqlConnectionStringBuilder(connectionString).DataSource;
            if (dataSource.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)) {
                dataSource = dataSource[4..];
            }

            var separator = dataSource.LastIndexOf(',');
            var host = separator >= 0 ? dataSource[..separator] : dataSource;
            var port = separator >= 0 && int.TryParse(dataSource[(separator + 1)..], out var parsed) ? parsed : 1433;

            var addresses = IPAddress.TryParse(host, out var literal) ? new[] { literal } : Dns.GetHostAddresses(host);
            return new SqlEndpoint(host, port, addresses);
        }

        public bool Matches(IPAddress address, int port) {
            return port == Port && MatchesAddress(address);
        }

        public bool MatchesHostName(string host) {
            return string.Equals(host, Host, StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(host, out var address) && MatchesAddress(address));
        }

        private bool MatchesAddress(IPAddress address) {
            var candidate = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

            // 127.0.0.1 and ::1 are the same endpoint; a connection string naming one does not make a
            // connect over the other a foreign host.
            if (IPAddress.IsLoopback(candidate) && _addresses.All(IPAddress.IsLoopback)) {
                return true;
            }

            return _addresses.Any(known => (known.IsIPv4MappedToIPv6 ? known.MapToIPv4() : known).Equals(candidate));
        }
    }

    private sealed class OutboundConnectionMonitor : EventListener {
        private static readonly Regex BraceBytes = new(@"\{([0-9,\s]+)\}", RegexOptions.Compiled);

        private readonly List<string> _unexpectedResolutions = new();
        private readonly List<string> _unexpectedConnects = new();
        private readonly object _gate = new();
        private readonly SqlEndpoint _expected;
        private int _connectsToExpectedEndpoint;

        public OutboundConnectionMonitor(SqlEndpoint expected) {
            _expected = expected;
        }

        public IReadOnlyList<string> UnexpectedResolutions {
            get {
                lock (_gate) {
                    return _unexpectedResolutions.ToArray();
                }
            }
        }

        public IReadOnlyList<string> UnexpectedConnects {
            get {
                lock (_gate) {
                    return _unexpectedConnects.ToArray();
                }
            }
        }

        /// <summary>
        /// Connects that decoded to the SQL Server's own host and port. Positive attribution: proves the
        /// listener saw the traffic it was supposed to see, and cannot be satisfied by unrelated process
        /// traffic on some other port.
        /// </summary>
        public int ConnectsToExpectedEndpoint => Volatile.Read(ref _connectsToExpectedEndpoint);

        protected override void OnEventSourceCreated(EventSource eventSource) {
            if (eventSource.Name is "System.Net.Sockets" or "System.Net.NameResolution") {
                EnableEvents(eventSource, EventLevel.Informational);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData) {
            // OnEventSourceCreated runs inside the base constructor, before these fields are initialized,
            // so an event arriving on another thread could land here early.
            if (_gate is null || _expected is null) {
                return;
            }

            if (eventData.EventName == "ResolutionStart") {
                var host = FirstStringPayload(eventData);
                if (host is not null && !_expected.MatchesHostName(host)) {
                    lock (_gate) {
                        _unexpectedResolutions.Add($"resolved '{host}' (only '{_expected.Host}' is expected)");
                    }
                }

                return;
            }

            if (eventData.EventName == "ConnectStart") {
                var address = FirstStringPayload(eventData);
                if (address is null) {
                    return;
                }

                foreach (var (ip, port) in ExtractCandidateEndpoints(address)) {
                    if (_expected.Matches(ip, port)) {
                        Interlocked.Increment(ref _connectsToExpectedEndpoint);
                    }
                    else {
                        lock (_gate) {
                            _unexpectedConnects.Add($"connected to {ip}:{port} (only {_expected.Host}:{_expected.Port} is expected; raw event payload '{address}')");
                        }
                    }
                }
            }
        }

        private static string? FirstStringPayload(EventWrittenEventArgs eventData) {
            return eventData.Payload is { Count: > 0 } ? eventData.Payload[0]?.ToString() : null;
        }

        /// <summary>
        /// Decodes the connect endpoint. The sockets event source reports the remote endpoint as
        /// SocketAddress.ToString(), e.g. "InterNetwork:16:{0,80,93,184,...}"; a clean "ip:port" form is
        /// also accepted in case a runtime emits one. Only the two IP families are decoded, so a Unix
        /// domain socket (the Docker daemon on Linux) is not mistaken for an IPv4 address. An IP connect
        /// that cannot be decoded is skipped rather than failed -- the positive-attribution assertion is
        /// what catches a decoder that has stopped working.
        /// </summary>
        private static IEnumerable<(IPAddress Address, int Port)> ExtractCandidateEndpoints(string addressText) {
            if (IPEndPoint.TryParse(addressText, out var endpoint) && endpoint.Port > 0) {
                yield return (endpoint.Address, endpoint.Port);
                yield break;
            }

            var isIPv6 = addressText.StartsWith("InterNetworkV6", StringComparison.OrdinalIgnoreCase);
            if (!isIPv6 && !addressText.StartsWith("InterNetwork", StringComparison.OrdinalIgnoreCase)) {
                yield break;
            }

            var braces = BraceBytes.Match(addressText);
            if (!braces.Success) {
                yield break;
            }

            var bytes = braces.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(value => byte.TryParse(value, out var parsed) ? (byte?)parsed : null).Where(value => value.HasValue).Select(value => value!.Value).ToArray();

            if (bytes.Length < 2) {
                yield break;
            }

            // SocketAddress.ToString() names the address family in the text prefix and lists the SOCKADDR
            // buffer from offset 2 in the braces (the 2-byte family is omitted), so the brace bytes begin
            // at the port: [port(2, network order)][addr...].
            var port = (bytes[0] << 8) | bytes[1];

            if (isIPv6) {
                // SOCKADDR_IN6 from offset 2: [port(2)][flowinfo(4)][addr(16)][scopeid(4)].
                if (bytes.Length >= 22) {
                    yield return (new IPAddress(bytes.AsSpan(6, 16).ToArray()), port);
                }
            }
            else {
                // SOCKADDR_IN from offset 2: [port(2)][addr(4)].
                if (bytes.Length >= 6) {
                    yield return (new IPAddress(bytes.AsSpan(2, 4).ToArray()), port);
                }
            }
        }
    }
}
