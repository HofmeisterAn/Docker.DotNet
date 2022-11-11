using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Docker.DotNet.Tests;

public sealed class GitHub : IClassFixture<Daemon>, IClassFixture<Client>
{
    private readonly Daemon _daemon;
    
    private readonly Client _client;

    private readonly ITestOutputHelper _testOutputHelper;

    public GitHub(Daemon daemon, Client client, ITestOutputHelper testOutputHelper)
    {
        _daemon = daemon;
        _client = client;
        _testOutputHelper = testOutputHelper;
    }

    [Fact]
    public Task Debug()
    {
        return Task.WhenAll(Enumerable.Range(IdRange.Min, IdRange.Max).Select(id => id.ToString().PadLeft(IdRange.PadLeftLength, '0')).Select(GetInspectContainerAsync));
    }

    private async Task GetInspectContainerAsync(string id)
    {
        var inspectContainerResponse = await _client.Containers.InspectContainerAsync(id)
            .ConfigureAwait(false);

        _testOutputHelper.WriteLine(id);

        Assert.Equal(id, inspectContainerResponse.ID);
    }
}

public sealed class Daemon : IDisposable
{
    public const ushort Port = 2375;

    private readonly CancellationTokenSource _cancellationTokenSource = new();

    private readonly TcpListener _tcpListener = new(new IPEndPoint(IPAddress.Any, Port));

    public Daemon()
    {
        _tcpListener.Start();

        Task.Run(async () =>
        {
            while (!_cancellationTokenSource.IsCancellationRequested)
            {
                var socket = await _tcpListener.AcceptSocketAsync(_cancellationTokenSource.Token)
                    .ConfigureAwait(false);

                _ = ProcessResponse(socket, _cancellationTokenSource.Token);
            }
        });
    }

    public void Dispose()
    {
        _cancellationTokenSource.Cancel();
        _cancellationTokenSource.Dispose();
        _tcpListener.Stop();
    }

    private static async Task ProcessResponse(Socket socket, CancellationToken ct)
    {
        using var messageBuffer = new MemoryStream();

        bool hasRemainingBytes;

        do
        {
            var readBytes = new byte[1024];

            var numberOfBytes = await socket.ReceiveAsync(readBytes, SocketFlags.None, ct)
                .ConfigureAwait(false);

            await messageBuffer.WriteAsync(readBytes.AsMemory(0, numberOfBytes), ct)
                .ConfigureAwait(false);

            hasRemainingBytes = numberOfBytes.Equals(readBytes.Length);
        }
        while (hasRemainingBytes && !ct.IsCancellationRequested);

        // Get the container id: GET /containers/00000000-0000-0000-0000-000000000000/json HTTP/1.1
        var id = new string(Encoding.Default.GetString(messageBuffer.ToArray()).Skip(16).Take(IdRange.PadLeftLength).ToArray());

        var content = $"{{\"ID\":\"{id}\"}}";

        var response = new List<string>();
        response.Add("HTTP/1.1 200 OK");
        response.Add("Content-Length: " + content.Length);
        response.Add("Content-Type: application/json");
        response.Add(string.Empty);
        response.Add(content);
        response.Add(string.Empty);
        response.Add(string.Empty);

        var sendBytes = Encoding.Default.GetBytes(string.Join("\r\n", response));

        _ = await socket.SendAsync(sendBytes, SocketFlags.None, ct)
            .ConfigureAwait(false);

        await socket.DisconnectAsync(false, ct)
            .ConfigureAwait(false);

        socket.Dispose();
    }
}

public sealed class Client : DockerClient
{
    public Client() : base(new DockerClientConfiguration(new Uri("tcp://localhost:" + Daemon.Port), new AnonymousCredentials(), Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan, new Dictionary<string, string>()), null)
    {
    }
}

public static class IdRange
{
    public const ushort Min = ushort.MinValue;

    public const ushort Max = Min + 100;

    public static readonly int PadLeftLength = Max.ToString().Length;
}