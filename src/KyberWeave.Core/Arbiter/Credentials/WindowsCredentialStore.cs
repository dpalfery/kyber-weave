using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace KyberWeave.Core.Arbiter.Credentials;

/// <summary>Windows Credential Manager store for the Arbiter key.</summary>
/// <remarks>
/// Target <c>kyber-weave-arbiter:&lt;origin&gt;</c>, reached by P/Invoke into
/// <c>advapi32</c> (<c>CredWriteW</c>, <c>CredReadW</c>, <c>CredFree</c>) with no package,
/// so the Core dependency set stays Markdig and YamlDotNet only. The file compiles on
/// every OS; both entry points return early through
/// <see cref="OperatingSystem.IsWindows"/> before touching native code, so the
/// <c>advapi32</c> import never loads elsewhere.
/// </remarks>
public sealed partial class WindowsCredentialStore : ICredentialStore
{
    private const string Service = "kyber-weave-arbiter";

    private const uint GenericType = 1;

    private const int LocalMachinePersist = 2;

    /// <inheritdoc />
    public string? Read(string origin)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "The Windows credential store is only available on Windows.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        string target = TargetFor(origin);

        if (!NativeMethods.CredReadW(target, GenericType, 0, out nint credential) || credential == 0)
        {
            return null;
        }

        try
        {
            NativeCredential stored = Marshal.PtrToStructure<NativeCredential>(credential);
            if (stored.CredentialBlobSize <= 0 || stored.CredentialBlob == 0)
            {
                return null;
            }

            byte[] bytes = new byte[stored.CredentialBlobSize];
            Marshal.Copy(stored.CredentialBlob, bytes, 0, bytes.Length);
            try
            {
                string key = Encoding.UTF8.GetString(bytes);
                return string.IsNullOrEmpty(key) ? null : key;
            }
            finally
            {
                Array.Clear(bytes);
            }
        }
        finally
        {
            NativeMethods.CredFree(credential);
        }
    }

    /// <inheritdoc />
    public void Write(string origin, string key)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "The Windows credential store is only available on Windows.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        ArgumentException.ThrowIfNullOrEmpty(key);

        byte[] bytes = Encoding.UTF8.GetBytes(key);
        nint targetPtr = Marshal.StringToHGlobalUni(TargetFor(origin));
        nint blob = Marshal.AllocHGlobal(Math.Max(bytes.Length, 1));
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            NativeCredential credential = new()
            {
                Type = GenericType,
                TargetName = targetPtr,
                CredentialBlobSize = bytes.Length,
                CredentialBlob = blob,
                Persist = LocalMachinePersist,
            };

            if (!NativeMethods.CredWriteW(credential, 0))
            {
                throw new InvalidOperationException(
                    "The Windows credential store write failed.");
            }
        }
        finally
        {
            for (int index = 0; index < bytes.Length; index++)
            {
                Marshal.WriteByte(blob + index, 0);
            }

            Marshal.FreeHGlobal(blob);
            Marshal.FreeHGlobal(targetPtr);
            Array.Clear(bytes);
        }
    }

    private static string TargetFor(string origin) => $"{Service}:{origin}";

    /// <summary>Raw <c>advapi32</c> entry points. Windows-only; callers gate first.</summary>
    [SupportedOSPlatform("windows")]
    private static partial class NativeMethods
    {
        [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CredWriteW(NativeCredential credential, uint flags);

        [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CredReadW(string targetName, uint type, uint flags, out nint credential);

        [LibraryImport("advapi32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static partial void CredFree(nint buffer);
    }

    /// <summary>Managed view of the native <c>CREDENTIALW</c> record.</summary>
    /// <remarks>
    /// The layout mirrors the native record field for field, with every string-typed
    /// field held as an explicit UTF-16 pointer rather than a managed string. Keeping
    /// the record blittable is what lets <c>LibraryImport</c> generate its marshalling
    /// without unsafe blocks, so the project file stays untouched. Only the target,
    /// blob and persist take part in reads and writes; the remaining fields stay so
    /// the prefix offsets the native side fills match what <c>PtrToStructure</c> reads.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCredential
    {
        public int Flags;
        public uint Type;
        public nint TargetName;
        public nint Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public nint CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public nint Attributes;
        public nint TargetAlias;
        public nint UserName;
    }
}
