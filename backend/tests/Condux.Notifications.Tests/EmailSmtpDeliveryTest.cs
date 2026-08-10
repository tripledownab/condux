using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Condux.Notifications.Tests;

/// <summary>Proves EmailNotifier delivers through the real BCL SmtpClient (SystemSmtpSender) over a real
/// SMTP socket: a minimal in-process SMTP server walks the conversation and captures the DATA. The other
/// email tests use a fake ISmtpSender, so they can't catch an SMTP-handshake/transport regression.
/// Unit-category (no Docker); random loopback port, so it never collides with a running compose stack.</summary>
public class EmailSmtpDeliveryTest
{
    // A throwaway SMTP server: accepts one connection, speaks just enough SMTP for the BCL SmtpClient
    // (plaintext, no auth), and completes Received with the captured DATA as soon as the message lands.
    private sealed class CapturingSmtpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly TaskCompletionSource<string> _received =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Port { get; }
        public Task<string> Received => _received.Task;

        public CapturingSmtpServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(ServeAsync);
        }

        private async Task ServeAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII);
                var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };

                await writer.WriteLineAsync("220 localhost ready");
                var data = new StringBuilder();
                var inData = false;
                string? line;
                while ((line = await reader.ReadLineAsync()) is not null)
                {
                    if (inData)
                    {
                        if (line == ".")
                        {
                            inData = false;
                            await writer.WriteLineAsync("250 OK queued");
                            _received.TrySetResult(data.ToString());
                            continue;
                        }
                        data.AppendLine(line);
                        continue;
                    }

                    var verb = (line.Length >= 4 ? line[..4] : line).ToUpperInvariant();
                    switch (verb)
                    {
                        case "EHLO":
                        case "HELO": await writer.WriteLineAsync("250 localhost"); break;
                        case "DATA": await writer.WriteLineAsync("354 end with <CRLF>.<CRLF>"); inData = true; break;
                        case "QUIT": await writer.WriteLineAsync("221 Bye"); return;
                        default: await writer.WriteLineAsync("250 OK"); break;
                    }
                }
            }
            catch
            {
                // Client hung up; if the message was already captured the test still passes.
            }
        }

        public void Dispose() => _listener.Stop();
    }

    [Fact]
    public async Task Email_delivers_through_the_real_smtp_client()
    {
        using var server = new CapturingSmtpServer();
        var options = new SmtpOptions("127.0.0.1", server.Port, "alerts@condux.test", null, null, UseSsl: false);
        using var sender = new SystemSmtpSender(options);
        var notifier = new EmailNotifier(sender, options);

        await notifier.SendMessageAsync("ops@acme.test", "Paused", "Cap reached.");
        var data = await server.Received.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("Subject: Paused", data);
        Assert.Contains("ops@acme.test", data); // the To header inside the message
        Assert.Contains("Cap reached.", data);  // the plain-text alternative
    }
}
