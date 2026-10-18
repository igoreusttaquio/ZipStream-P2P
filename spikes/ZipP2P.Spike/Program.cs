using System.Diagnostics;
using DataChannelDotnet;
using DataChannelDotnet.Data;
using DataChannelDotnet.Impl;

// Spike Fase 2 (libdatachannel): medir vazão de DataChannel em loopback in-process.

const int MessageSize = 64 * 1024;
const long TotalBytes = 256L * 1024 * 1024;

var offerer = new RtcPeerConnection(new RtcPeerConfiguration());
var answerer = new RtcPeerConnection(new RtcPeerConfiguration());

// Ponte de sinalização (loopback): SDP e candidatos trocados diretamente entre os peers.
offerer.OnLocalDescriptionSafe += (_, desc) => answerer.SetRemoteDescription(desc);
answerer.OnLocalDescriptionSafe += (_, desc) => offerer.SetRemoteDescription(desc);
offerer.OnCandidateSafe += (_, cand) => answerer.AddRemoteCandidate(cand);
answerer.OnCandidateSafe += (_, cand) => offerer.AddRemoteCandidate(cand);

offerer.OnConnectionStateChange += (_, s) => Console.WriteLine($"[offerer] conn={s}");
answerer.OnConnectionStateChange += (_, s) => Console.WriteLine($"[answerer] conn={s}");

var offererOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var answererOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var received = 0L;
var allReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var sw = new Stopwatch();

answerer.OnDataChannel += (_, channel) =>
{
    Console.WriteLine($"[answerer] ondatachannel label={channel.Label}");
    channel.OnOpen += _ => { Console.WriteLine("[answerer] channel open"); answererOpen.TrySetResult(); };
    channel.OnBinaryReceivedSafe += (_, data) =>
    {
        if (received == 0)
            sw.Start();
        received += data.Data.Length;
        if (received >= TotalBytes)
        {
            sw.Stop();
            allReceived.TrySetResult();
        }
    };
};

var channel = offerer.CreateDataChannel(new RtcCreateDataChannelArgs { Label = "data" });
channel.OnOpen += _ => { Console.WriteLine("[offerer] channel open"); offererOpen.TrySetResult(); };

await Task.WhenAll(offererOpen.Task, answererOpen.Task).WaitAsync(TimeSpan.FromSeconds(15));

Console.WriteLine($"[spike] enviando {TotalBytes / (1024 * 1024)} MB em mensagens de {MessageSize / 1024} KB...");

var buffer = new byte[MessageSize];
new Random(42).NextBytes(buffer);

sw.Reset();
long sent = 0;
while (sent < TotalBytes)
{
    int length = (int)Math.Min(MessageSize, TotalBytes - sent);
    channel.Send(buffer.AsSpan(0, length));
    sent += length;
}

await allReceived.Task.WaitAsync(TimeSpan.FromSeconds(60));

double seconds = sw.Elapsed.TotalSeconds;
double mbps = received / (1024.0 * 1024.0) / seconds;
Console.WriteLine($"[spike] OK: {received / (1024 * 1024)} MB em {seconds:F2}s → {mbps:F1} MB/s");

return 0;
