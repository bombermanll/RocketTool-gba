using System.Net.Sockets;
using System.Text;

namespace RocketTool.Core;

public sealed class MgbaBridgeClient : IDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;

    private MgbaBridgeClient(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
    }

    public string Welcome { get; private set; } = string.Empty;

    public static MgbaBridgeClient Connect(string host = "127.0.0.1", int port = 8765)
    {
        var client = new TcpClient();
        client.Connect(host, port);
        client.ReceiveTimeout = 5000;
        client.SendTimeout = 5000;
        var bridge = new MgbaBridgeClient(client);
        bridge.Welcome = bridge.ReadLine();
        return bridge;
    }

    public string Command(string line)
    {
        var bytes = Encoding.ASCII.GetBytes(line + "\n");
        _stream.Write(bytes, 0, bytes.Length);
        var response = ReadLine();
        if (!response.StartsWith("OK ", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Bridge command failed: {line}: {response}");
        }
        return response[3..];
    }

    public string GameCode()
    {
        var bytes = Convert.FromHexString(Command("GAMECODE"));
        return Encoding.ASCII.GetString(bytes);
    }

    public byte[] Read(uint address, int length)
        => Convert.FromHexString(Command($"READ 0x{address:X} {length}"));

    public void WriteRange(uint address, ReadOnlySpan<byte> data)
        => Command($"WRITERANGE 0x{address:X} {Convert.ToHexString(data)}");

    public void Write16(uint address, ushort value)
        => Command($"WRITE16 0x{address:X} 0x{value:X}");

    private string ReadLine()
    {
        var buffer = new List<byte>(256);
        Span<byte> one = stackalloc byte[1];
        while (true)
        {
            var read = _stream.Read(one);
            if (read <= 0) throw new IOException("mGBA bridge closed the connection");
            if (one[0] == (byte)'\n') break;
            buffer.Add(one[0]);
        }
        return Encoding.UTF8.GetString(buffer.ToArray()).TrimEnd('\r');
    }

    public void Dispose()
    {
        _stream.Dispose();
        _client.Dispose();
    }
}
