using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Condux.Notifications.Tests;

/// <summary>
/// The one <see cref="SystemSmtpSender"/> a process holds is shared by every caller, and the BCL
/// SmtpClient refuses a send while another is in flight. Two sends at once must both arrive, the second
/// after the first, over a real SMTP socket that holds the first message open until told to finish.
/// </summary>
public class SmtpSenderConcurrencyTest
{
    // Speaks just enough SMTP for SmtpClient on each connection it accepts, and holds its answer to the
    // first message's DATA until Release is called, so the first send is provably still in flight.
    private sealed class GatedSmtpServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _messages;

        public GatedSmtpServer()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptAsync);
        }

        public int Port { get; }
        public Task FirstHeld => _firstHeld.Task;
        public int Messages => Volatile.Read(ref _messages);

        public void Release() => _gate.TrySetResult();

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    _ = Task.Run(() => ServeAsync(client));
                }
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII);
                var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };
                await writer.WriteLineAsync("220 localhost ready");
                var inData = false;
                while (await reader.ReadLineAsync() is { } line)
                {
                    if (inData)
                    {
                        if (line == ".")
                        {
                            inData = false;
                            if (Interlocked.Increment(ref _messages) == 1)
                            {
                                _firstHeld.TrySetResult();
                                await _gate.Task;
                            }
                            await writer.WriteLineAsync("250 OK queued");
                        }
                        continue;
                    }
                    var verb = line.Split(' ')[0].ToUpperInvariant();
                    switch (verb)
                    {
                        case "DATA":
                            inData = true;
                            await writer.WriteLineAsync("354 go ahead");
                            break;
                        case "QUIT":
                            await writer.WriteLineAsync("221 bye");
                            return;
                        default:
                            await writer.WriteLineAsync("250 OK");
                            break;
                    }
                }
            }
        }

        public void Dispose()
        {
            Release();
            _listener.Stop();
        }
    }

    private static MailMessage Message(string subject) =>
        new("alerts@condux.test", "ops@acme.test", subject, "body");

    [Fact]
    public async Task A_second_send_waits_for_the_first_instead_of_failing()
    {
        using var server = new GatedSmtpServer();
        using var sender = new SystemSmtpSender(
            new SmtpOptions("127.0.0.1", server.Port, "alerts@condux.test", null, null, UseSsl: false));
        using var first = Message("first");
        using var second = Message("second");

        var firstSend = sender.SendAsync(first);
        await server.FirstHeld.WaitAsync(TimeSpan.FromSeconds(10));
        var secondSend = sender.SendAsync(second);
        server.Release();

        await Task.WhenAll(firstSend, secondSend).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, server.Messages);
    }
}
