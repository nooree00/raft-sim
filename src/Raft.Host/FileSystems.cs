using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Raft.Host;

/// <summary>
/// The files a host keeps in its data directory (P9-03). Every write lands in the operating system
/// at once; <see cref="Sync"/> and <see cref="SyncDirectory"/> make it durable. The interface is the
/// seam a test records through, and the one fsync failure injection would use (the register's
/// phase-10 row, phase 9 decision 7).
/// </summary>
public interface IFileSystem
{
    IReadOnlyList<string> List();

    byte[]? Read(string name);

    bool Exists(string name);

    void Append(string name, ReadOnlySpan<byte> data);

    void WriteAt(string name, long offset, ReadOnlySpan<byte> data);

    void Truncate(string name, long length);

    void Rename(string source, string target);

    void Delete(string name);

    /// <summary>Make the file's data durable (fsync).</summary>
    void Sync(string name);

    /// <summary>Make the directory's entries durable: a rename, a create or a delete (fsync of the directory).</summary>
    void SyncDirectory();
}

/// <summary>A host's data directory on the real file system.</summary>
public sealed class DirectoryFileSystem : IFileSystem
{
    private readonly string _dir;

    public DirectoryFileSystem(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
    }

    private string P(string name) => Path.Combine(_dir, name);

    public IReadOnlyList<string> List()
    {
        var names = new List<string>();
        foreach (var f in Directory.GetFiles(_dir))
        {
            names.Add(Path.GetFileName(f));
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }

    public byte[]? Read(string name) => File.Exists(P(name)) ? File.ReadAllBytes(P(name)) : null;

    public bool Exists(string name) => File.Exists(P(name));

    public void Append(string name, ReadOnlySpan<byte> data)
    {
        using var s = new FileStream(P(name), FileMode.Append, FileAccess.Write, FileShare.None);
        s.Write(data);
    }

    public void WriteAt(string name, long offset, ReadOnlySpan<byte> data)
    {
        using var s = new FileStream(P(name), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        s.Seek(offset, SeekOrigin.Begin);
        s.Write(data);
    }

    public void Truncate(string name, long length)
    {
        if (!File.Exists(P(name)))
        {
            return;
        }

        using var s = new FileStream(P(name), FileMode.Open, FileAccess.Write, FileShare.None);
        if (s.Length > length)
        {
            s.SetLength(length);
        }
    }

    public void Rename(string source, string target) => File.Move(P(source), P(target), overwrite: true);

    public void Delete(string name) => File.Delete(P(name));

    public void Sync(string name)
    {
        if (!File.Exists(P(name)))
        {
            return;
        }

        using var s = new FileStream(P(name), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        s.Flush(flushToDisk: true);
    }

    /// <summary>
    /// fsync of the directory. .NET has no managed call for it: opening a directory as a file handle
    /// fails on Linux (P9-03, measured: UnauthorizedAccessException). So `open(2)` with O_DIRECTORY
    /// and `fsync(2)`, through P/Invoke, here and nowhere else.
    /// </summary>
    public void SyncDirectory()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("the host syncs its data directory with open(2) and fsync(2), on Linux");
        }

        var fd = NativeMethods.Open(_dir, NativeMethods.ReadOnlyDirectory);
        if (fd < 0)
        {
            throw new IOException($"open({_dir}) for a directory sync failed: errno {Marshal.GetLastPInvokeError()}");
        }

        try
        {
            if (NativeMethods.Fsync(fd) != 0)
            {
                throw new IOException($"fsync({_dir}) failed: errno {Marshal.GetLastPInvokeError()}");
            }
        }
        finally
        {
            _ = NativeMethods.Close(fd);
        }
    }
}

/// <summary>The three libc calls a directory sync needs (Linux).</summary>
internal static partial class NativeMethods
{
    /// <summary>O_RDONLY | O_DIRECTORY on Linux.</summary>
    public const int ReadOnlyDirectory = 0x10000;

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    public static partial int Fsync(int fd);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    public static partial int Close(int fd);
}
