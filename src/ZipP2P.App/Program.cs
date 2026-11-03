using Avalonia;
using Avalonia.Controls;
using System;

namespace ZipP2P.App;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    /// <summary>
    /// Existe enquanto o app está aberto. O instalador verifica este nome para saber se precisa
    /// pedir <c>--close-for-update</c> antes de substituir os arquivos (ver packaging/windows).
    /// </summary>
    public const string AppMutexName = "ZipStreamAppMutex";

    private static Mutex? _appMutex;

    [STAThread]
    public static void Main(string[] args)
    {
        // Já existe uma instância (ex.: na bandeja)? Entrega o comando a ela e sai.
        var command = StartupCommand.Parse(args).WithAbsolutePaths();
        if (new SingleInstance().TrySendAsync(command).GetAwaiter().GetResult())
        {
            if (command.CloseForUpdate)
                WaitForOtherInstanceToExit();
            return;
        }

        // Pedido do instalador sem instância aberta: não há nada para fechar.
        if (command.CloseForUpdate)
            return;

        // Um perfil personalizado (ZIPSTREAM_DATA_DIR) não é a instalação: não segura o mutex do instalador.
        if (OperatingSystem.IsWindows())
            _appMutex = new Mutex(initiallyOwned: false, AppMutexName + AppPaths.InstanceSuffix);

        App.StartupCommand = command;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        GC.KeepAlive(_appMutex);
    }

    /// <summary>
    /// Espera a instância aberta liberar o mutex (até 15 s), para quem chamou <c>--close-for-update</c>
    /// (o instalador) só continuar com os arquivos já livres.
    /// </summary>
    private static void WaitForOtherInstanceToExit()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && Mutex.TryOpenExisting(AppMutexName + AppPaths.InstanceSuffix, out var other))
        {
            other.Dispose();
            Thread.Sleep(200);
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
