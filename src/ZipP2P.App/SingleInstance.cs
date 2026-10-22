using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace ZipP2P.App;

/// <summary>
/// Garante uma única instância por usuário. A primeira escuta num named pipe (no Linux/macOS é um
/// socket Unix) restrito ao usuário atual; as seguintes (ex.: "Enviar com ZipStream" no menu de
/// contexto) repassam o comando para ela e terminam.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();

    public SingleInstance(string? pipeName = null)
    {
        _pipeName = pipeName ?? DefaultPipeName;
    }

    public static string DefaultPipeName =>
        "ZipStream-" + new string(Environment.UserName.Where(char.IsLetterOrDigit).ToArray());

    /// <summary>Tenta entregar o comando a uma instância já aberta. Retorna false se não houver nenhuma.</summary>
    public async Task<bool> TrySendAsync(StartupCommand command, TimeSpan? timeout = null)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            await client.ConnectAsync((int)(timeout ?? TimeSpan.FromMilliseconds(400)).TotalMilliseconds).ConfigureAwait(false);
            var payload = JsonSerializer.SerializeToUtf8Bytes(command);
            await client.WriteAsync(payload).ConfigureAwait(false);
            await client.FlushAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Passa a atender comandos de outras instâncias (cada um chega em <paramref name="onCommand"/>).</summary>
    public void Listen(Action<StartupCommand> onCommand, Action<Exception>? onError = null)
        => _ = Task.Run(() => ListenLoopAsync(onCommand, onError));

    private async Task ListenLoopAsync(Action<StartupCommand> onCommand, Action<Exception>? onError)
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);

                using var buffer = new MemoryStream();
                await server.CopyToAsync(buffer, _cts.Token).ConfigureAwait(false);
                if (JsonSerializer.Deserialize<StartupCommand>(Encoding.UTF8.GetString(buffer.ToArray())) is { } command)
                    onCommand(command);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // Outra instância já atende (corrida na inicialização) ou mensagem inválida.
                onError?.Invoke(ex);
                if (ex is IOException)
                    await Task.Delay(500, _cts.Token).ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
