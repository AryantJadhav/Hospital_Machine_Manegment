using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace HospitalPm.Api.Hosting;

/// <summary>
/// A minimal mDNS responder that answers queries for a single hostname.
///
/// Built in-house rather than pulling a library because the feature is small
/// (respond to A-record queries for one name), the protocol surface needed is
/// tiny, and the packaging constraint says no new dependency without
/// justification. ~150 lines of multicast UDP is less risk than a transitive
/// dependency tree on a hospital PC.
///
/// Implements just enough of RFC 6762 to let phones and browsers on the ward
/// network resolve "hospitalpm.local" to this machine's LAN IP, so that a
/// DHCP change never becomes a support call.
/// </summary>
public sealed class MdnsResponder : IDisposable
{
    // RFC 6762: mDNS uses multicast group 224.0.0.251, port 5353.
    private static readonly IPAddress MulticastGroup = IPAddress.Parse("224.0.0.251");
    private const int MdnsPort = 5353;

    // DNS constants.
    private const ushort TypeA = 1;       // A record (IPv4 address)
    private const ushort ClassIn = 1;     // Internet class
    private const ushort ClassInFlush = 0x8001; // Class IN with cache-flush bit
    private const uint DefaultTtl = 120;  // 2 minutes — standard for mDNS

    private readonly string _hostname;
    private readonly byte[] _encodedName;
    private readonly ILogger _logger;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    public MdnsResponder(string hostname, ILogger logger)
    {
        // Strip ".local" if someone passed the full name.
        _hostname = hostname.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            ? hostname[..^6]
            : hostname;
        _encodedName = EncodeDnsName($"{_hostname}.local");
        _logger = logger;
    }

    public void Start()
    {
        try
        {
            _udp = new UdpClient();
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
            _udp.JoinMulticastGroup(MulticastGroup);

            _cts = new CancellationTokenSource();
            _listenTask = ListenAsync(_cts.Token);

            // Announce ourselves on startup so devices learn the name quickly
            // without waiting for a query.
            Announce();

            _logger.LogInformation("mDNS responder started for {Hostname}.local", _hostname);
        }
        catch (SocketException ex)
        {
            // A port conflict or missing multicast support should not prevent
            // the application from starting. The system works fine without
            // mDNS — the hospital just has to use the IP address.
            _logger.LogWarning(ex,
                "mDNS responder could not start — devices will need the IP address " +
                "rather than {Hostname}.local", _hostname);
        }
    }

