using Microsoft.CommandPalette.Extensions;
using PowerToysExtensions.Core;
using PowerToysExtensions.Extensions.BookStack;
using PowerToysExtensions.Extensions.Checkmk;
using PowerToysExtensions.Extensions.KeepAwake;
using PowerToysExtensions.Extensions.LauncherKey;
using PowerToysExtensions.Extensions.OneDev;
using PowerToysExtensions.Extensions.SnipeIt;
using Shmuelie.WinRTServer.CsWinRT;

namespace PowerToysExtensions;

public static class Program
{
    [MTAThread]
    public static void Main(string[] args)
    {
        if (args.Length == 0 || args[0] != "-RegisterProcessAsComServer")
        {
            Console.WriteLine("This program is started by PowerToys Command Palette.");
            return;
        }

        Log.Info("starting");
        var server = new Shmuelie.WinRTServer.ComServer();

        // Every extension of the package is its own COM class (see Package.appxmanifest).
        // Command Palette asks for each one separately; the process lives until all are disposed.
        Register(server, () => new KeepAwakeExtension());
        Register(server, () => new LauncherKeyExtension());
        Register(server, () => new BookStackExtension());
        Register(server, () => new OneDevExtension());
        Register(server, () => new CheckmkExtension());
        Register(server, () => new SnipeItExtension());

        server.Start();
        ExtensionBase.AllDisposed.WaitOne();
        server.Stop();
        server.UnsafeDispose();
        Log.Info("stopped");
    }

    private static void Register<T>(Shmuelie.WinRTServer.ComServer server, Func<T> create)
        where T : ExtensionBase
    {
        T? instance = null;
        server.RegisterClass<T, IExtension>(() => instance is { IsDisposed: false } ? instance : instance = create());
    }
}
