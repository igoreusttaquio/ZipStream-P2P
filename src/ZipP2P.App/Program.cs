using Avalonia;
using Avalonia.Controls;
using System;

namespace ZipP2P.App;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Já existe uma instância (ex.: na bandeja)? Entrega o comando a ela e sai.
        var command = StartupCommand.Parse(args).WithAbsolutePaths();
        if (new SingleInstance().TrySendAsync(command).GetAwaiter().GetResult())
            return;

        App.StartupCommand = command;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
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