    /// <summary>
    /// Sends an unsolicited announcement so devices learn the address without
    /// waiting for a query.
    /// </summary>
    public void Announce()
    {
        try
        {
            foreach (var ip in GetLocalIPv4Addresses())
            {
                var response = BuildResponse(ip);
                _udp?.Send(response, response.Length, new IPEndPoint(MulticastGroup, MdnsPort));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "mDNS announce failed");
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();

        // Send a goodbye packet (TTL=0) so cached entries are cleared promptly.
        try
        {
            foreach (var ip in GetLocalIPv4Addresses())
            {
                var goodbye = BuildResponse(ip, ttl: 0);
                _udp?.Send(goodbye, goodbye.Length, new IPEndPoint(MulticastGroup, MdnsPort));
            }
        }
        catch { /* Best effort on shutdown. */ }

        _udp?.Dispose();
        _cts?.Dispose();
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _udp!.ReceiveAsync(ct);
                HandlePacket(result.Buffer, result.RemoteEndPoint);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex)
            {
                _logger.LogDebug(ex, "mDNS receive error");
            }
        }
    }

    private void HandlePacket(byte[] data, IPEndPoint sender)
    {
        // Minimum DNS header is 12 bytes.
        if (data.Length < 12) return;

        // Check this is a standard query (QR=0, opcode=0).
        var flags = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2));
        if ((flags & 0x8000) != 0) return; // Response, not a query.

        var questionCount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4));
        if (questionCount == 0) return;

        // Walk through questions looking for our name.
        var offset = 12;
        for (var i = 0; i < questionCount && offset < data.Length; i++)
        {
            var nameStart = offset;
            if (!TrySkipDnsName(data, ref offset)) return;
            if (offset + 4 > data.Length) return;

            var qtype = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));
            var qclass = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 2));
            offset += 4;

            // Only answer A-record queries for our name.
            if (qtype != TypeA && qtype != 255) continue; // 255 = ANY
            if ((qclass & 0x7FFF) != ClassIn) continue;

            if (!NameMatches(data, nameStart)) continue;

            // It's for us — respond with every local IPv4 address.
            foreach (var ip in GetLocalIPv4Addresses())
            {
                var response = BuildResponse(ip);
                try
                {
                    _udp?.Send(response, response.Length, new IPEndPoint(MulticastGroup, MdnsPort));
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "mDNS send failed");
                }
            }
        }
    }

    /// <summary>
    /// Checks if the DNS name starting at <paramref name="offset"/> matches our hostname.
    /// </summary>
    private bool NameMatches(byte[] data, int offset)
    {
        // Simple label-by-label comparison against our pre-encoded name.
        for (var i = 0; i < _encodedName.Length; i++)
        {
            if (offset + i >= data.Length) return false;

            // Case-insensitive comparison for ASCII letters.
            var a = _encodedName[i];
            var b = data[offset + i];
            if (a >= 0x41 && a <= 0x5A) a |= 0x20; // tolower
            if (b >= 0x41 && b <= 0x5A) b |= 0x20;
            if (a != b) return false;
        }
        return true;
    }

    /// <summary>
    /// Builds a complete mDNS response packet for an A record.
    /// </summary>
    private byte[] BuildResponse(IPAddress address, uint ttl = DefaultTtl)
    {
        // Header (12) + Name + Type(2) + Class(2) + TTL(4) + RDLength(2) + RData(4)
        var packet = new byte[12 + _encodedName.Length + 2 + 2 + 4 + 2 + 4];
        var span = packet.AsSpan();

        // Header: QR=1 (response), AA=1 (authoritative), no questions, 1 answer.
        BinaryPrimitives.WriteUInt16BigEndian(span[0..], 0);           // Transaction ID (0 for mDNS)
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], 0x8400);      // Flags: QR=1, AA=1
        BinaryPrimitives.WriteUInt16BigEndian(span[4..], 0);           // Questions
        BinaryPrimitives.WriteUInt16BigEndian(span[6..], 1);           // Answers
        BinaryPrimitives.WriteUInt16BigEndian(span[8..], 0);           // Authority
        BinaryPrimitives.WriteUInt16BigEndian(span[10..], 0);          // Additional

        // Name
        var pos = 12;
        _encodedName.CopyTo(span[pos..]);
        pos += _encodedName.Length;

        // Type, Class, TTL, RDLength, RData
        BinaryPrimitives.WriteUInt16BigEndian(span[pos..], TypeA);         pos += 2;
        BinaryPrimitives.WriteUInt16BigEndian(span[pos..], ClassInFlush);  pos += 2;
        BinaryPrimitives.WriteUInt32BigEndian(span[pos..], ttl);           pos += 4;
        BinaryPrimitives.WriteUInt16BigEndian(span[pos..], 4);             pos += 2; // IPv4 = 4 bytes
        address.GetAddressBytes().CopyTo(span[pos..]);

        return packet;
    }

    /// <summary>
    /// Encodes "hospitalpm.local" as DNS wire format: length-prefixed labels
    /// terminated by a zero byte.
    /// </summary>
    private static byte[] EncodeDnsName(string name)
    {
        var labels = name.Split('.');
        var totalLength = labels.Sum(l => l.Length + 1) + 1; // +1 for each length byte, +1 for terminator
        var result = new byte[totalLength];
        var pos = 0;

        foreach (var label in labels)
        {
            result[pos++] = (byte)label.Length;
            foreach (var c in label)
            {
                result[pos++] = (byte)c;
            }
        }
        result[pos] = 0; // Root label

        return result;
    }

    /// <summary>
    /// Advances past a DNS name in a packet, handling both labels and
    /// compression pointers.
    /// </summary>
    private static bool TrySkipDnsName(byte[] data, ref int offset)
    {
        var limit = 256; // Prevent infinite loops on malformed packets.
        while (offset < data.Length && limit-- > 0)
        {
            var len = data[offset];
            if (len == 0)
            {
                offset++;
                return true;
            }

            if ((len & 0xC0) == 0xC0)
            {
                // Compression pointer — 2 bytes total.
                offset += 2;
                return true;
            }

            offset += 1 + len;
        }
        return false;
    }

    /// <summary>
    /// This machine's non-loopback IPv4 addresses — the same approach used by
    /// <see cref="Operations.DiagnosticsEndpoints"/>.
    /// </summary>
    private static List<IPAddress> GetLocalIPv4Addresses()
    {
        var addresses = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                foreach (var info in nic.GetIPProperties().UnicastAddresses)
                {
                    if (info.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(info.Address))
                    {
                        addresses.Add(info.Address);
                    }
                }
            }
        }
        catch (NetworkInformationException)
        {
            // No network interfaces available.
        }
        return addresses;
    }
}
