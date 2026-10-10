using System.Globalization;
using System.Reflection;

namespace Momentum4.CoreHost;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args is ["--version"])
        {
            var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
            Console.WriteLine($"momentum-core-host {version} protocol 1");
            return 0;
        }
        try
        {
            await using Stream output = OperatingSystem.IsLinux() ? new LinuxProtocolOutput() : Console.OpenStandardOutput();
            await using var host = new PrivateHost(Console.OpenStandardInput(), output);
            if (args is ["headset", "--fd", var fd] && int.TryParse(fd, NumberStyles.None, CultureInfo.InvariantCulture, out var descriptor) && descriptor >= 3)
            {
                await using var operations = new HeadsetOperations(descriptor);
                using var terminal = new CancellationTokenSource();
                var watch = WatchCompletionAsync(operations.Completion, terminal);
                try { await host.RunAsync(operations.DispatchAsync, terminal.Token); }
                finally
                {
                    await operations.DisposeAsync();
                    await watch;
                }
                return 0;
            }
            if (args is ["dongle"])
            {
                using var operations = new DongleOperations(host);
                await host.RunAsync(operations.DispatchAsync, CancellationToken.None);
                return 0;
            }
            return 2;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("Shared core session ended.");
            return 1;
        }
    }

    private static async Task WatchCompletionAsync(Task completion, CancellationTokenSource terminal)
    {
        try { await completion; }
        catch (Exception) { }
        await terminal.CancelAsync();
    }
}
