using System.Reflection;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Data.Hooks;
using KyberWeave.Testing.Squad;
using Xunit;

namespace KyberWeave.Tests.FileSystem;

/// <summary>
/// The flush choice of an atomic write is a default interface member that quietly drops the flag, so every class that
/// stands in for the file system must say what it does with the flag, or a caller asking for a flush would not get one.
/// </summary>
public sealed class WritableFileSystemFlushForwardingTests
{
    /// <summary>Every concrete file system and wrapper declares both flush overloads itself.</summary>
    [Fact]
    public void EveryFileSystemImplementationDeclaresBothFlushOverloads()
    {
        Assembly[] assemblies =
        [
            typeof(PhysicalFileSystem).Assembly,
            typeof(InMemoryFileSystem).Assembly,
            typeof(FaultInjectingFileSystem).Assembly,
        ];
        List<string> missing = [];
        foreach (Type type in assemblies.SelectMany(assembly => assembly.GetTypes()).Distinct())
        {
            if (type.IsAbstract || type.IsInterface || !typeof(IWritableFileSystem).IsAssignableFrom(type))
            {
                continue;
            }

            if (Declared(type, nameof(IWritableFileSystem.WriteAllBytesAtomic), typeof(byte[])) is null)
            {
                missing.Add($"{type.FullName}.WriteAllBytesAtomic(path, bytes, flushToDisk)");
            }

            if (Declared(type, nameof(IWritableFileSystem.WriteAllTextAtomic), typeof(string)) is null)
            {
                missing.Add($"{type.FullName}.WriteAllTextAtomic(path, contents, flushToDisk)");
            }
        }

        Assert.True(missing.Count == 0, "These drop the flush flag by default: " + string.Join(", ", missing));
    }

    /// <summary>A wrapper passes the flag it was given on to the file system it wraps.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AFaultInjectingWrapperPassesTheFlushFlagOn(bool flushToDisk)
    {
        InMemoryFileSystem inner = new();
        FlushRecorder recorder = new();
        FaultInjectingFileSystem wrapper = new(recorder.Wrap(inner));
        string directory = Path.Combine(inner.CreateTemporaryDirectory("kw-flush-"), "flush");
        wrapper.CreateDirectory(directory);

        wrapper.WriteAllBytesAtomic(Path.Combine(directory, "a.bin"), [1], flushToDisk);
        wrapper.WriteAllTextAtomic(Path.Combine(directory, "a.txt"), "x", flushToDisk);

        Assert.Equal([flushToDisk, flushToDisk], recorder.Flags);
    }

    private static MethodInfo? Declared(Type type, string name, Type contentType) =>
        type.GetMethod(
            name,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
            [typeof(string), contentType, typeof(bool)]);

    /// <summary>Records the flush flag of each three-argument atomic write that reaches the file system it wraps.</summary>
    private sealed class FlushRecorder
    {
        public List<bool> Flags { get; } = [];

        public IWritableFileSystem Wrap(IWritableFileSystem target)
        {
            IWritableFileSystem proxy = DispatchProxy.Create<IWritableFileSystem, FlushRecordingProxy>();
            FlushRecordingProxy typed = (FlushRecordingProxy)(object)proxy;
            typed.Target = target;
            typed.Flags = Flags;
            return proxy;
        }
    }
}

/// <summary>The generated proxy: it calls the target and notes the flag of the flush overloads.</summary>
#pragma warning disable CA1812 // created by DispatchProxy.Create
internal class FlushRecordingProxy : DispatchProxy
{
    public IWritableFileSystem Target { get; set; } = null!;

    public IList<bool> Flags { get; set; } = [];

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        if (targetMethod.Name is nameof(IWritableFileSystem.WriteAllBytesAtomic) or nameof(IWritableFileSystem.WriteAllTextAtomic)
            && args is { Length: 3 })
        {
            Flags.Add((bool)args[2]!);
        }

        try
        {
            return targetMethod.Invoke(Target, args);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }
    }
}
#pragma warning restore CA1812
