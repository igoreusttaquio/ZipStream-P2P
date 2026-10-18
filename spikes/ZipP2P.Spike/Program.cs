using System.Diagnostics;
using SIPSorcery.Net;

const int MessageSize = 16 * 1024;           // 16 KB
const long TotalBytes = 64L * 1024 * 1024;   // 64 MB
const ulong BackpressureThreshold = 32 * 1024 * 1024;

var offerer = new RTCPeerConnection(null);
var answerer = new RTCPeerConnection(null);

offerer.onicecandidate += c => answerer.addIceCandidate(new RTCIceCandidateInit { candidate = c.candidate, sdpMid = c.sdpMid, sdpMLineIndex = c.sdpMLineIndex, usernameFragment = c.usernameFragment });
answerer.onicecandidate += c => offerer.addIceCandidate(new RTCIceCandidateInit { candidate = c.candidate, sdpMid = c.sdpMid, sdpMLineIndex = c.sdpMLineIndex, usernameFragment = c.usernameFragment });

var receivedBytes = 0L;
var allReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var sw = new Stopwatch();

answerer.ondatachannel += dc =>
{
    Console.WriteLine($"[answerer] ondatachannel readyState={dc.readyState}");
    dc.onmessage += (_, protocol, data) =>
    {
        if (receivedBytes == 0) sw.Start();
        receivedBytes += data.Length;
        if (receivedBytes >= TotalBytes) { sw.Stop(); allReceived.TrySetResult(); }
    };
};

var channel = await offerer.createDataChannel("data", new RTCDataChannelInit());
var offer = offerer.createOffer(new RTCOfferOptions());
await offerer.setLocalDescription(offer);
answerer.setRemoteDescription(offer);
var answer = answerer.createAnswer(new RTCAnswerOptions());
await answerer.setLocalDescription(answer);
offerer.setRemoteDescription(answer);

await WaitUntilAsync(() => channel.IsOpened && answerer.DataChannels.Count > 0, TimeSpan.FromSeconds(15));
Console.WriteLine($"[spike] canais abertos. Enviando {TotalBytes / (1024 * 1024)} MB em mensagens de {MessageSize / 1024} KB...");

var buffer = new byte[MessageSize];
new Random(42).NextBytes(buffer);

var progress = Task.Run(async () =>
{
    while (!allReceived.Task.IsCompleted)
    {
        await Task.Delay(1000);
        Console.WriteLine($"  recebido={receivedBytes / (1024 * 1024)} MB, rate={receivedBytes / (1024.0 * 1024.0)} MB/s, bufferedAmount={channel.bufferedAmount}");
    }
});

long sent = 0;
while (sent < TotalBytes)
{
    while (channel.bufferedAmount > BackpressureThreshold)
        await Task.Delay(1);
    int length = (int)Math.Min(MessageSize, TotalBytes - sent);
    channel.send(buffer, 0, length);
    sent += length;
}

var done = await Task.WhenAny(allReceived.Task, Task.Delay(TimeSpan.FromSeconds(30)));
if (done != allReceived.Task)
{
    Console.WriteLine($"[spike] TIMEOUT: recebido {receivedBytes / (1024 * 1024)} MB de {TotalBytes / (1024 * 1024)} MB");
    return 1;
}

double seconds = sw.Elapsed.TotalSeconds;
Console.WriteLine($"[spike] OK: {receivedBytes / (1024 * 1024)} MB em {seconds:F2}s → {receivedBytes / (1024.0 * 1024.0) / seconds:F1} MB/s");
return 0;

static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        if (condition()) return;
        await Task.Delay(20);
    }
    throw new TimeoutException("Conexão não estabelecida a tempo.");
}
