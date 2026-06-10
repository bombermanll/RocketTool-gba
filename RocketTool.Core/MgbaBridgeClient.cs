using System.Net.Sockets;
using System.Text;

namespace RocketTool.Core;

public sealed class MgbaBridgeClient : IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private bool _disposed;

    private MgbaBridgeClient(string host, int port)
    {
        _host = host;
        _port = port;
    }

    public string Welcome { get; private set; } = string.Empty;

    public static MgbaBridgeClient Connect(string host = "127.0.0.1", int port = 8765)
    {
        var bridge = new MgbaBridgeClient(host, port);
        bridge.OpenWithRetry();
        return bridge;
    }

    public string Command(string line)
    {
        try
        {
            return CommandOnce(line);
        }
        catch (Exception ex) when (IsReconnectable(ex))
        {
            Reopen();
            return CommandOnce(line);
        }
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

    public void WriteRangeVerified(uint address, ReadOnlySpan<byte> data)
    {
        WriteRange(address, data);
        var actual = Read(address, data.Length);
        if (!actual.AsSpan().SequenceEqual(data))
            throw new InvalidOperationException("写入校验失败：mGBA 已响应，但内存回读不一致。请重新连接/读取后再试。");
    }

    public void Write16(uint address, ushort value)
        => Command($"WRITE16 0x{address:X} 0x{value:X}");

    public string Cheat(string name, bool enabled)
        => Command($"CHEAT {name} {(enabled ? 1 : 0)}");

    public string CheatCommand(string command)
        => Command($"CHEAT {command}");

    private void OpenWithRetry()
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                OpenOnce();
                return;
            }
            catch (Exception ex) when (IsReconnectable(ex) || ex is SocketException)
            {
                lastError = ex;
                CloseCurrent();
                Thread.Sleep(120);
            }
        }

        throw new IOException("无法连接 mGBA bridge，请确认脚本仍在运行。", lastError);
    }

    private void Reopen()
    {
        CloseCurrent();
        OpenWithRetry();
    }

    private void OpenOnce()
    {
        ThrowIfDisposed();
        var client = new TcpClient();
        client.Connect(_host, _port);
        client.ReceiveTimeout = 5000;
        client.SendTimeout = 5000;
        _client = client;
        _stream = client.GetStream();
        Welcome = ReadLine();
    }

    private string CommandOnce(string line)
    {
        ThrowIfDisposed();
        var stream = _stream ?? throw new IOException("mGBA bridge is not connected");
        var bytes = Encoding.ASCII.GetBytes(line + "\n");
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
        var response = ReadLine();
        if (!response.StartsWith("OK ", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Bridge command failed: {line}: {response}");
        }
        return response[3..];
    }

    private string ReadLine()
    {
        var stream = _stream ?? throw new IOException("mGBA bridge is not connected");
        var buffer = new List<byte>(256);
        Span<byte> one = stackalloc byte[1];
        while (true)
        {
            var read = stream.Read(one);
            if (read <= 0) throw new IOException("mGBA bridge closed the connection");
            if (one[0] == (byte)'\n') break;
            buffer.Add(one[0]);
        }
        return Encoding.UTF8.GetString(buffer.ToArray()).TrimEnd('\r');
    }

    private static bool IsReconnectable(Exception ex)
    {
        if (ex is SocketException or EndOfStreamException) return true;
        return ex is IOException io &&
               (io.Message.Contains("closed", StringComparison.OrdinalIgnoreCase) ||
                io.Message.Contains("not connected", StringComparison.OrdinalIgnoreCase) ||
                io.InnerException is SocketException);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MgbaBridgeClient));
    }

    private void CloseCurrent()
    {
        try { _stream?.Dispose(); } catch { }
        try { _client?.Dispose(); } catch { }
        _stream = null;
        _client = null;
    }

    public void Dispose()
    {
        _disposed = true;
        CloseCurrent();
    }
}
