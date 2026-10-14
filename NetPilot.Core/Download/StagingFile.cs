using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace NetPilot.Services;

/// <summary>
/// Where a bonded download is assembled, and the record of which blocks are already in it.
///
/// Every stream writes into this one file at its block's absolute offset, so the file is the
/// download: pause the process, kill it, or lose the power, and the bytes already fetched are
/// still there and still in the right place. Nothing about a transfer has to be reconstructed
/// from memory.
///
/// Two rules that are not negotiable, because both of them silently produce a corrupt file that
/// looks complete:
///
///   Writes are random-access and thread-safe. <see cref="FileStream"/> is not safe for
///   concurrent writes at different positions - it shares one seek cursor - so each block is
///   written with <see cref="RandomAccess"/>, which takes the position per call.
///
///   A resume proves the file on the server has not changed. If the ETag or Last-Modified moved
///   since the previous run, the bytes on disk belong to a different file, and the only safe
///   answer is to refuse rather than to mix the two. That refusal is what the progress record
///   exists to make possible.
/// </summary>
public sealed class StagingFile : IDisposable
{
    /// <summary>Appended to the destination, so a half-downloaded file never masquerades as a
    /// finished one and an interrupted download can be found again.</summary>
    public const string Suffix = ".netpilot";

    private readonly SafeFileHandle _handle;
    private readonly string _progressPath;
    private readonly HashSet<int> _done = new();
    private readonly object _gate = new();

    public string Path { get; }
    public long TotalBytes { get; }

    /// <summary>The raw handle, for a caller that wants to write sequentially at its own pace.</summary>
    internal SafeFileHandle Handle => _handle;

    /// <summary>The server's version markers from the run that started this file.</summary>
    public string ETag { get; private set; }
    public string LastModified { get; private set; }

    private StagingFile(SafeFileHandle handle, string path, long total, string progressPath)
    {
        _handle = handle; Path = path; TotalBytes = total; _progressPath = progressPath;
    }

    /// <summary>
    /// Opens the staging file for a download, resuming it when the record says it is safe.
    ///
    /// <paramref name="resume"/> is refused rather than guessed at: the caller gets false when
    /// the previous run's file is a different file on the server, and the download starts over.
    /// </summary>
    public static StagingFile Open(string destination, long totalBytes,
                                   string etag, string lastModified, out bool resume)
    {
        if (string.IsNullOrWhiteSpace(destination))
            throw new ArgumentException("a destination path is required", nameof(destination));

        string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(destination));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string path = destination + Suffix;
        string progressPath = path + ".progress";

        var handle = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                                     FileOptions.Asynchronous);
        var file = new StagingFile(handle, path, totalBytes, progressPath);
        file.ETag = etag;
        file.LastModified = lastModified;

        // The file exists from a previous run: this is a resume, and only if the server still
        // has the same file.
        Record previous = null;
        resume = File.Exists(progressPath) && file.TryLoadRecord(out previous)
                                             && previous.ETag == etag
                                             && previous.LastModified == lastModified
                                             && previous.TotalBytes == totalBytes;

        if (resume) { lock (file._gate) foreach (var i in previous.Done) file._done.Add(i); }
        else file.SaveRecord();

        RandomAccess.SetLength(handle, totalBytes);
        return file;
    }

    /// <summary>True when this block has already been written by a previous run.</summary>
    public bool IsWritten(int blockIndex)
    {
        lock (_gate) return _done.Contains(blockIndex);
    }

    /// <summary>Writes one block at its own offset, and marks it done.</summary>
    public async System.Threading.Tasks.ValueTask WriteAsync(int blockIndex, long offset,
                                                             ReadOnlyMemory<byte> bytes,
                                                             CancellationToken ct)
    {
        await RandomAccess.WriteAsync(_handle, bytes, offset, ct).ConfigureAwait(false);

        bool first;
        lock (_gate) first = _done.Add(blockIndex);
        if (first) SaveRecord();
    }

    /// <summary>
    /// Renames the assembled file to its final name, and drops the record.
    ///
    /// A numbered name is picked rather than overwriting, so a previous download of the same
    /// file is never destroyed to make room for the new one.
    /// </summary>
    public void Finish(string destination)
    {
        Dispose();

        string final = destination;
        if (File.Exists(final))
        {
            string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(final)) ?? ".";
            string stem = System.IO.Path.GetFileNameWithoutExtension(final);
            string ext = System.IO.Path.GetExtension(final);
            for (int n = 1; ; n++)
            {
                final = System.IO.Path.Combine(dir, $"{stem} ({n}){ext}");
                if (!File.Exists(final)) break;
            }
        }

        File.Move(Path, final);
        TryDelete(_progressPath);
    }

    public void Dispose()
    {
        try { _handle.Dispose(); } catch { }
    }

    // ---------------- the record ----------------

    private sealed class Record
    {
        public string ETag { get; set; }
        public string LastModified { get; set; }
        public long TotalBytes { get; set; }
        public List<int> Done { get; set; } = new();
    }

    private bool TryLoadRecord(out Record record)
    {
        record = null;
        try
        {
            record = JsonSerializer.Deserialize<Record>(File.ReadAllText(_progressPath));
            return record?.Done != null;
        }
        catch { return false; }   // unreadable or half-written: start over rather than guess
    }

    /// <summary>
    /// Written after every block, which sounds expensive and is not: the record holds block
    /// indices only, so a 4 GB file is a few kilobytes, and it is fsynced by the OS on its own
    /// schedule. A record that is one block out of date costs one block, not one file.
    /// </summary>
    private void SaveRecord()
    {
        try
        {
            var record = new Record { ETag = ETag, LastModified = LastModified, TotalBytes = TotalBytes };
            lock (_gate) record.Done.AddRange(_done);
            File.WriteAllText(_progressPath, JsonSerializer.Serialize(record));
        }
        catch { }   // a record that cannot be written only costs a re-download
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}