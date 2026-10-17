using System.Net;
using System.Net.Sockets;

namespace Quorum.Modules.Agents.Tools.Mcp;

/// <summary>Resolves host names; replaceable so tests can make a name resolve to any address.</summary>
internal interface IMcpHostResolver
{
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken);
}

internal sealed class DnsMcpHostResolver : IMcpHostResolver
{
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) =>
        // An address literal needs no lookup, and the resolver refuses the unspecified ones (0.0.0.0) that the guard must judge.
        IPAddress.TryParse(host.Trim('[', ']'), out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, cancellationToken);
}

/// <summary>The connection was refused because the address is one an MCP server must never be reached at (D-18).</summary>
internal sealed class McpUrlBlockedException(string host) : IOException($"The address of '{host}' is blocked.");

/// <summary>
/// Keeps MCP connections away from cloud metadata and link-local addresses (D-18). The internal network stays reachable,
/// because customers' servers live there. The check is made on the address the connection is really made to, so a name
/// that resolves to a blocked address later (DNS rebinding) is refused too.
/// </summary>
internal sealed class McpUrlGuard(IMcpHostResolver resolver)
{
    // Embedded IPv4 prefix of NAT64 (RFC 6052): on such a network 64:ff9b::a9fe:a9fe reaches 169.254.169.254.
    private static readonly byte[] Nat64Prefix = [0x00, 0x64, 0xff, 0x9b, 0, 0, 0, 0, 0, 0, 0, 0];

    private static readonly IPAddress AlibabaMetadata = IPAddress.Parse("100.100.100.200");

    private static readonly IPAddress AwsIpv6Metadata = IPAddress.Parse("fd00:ec2::254");

    public static bool IsBlocked(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 0 // 0.0.0.0/8: "this host" on many systems
                || (bytes[0] == 169 && bytes[1] == 254) // link-local, AWS/Azure/GCP metadata
                || address.Equals(AlibabaMetadata);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal || address.Equals(AwsIpv6Metadata))
            {
                return true;
            }

            var bytes = address.GetAddressBytes();
            return bytes.AsSpan(0, Nat64Prefix.Length).SequenceEqual(Nat64Prefix)
                && IsBlocked(new IPAddress(bytes.AsSpan(12, 4)));
        }

        return true;
    }

    /// <summary>
    /// Whether the URL's host is, or resolves to, a blocked address. A name that does not resolve is not blocked: the
    /// connection will fail as an unreachable server.
    /// </summary>
    public async Task<bool> IsBlockedAsync(Uri url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);

        try
        {
            return (await resolver.ResolveAsync(url.DnsSafeHost, cancellationToken)).Any(IsBlocked);
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>The connect callback of the <c>mcp-server</c> HTTP client: resolves, checks every address, then connects to one of them.</summary>
    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var host = context.DnsEndPoint.Host;
        var addresses = await resolver.ResolveAsync(host, cancellationToken);
        if (addresses.Any(IsBlocked))
        {
            throw new McpUrlBlockedException(host);
        }

        SocketException? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException exception)
            {
                socket.Dispose();
                last = exception;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw last ?? new SocketException((int)SocketError.HostNotFound);
    }
}
