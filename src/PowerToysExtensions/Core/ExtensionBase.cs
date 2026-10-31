using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace PowerToysExtensions.Core;

/// <summary>
/// One Command Palette extension of this package. Each subclass needs its own [Guid] that matches a
/// com:Class and an AppExtension in Package.appxmanifest.
/// </summary>
public abstract partial class ExtensionBase : IExtension, IDisposable
{
    private static int _alive;

    /// <summary>Signalled when Command Palette has released every extension of this process.</summary>
    public static ManualResetEvent AllDisposed { get; } = new(false);

    protected ExtensionBase()
    {
        Interlocked.Increment(ref _alive);
    }

    public bool IsDisposed { get; private set; }

    protected abstract CommandProvider Provider { get; }

    public object? GetProvider(ProviderType providerType) => providerType == ProviderType.Commands ? Provider : null;

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        Provider.Dispose();
        if (Interlocked.Decrement(ref _alive) == 0)
        {
            // Command Palette may ask for another extension right after releasing one (reload)
            _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ =>
            {
                if (Volatile.Read(ref _alive) == 0)
                {
                    AllDisposed.Set();
                }
            });
        }

        GC.SuppressFinalize(this);
    }
}
