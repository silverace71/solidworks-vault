using SwVault.Core.Client;
using SwVault.Protocol;

namespace SwVault.Agent;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var profileIndex = Array.IndexOf(args, "--profile");
        var profile = new SwVaultProfile(profileIndex >= 0 && profileIndex + 1 < args.Length ? args[profileIndex + 1] : null);

        // A swvault:// link from the web file browser (Windows starts us with --link "<uri>").
        var linkIndex = Array.IndexOf(args, "--link");
        var link = linkIndex >= 0 && linkIndex + 1 < args.Length ? args[linkIndex + 1] : null;

        // One agent per user and profile; a second launch (e.g. from the add-in) just exits,
        // after handing its link to the running agent.
        using var mutex = new Mutex(initiallyOwned: true, @"Local\SwVault.Agent." + profile.Name, out var isFirst);
        if (link != null) LogLink(profile, (isFirst ? "Started by a link (no agent was running): " : "Link received, passing it to the running agent: ") + link);
        if (!isFirst)
        {
            if (link != null) ForwardLink(profile, link);
            return;
        }

        ApplicationConfiguration.Initialize();
        using var agent = new AgentHost(profile);
        Application.ThreadException += (_, e) => agent.Log.Error("UI thread exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => agent.Log.Error("Unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            agent.Log.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
        // Subscribe to the one-time migration event BEFORE Start(): an already
        // armed 1.2.9 -> 1.4 cutover can be applied at process startup.
        using var tray = new TrayContext(agent);
        agent.Start();
        if (link != null) _ = Task.Run(() => agent.HandleLinkAsync(link));
        Application.Run(tray);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    private static void ForwardLink(SwVaultProfile profile, string link)
    {
        try
        {
            // This process was started by a click in the web browser, so it may hand the
            // foreground on: SOLIDWORKS (or the agent's window) comes to the front.
            AllowSetForegroundWindow(-1);
            using var client = new AgentClient();
            client.ConnectAsync(profile.PipeName, "link", typeof(Program).Assembly.GetName().Version?.ToString() ?? "", 5000, CancellationToken.None).GetAwaiter().GetResult();
            client.CallAsync<LinkRequest, object>(Methods.LinkHandle, new LinkRequest { Uri = link }).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            LogLink(profile, "Could not pass the link to the running agent: " + ex);
            ApplicationConfiguration.Initialize();
            MessageBox.Show("SwVault couldn't open that link: " + ex.Message, "SwVault", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// The process Windows starts for a swvault:// click writes its own line (to links.log in the
    /// log folder) before it talks to the running agent, so a click that never arrives can be told
    /// apart from one that was never sent: no line here means the browser didn't start SwVault.
    /// </summary>
    private static void LogLink(SwVaultProfile profile, string message)
    {
        try
        {
            Directory.CreateDirectory(profile.LogDir);
            File.AppendAllText(Path.Combine(profile.LogDir, "links.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} pid {Environment.ProcessId} {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Diagnostics only.
        }
    }
}
