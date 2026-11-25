using KyberDash.Core.Exceptions;
using SQLitePCL;

namespace KyberDash.Core.Repositories;

/// <summary>Registers the system SQLite library with <c>Microsoft.Data.Sqlite</c> once per process.</summary>
/// <remarks>
/// <c>Microsoft.Data.Sqlite.Core</c> ships no native SQLite, so every in-process reader and writer
/// (the canonical store and the harness SQLite readers alike) must register
/// <see cref="SQLite3Provider_sqlite3"/> before opening a connection. One shared gate keeps the
/// registration to a single call however many of them start first.
/// </remarks>
public static class SqliteProvider
{
    private static readonly Lock Gate = new();
    private static volatile bool initialized;

    /// <summary>Ensures the system sqlite3 dynamic library provider is registered once.</summary>
    /// <exception cref="SqliteExecutionException">The native <c>sqlite3</c> library cannot be loaded.</exception>
    public static void EnsureInitialized()
    {
        if (initialized) return;
        lock (Gate)
        {
            if (initialized) return;
            try
            {
                raw.SetProvider(new SQLite3Provider_sqlite3());
                initialized = true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException or InvalidOperationException or BadImageFormatException)
            {
                throw new SqliteExecutionException("The native 'sqlite3' dynamic library is unavailable.", ex);
            }
        }
    }
}
