using System.Diagnostics;
using DataChannelDotnet.Data;
using ZipP2P.P2P;

// Spike Fase 2 (libdatachannel): validar ITransport + LibDataChannelTransport em loopback,
// medindo vazão de chunks binários.

const int MessageSize = 64 * 1024;
const long TotalBytes = 256L * 1024 * 1024;

await using var offerer = new LibDataChannelTransport(new RtcPeerConfiguration());
await using var answerer = new LibDataChannelTransport(new RtcPeerConfiguration());

// Ponte de sinalização (loopback): SDP e candidatos trocados diretamente entre os transportes.
offerer.LocalDescriptionReady += d => answerer.OnRemoteDescription(d);
answerer.LocalDescriptionReady += d => offerer.OnRemoteDescription(d);
offerer.LocalCandidateReady += c => answerer.OnRemoteCandidate(c);
answerer.LocalCandidateReady += c => offerer.OnRemoteCandidate(c);

var offererOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var answererOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var received = 0L;
var allReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var sw = new Stopwatch();

offerer.Connected += () => { Console.WriteLine("[offerer] connected"); offererOpen.TrySetResult(); };
answerer.Connected += () => { Console.WriteLine("[answerer] connected"); answererOpen.TrySetResult(); };
offerer.Disconnected += () => Console.WriteLine("[offerer] disconnected");
answerer.Disconnected += () => Console.WriteLine("[answerer] disconnected");

answerer.MessageReceived += data =>
{
    if (received == 0)
        sw.Start();
    received += data.Length;
    if (received >= TotalBytes)
    {
        sw.Stop();
        allReceived.TrySetResult();
    }
};

offerer.CreateDataChannel("data");

await Task.WhenAll(offererOpen.Task, answererOpen.Task).WaitAsync(TimeSpan.FromSeconds(15));

Console.WriteLine($"[spike] enviando {TotalBytes / (1024 * 1024)} MB em mensagens de {MessageSize / 1024} KB...");

var buffer = new byte[MessageSize];
new Random(42).NextBytes(buffer);

sw.Reset();
long sent = 0;
while (sent < TotalBytes)
{
    int length = (int)Math.Min(MessageSize, TotalBytes - sent);
    await offerer.SendAsync(buffer.AsMemory(0, length));
    sent += length;
}

await allReceived.Task.WaitAsync(TimeSpan.FromSeconds(60));

double seconds = sw.Elapsed.TotalSeconds;
double mbps = received / (1024.0 * 1024.0) / seconds;
Console.WriteLine($"[spike] OK: {received / (1024 * 1024)} MB em {seconds:F2}s → {mbps:F1} MB/s");

return 0;
