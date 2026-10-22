using System.Diagnostics;

namespace ZipP2P.App.ShellIntegration;

/// <summary>
/// macOS (melhor esforço / experimental): cria uma Ação Rápida (workflow do Automator) em
/// <c>~/Library/Services</c>, que aparece no menu de contexto do Finder em "Ações Rápidas"
/// (ou "Serviços"). Funciona melhor com o app instalado como .app em /Applications.
/// </summary>
public sealed class MacShellIntegration : IShellIntegration
{
    public const string WorkflowName = "ZipStream Send.workflow";

    private readonly string _home;

    public MacShellIntegration(string home)
    {
        _home = home;
    }

    public string TargetDescription => "Finder";

    private string WorkflowPath => Path.Combine(_home, "Library/Services", WorkflowName);

    private string DocumentPath => Path.Combine(WorkflowPath, "Contents/document.wflow");

    public ShellIntegrationStatus GetStatus(LaunchCommand command)
    {
        if (!File.Exists(DocumentPath))
            return ShellIntegrationStatus.NotInstalled;

        return File.ReadAllText(DocumentPath).Contains(EscapeXml(BuildShellCommand(command)), StringComparison.Ordinal)
            ? ShellIntegrationStatus.Installed
            : ShellIntegrationStatus.Outdated;
    }

    public void Install(LaunchCommand command, string label)
    {
        Uninstall();
        var contents = Path.Combine(WorkflowPath, "Contents");
        Directory.CreateDirectory(contents);
        File.WriteAllText(Path.Combine(contents, "Info.plist"), BuildInfoPlist(label));
        File.WriteAllText(DocumentPath, BuildDocument(command));
        RefreshServices();
    }

    public void Uninstall()
    {
        if (Directory.Exists(WorkflowPath))
            Directory.Delete(WorkflowPath, recursive: true);
    }

    /// <summary>Script executado pelo "Run Shell Script" (itens recebidos como argumentos).</summary>
    public static string BuildShellCommand(LaunchCommand command)
    {
        static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";
        var prefix = string.Join(' ', new[] { command.Executable }.Concat(command.PrefixArguments).Select(Q));
        return $"for f in \"$@\"; do {prefix} --send \"$f\" >/dev/null 2>&1 & break; done";
    }

    public static string BuildInfoPlist(string label) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>NSServices</key>
            <array>
                <dict>
                    <key>NSMenuItem</key>
                    <dict>
                        <key>default</key>
                        <string>{EscapeXml(label)}</string>
                    </dict>
                    <key>NSMessage</key>
                    <string>runWorkflowAsService</string>
                    <key>NSRequiredContext</key>
                    <dict>
                        <key>NSApplicationIdentifier</key>
                        <string>com.apple.finder</string>
                    </dict>
                    <key>NSSendFileTypes</key>
                    <array>
                        <string>public.item</string>
                    </array>
                </dict>
            </array>
        </dict>
        </plist>
        """;

    public static string BuildDocument(LaunchCommand command) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>AMApplicationBuild</key>
            <string>523</string>
            <key>AMApplicationVersion</key>
            <string>2.10</string>
            <key>AMDocumentVersion</key>
            <string>2</string>
            <key>actions</key>
            <array>
                <dict>
                    <key>action</key>
                    <dict>
                        <key>AMAccepts</key>
                        <dict>
                            <key>Container</key>
                            <string>List</string>
                            <key>Optional</key>
                            <true/>
                            <key>Types</key>
                            <array>
                                <string>com.apple.cocoa.string</string>
                            </array>
                        </dict>
                        <key>AMActionVersion</key>
                        <string>2.0.3</string>
                        <key>AMApplication</key>
                        <array>
                            <string>Automator</string>
                        </array>
                        <key>AMParameterProperties</key>
                        <dict>
                            <key>COMMAND_STRING</key>
                            <dict/>
                            <key>CheckedForUserDefaultShell</key>
                            <dict/>
                            <key>inputMethod</key>
                            <dict/>
                            <key>shell</key>
                            <dict/>
                            <key>source</key>
                            <dict/>
                        </dict>
                        <key>AMProvides</key>
                        <dict>
                            <key>Container</key>
                            <string>List</string>
                            <key>Types</key>
                            <array>
                                <string>com.apple.cocoa.string</string>
                            </array>
                        </dict>
                        <key>ActionBundlePath</key>
                        <string>/System/Library/Automator/Run Shell Script.action</string>
                        <key>ActionName</key>
                        <string>Run Shell Script</string>
                        <key>ActionParameters</key>
                        <dict>
                            <key>COMMAND_STRING</key>
                            <string>{EscapeXml(BuildShellCommand(command))}</string>
                            <key>CheckedForUserDefaultShell</key>
                            <true/>
                            <key>inputMethod</key>
                            <integer>1</integer>
                            <key>shell</key>
                            <string>/bin/bash</string>
                            <key>source</key>
                            <string></string>
                        </dict>
                        <key>BundleIdentifier</key>
                        <string>com.apple.RunShellScript</string>
                        <key>CFBundleVersion</key>
                        <string>2.0.3</string>
                        <key>CanShowSelectedItemsWhenRun</key>
                        <false/>
                        <key>CanShowWhenRun</key>
                        <true/>
                        <key>Category</key>
                        <array>
                            <string>AMCategoryUtilities</string>
                        </array>
                        <key>Class Name</key>
                        <string>RunShellScriptAction</string>
                        <key>InputUUID</key>
                        <string>8D5C3F1E-6B5A-4F6B-9C2E-2B7A1D3E4F50</string>
                        <key>Keywords</key>
                        <array>
                            <string>Shell</string>
                            <string>Script</string>
                        </array>
                        <key>OutputUUID</key>
                        <string>1A2B3C4D-5E6F-4A1B-8C2D-3E4F5A6B7C8D</string>
                        <key>UUID</key>
                        <string>0F1E2D3C-4B5A-4968-8776-655443322110</string>
                        <key>UnlocalizedApplications</key>
                        <array>
                            <string>Automator</string>
                        </array>
                        <key>arguments</key>
                        <dict/>
                        <key>isViewVisible</key>
                        <integer>1</integer>
                    </dict>
                    <key>isViewVisible</key>
                    <integer>1</integer>
                </dict>
            </array>
            <key>connectors</key>
            <dict/>
            <key>workflowMetaData</key>
            <dict>
                <key>applicationBundleID</key>
                <string>com.apple.finder</string>
                <key>serviceInputTypeIdentifier</key>
                <string>com.apple.Automator.fileSystemObject</string>
                <key>serviceOutputTypeIdentifier</key>
                <string>com.apple.Automator.nothing</string>
                <key>serviceProcessesInput</key>
                <integer>0</integer>
                <key>workflowTypeIdentifier</key>
                <string>com.apple.Automator.servicesMenu</string>
            </dict>
        </dict>
        </plist>
        """;

    private static string EscapeXml(string text)
        => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>Pede ao macOS para recarregar a lista de serviços (melhor esforço).</summary>
    private static void RefreshServices()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        try
        {
            using var process = Process.Start(new ProcessStartInfo("/System/Library/CoreServices/pbs", "-update")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            process?.WaitForExit(5000);
        }
        catch
        {
            // sem pbs: o Finder pega a ação no próximo login
        }
    }
}
