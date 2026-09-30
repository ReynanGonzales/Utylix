using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace IdmClone.Engine;

/// <summary>One file or folder inside an archive. <see cref="Path"/> uses "/" and has no trailing slash.</summary>
public sealed record ArchiveEntryInfo(string Path, bool IsDirectory, long Size, long Packed, DateTime? Modified, bool Encrypted);

public sealed class ArchiveListing
{
    public string Path { get; init; } = "";
    public string Format { get; init; } = "";
    public List<ArchiveEntryInfo> Entries { get; init; } = new();
    public long TotalSize { get; init; }
    public long TotalPacked { get; init; }
    public bool HasEncrypted { get; init; }
    public int FileCount => Entries.Count(e => !e.IsDirectory);
}

public enum OverwriteMode { Rename, Overwrite, Skip }
public enum ArchiveFormat { Zip, TarGz, Tar }

public sealed record ExtractResult(int Files, int Skipped, long Bytes, string Folder);

/// <summary>The archive needs a password, or the one given was wrong.</summary>
public sealed class ArchivePasswordException : Exception
{
    public bool Wrong { get; }
    public ArchivePasswordException(bool wrong) : base(wrong ? "That password is not right." : "This archive is protected by a password.") { Wrong = wrong; }
}

/// <summary>
/// Opening, extracting, testing and creating archives. Reading (ZIP, RAR, 7z, TAR, GZ, BZ2, XZ ...) uses SharpCompress;
/// creating ZIP and TAR(.GZ) uses .NET itself. RAR and 7z can be opened but not created (RAR is a closed format).
/// </summary>
public static class ArchiveService
{
    /// <summary>Endings Utylix opens as archives (and offers itself for in Explorer's "Open with").</summary>
    public static readonly string[] Extensions =
        { "zip", "rar", "7z", "tar", "gz", "tgz", "bz2", "tbz2", "xz", "txz", "cbz", "cbr", "jar" };

    public static bool IsArchiveName(string path)
    {
        string name = Path.GetFileName(path).ToLowerInvariant();
        if (name.EndsWith(".tar.gz") || name.EndsWith(".tar.bz2") || name.EndsWith(".tar.xz")) return true;
        return Extensions.Contains(Path.GetExtension(name).TrimStart('.'));
    }

    /// <summary>"photos.tar.gz" -> "photos"; "backup.zip" -> "backup".</summary>
    public static string BaseName(string archivePath)
    {
        string name = Path.GetFileName(archivePath);
        foreach (var two in new[] { ".tar.gz", ".tar.bz2", ".tar.xz" })
            if (name.EndsWith(two, StringComparison.OrdinalIgnoreCase)) return name[..^two.Length];
        return Path.GetFileNameWithoutExtension(name);
    }

    public static string Normalize(string key) => key.Replace('\\', '/').Trim('/');

    // ---------- reading ----------
    private static ReaderOptions Options(string? password) => new() { Password = string.IsNullOrEmpty(password) ? null : password, LeaveStreamOpen = false };

    /// <summary>Anything that is not one of the errors we deliberately raise (a library's own exception types, bad data ...).</summary>
    private static bool IsUnexpected(Exception e) => e is not (OperationCanceledException or IOException or UnauthorizedAccessException or ArchivePasswordException);

    public static ArchiveListing List(string path, string? password = null)
    {
        try
        {
            try { return ListWithArchive(path, password); }
            catch (SharpCompress.Common.ArchiveOperationException) { return ListWithReader(path, password); }    // compressed TARs: .tar.gz, .tar.bz2, .tar.xz
        }
        catch (SharpCompress.Common.CryptographicException) { throw new ArchivePasswordException(!string.IsNullOrEmpty(password)); }
        catch (Exception e) when (!string.IsNullOrEmpty(password) && (e is SharpCompress.Common.InvalidFormatException || e.GetType().Name == "DataErrorException"))
        {
            throw new ArchivePasswordException(true);        // an encrypted 7z opened with a wrong password fails as "CRC mismatch" / "data error"
        }
        catch (Exception e) when (IsUnexpected(e))
        {
            if (LooksLikePassword(e)) throw new ArchivePasswordException(!string.IsNullOrEmpty(password));
            // An AES-encrypted ZIP is not even recognised as a ZIP until the password is given: it starts like a ZIP but can't be read.
            if (e is SharpCompress.Common.ArchiveOperationException && HasZipSignature(path)) throw new ArchivePasswordException(!string.IsNullOrEmpty(password));
            throw new IOException("Utylix can't open this archive (" + e.Message + "). It may be damaged or a type it doesn't know.");
        }
    }

    private static bool HasZipSignature(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var b = new byte[4];
            return fs.Read(b, 0, 4) == 4 && b[0] == 'P' && b[1] == 'K' && (b[2] == 3 || b[2] == 5) && (b[3] == 4 || b[3] == 6);
        }
        catch (Exception) { return false; }
    }

    private static ArchiveListing ListWithArchive(string path, string? password)
    {
        using var archive = ArchiveFactory.OpenArchive(path, Options(password));
        var entries = new List<ArchiveEntryInfo>();
        bool encrypted = false;
        foreach (var e in archive.Entries)
        {
            string key = Normalize(e.Key ?? "");
            if (key.Length == 0) continue;
            encrypted |= e.IsEncrypted;
            entries.Add(new ArchiveEntryInfo(key, e.IsDirectory, Math.Max(0, e.Size), Math.Max(0, e.CompressedSize), e.LastModifiedTime, e.IsEncrypted));
        }
        return new ArchiveListing
        {
            Path = path, Format = FormatName(archive.Type, path), Entries = entries,
            TotalSize = entries.Where(x => !x.IsDirectory).Sum(x => x.Size), TotalPacked = new FileInfo(path).Length, HasEncrypted = encrypted,
        };
    }

    /// <summary>For formats that can only be read from start to end (compressed TARs): read once through to list them.</summary>
    private static ArchiveListing ListWithReader(string path, string? password)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = ReaderFactory.OpenReader(fs, Options(password));
        var entries = new List<ArchiveEntryInfo>();
        while (reader.MoveToNextEntry())
        {
            var e = reader.Entry;
            string key = Normalize(e.Key ?? "");
            if (key.Length == 0) continue;
            entries.Add(new ArchiveEntryInfo(key, e.IsDirectory, Math.Max(0, e.Size), Math.Max(0, e.CompressedSize), e.LastModifiedTime, e.IsEncrypted));
        }
        return new ArchiveListing
        {
            Path = path, Format = FormatName(ArchiveType.Tar, path), Entries = entries,
            TotalSize = entries.Where(x => !x.IsDirectory).Sum(x => x.Size), TotalPacked = new FileInfo(path).Length,
        };
    }

    private static bool LooksLikePassword(Exception e) => e.Message.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                                                          e.Message.Contains("encrypted", StringComparison.OrdinalIgnoreCase);

    private static string FormatName(ArchiveType type, string path)
    {
        string name = Path.GetFileName(path).ToLowerInvariant();
        if (name.EndsWith(".tar.gz") || name.EndsWith(".tgz")) return "TAR.GZ";
        if (name.EndsWith(".tar.bz2") || name.EndsWith(".tbz2")) return "TAR.BZ2";
        if (name.EndsWith(".tar.xz") || name.EndsWith(".txz")) return "TAR.XZ";
        return type switch { ArchiveType.Rar => "RAR", ArchiveType.SevenZip => "7z", ArchiveType.Zip => "ZIP", ArchiveType.Tar => "TAR", ArchiveType.GZip => "GZ", _ => type.ToString().ToUpperInvariant() };
    }

    // ---------- extracting ----------
    /// <summary>The safe full path for an entry inside <paramref name="root"/>, or null when it would land outside it ("../" tricks).</summary>
    public static string? SafeTarget(string root, string key)
    {
        string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string target;
        try { target = Path.GetFullPath(Path.Combine(rootFull, key.Replace('/', Path.DirectorySeparatorChar))); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        return target.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) ? target : null;
    }

    private static bool Wanted(string key, IReadOnlyCollection<string>? only) =>
        only == null || only.Any(s => key.Equals(s, StringComparison.OrdinalIgnoreCase) || key.StartsWith(s + "/", StringComparison.OrdinalIgnoreCase));

    /// <summary>Extracts everything (or only the chosen files/folders) into <paramref name="destination"/>. Progress is 0..1.</summary>
    public static Task<ExtractResult> ExtractAsync(string path, string destination, IReadOnlyCollection<string>? only, string? password,
                                                   OverwriteMode overwrite, IProgress<(double Fraction, string Current)>? progress, CancellationToken ct) =>
        Task.Run(() => Extract(path, destination, only, password, overwrite, progress, ct), ct);

    private static ExtractResult Extract(string path, string destination, IReadOnlyCollection<string>? only, string? password,
                                         OverwriteMode overwrite, IProgress<(double, string)>? progress, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(destination);

            // Most formats can be opened for random access; compressed TARs (.tar.gz ...) can only be read from start to end.
            IArchive? archive = null;
            try { archive = ArchiveFactory.OpenArchive(path, Options(password)); }
            catch (SharpCompress.Common.ArchiveOperationException) { }
            using var archiveHolder = archive;
            FileStream? streamFile = archive == null ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite) : null;
            using var streamHolder = streamFile;

            // how much will be written? (progress, and a check that the drive has room)
            long total = 0;
            if (archive != null)
            {
                var wanted = archive.Entries.Where(e => !e.IsDirectory && Wanted(Normalize(e.Key ?? ""), only)).ToList();
                total = wanted.Sum(e => Math.Max(0, e.Size));
                try
                {
                    long free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(destination))!).AvailableFreeSpace;
                    if (total > free - 50L * 1024 * 1024)
                        throw new IOException($"Not enough space on that drive: the files need {Format.Bytes(total)}, it has {Format.Bytes(free)} free.");
                }
                catch (ArgumentException) { /* a network path: skip the check */ }
            }

            long done = 0; int files = 0, skipped = 0;
            double Fraction() => total > 0 ? Math.Min(1.0, done / (double)total)
                                : streamFile is { Length: > 0 } sf ? Math.Min(1.0, sf.Position / (double)sf.Length) : 0;   // no sizes known: how far through the file we are
            void Report(string current) => progress?.Report((Fraction(), current));

            void Write(string key, bool isDir, DateTime? modified, Func<Stream> open)
            {
                ct.ThrowIfCancellationRequested();
                string? target = SafeTarget(destination, key);
                if (target == null) { skipped++; return; }                            // "../../x": never written outside the folder
                if (isDir) { Directory.CreateDirectory(target); return; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                {
                    if (overwrite == OverwriteMode.Skip) { skipped++; return; }
                    if (overwrite == OverwriteMode.Rename)
                        target = Path.Combine(Path.GetDirectoryName(target)!, Util.UniqueName(Path.GetDirectoryName(target)!, Path.GetFileName(target)));
                }
                Report(key);
                using (var src = open())
                using (var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    var buf = new byte[1 << 16];
                    int n;
                    while ((n = src.Read(buf, 0, buf.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        dst.Write(buf, 0, n);
                        done += n;
                        Report(key);
                    }
                }
                if (modified is { } m) { try { File.SetLastWriteTime(target, m); } catch (Exception) { } }
                files++;
            }

            if (archive != null && !(archive.IsSolid || archive.Type == ArchiveType.SevenZip))
            {
                foreach (var entry in archive.Entries)
                {
                    string key = Normalize(entry.Key ?? "");
                    if (key.Length == 0 || !Wanted(key, only)) continue;
                    var e = entry;
                    Write(key, e.IsDirectory, e.LastModifiedTime, () => e.IsDirectory ? Stream.Null : e.OpenEntryStream());
                }
            }
            else
            {
                // solid archives and compressed TARs: one pass, in order (much faster than jumping around)
                using var reader = archive != null ? archive.ExtractAllEntries() : ReaderFactory.OpenReader(streamFile!, Options(password));
                while (reader.MoveToNextEntry())
                {
                    string key = Normalize(reader.Entry.Key ?? "");
                    if (key.Length == 0 || !Wanted(key, only)) continue;
                    var entry = reader.Entry;
                    Write(key, entry.IsDirectory, entry.LastModifiedTime, () => entry.IsDirectory ? Stream.Null : reader.OpenEntryStream());
                }
            }
            progress?.Report((1, ""));
            return new ExtractResult(files, skipped, done, destination);
        }
        catch (SharpCompress.Common.CryptographicException) { throw new ArchivePasswordException(!string.IsNullOrEmpty(password)); }
        catch (Exception e) when (!string.IsNullOrEmpty(password) && (e is SharpCompress.Common.InvalidFormatException || e.GetType().Name == "DataErrorException"))
        {
            throw new ArchivePasswordException(true);        // an encrypted 7z opened with a wrong password fails as "CRC mismatch" / "data error"
        }
        catch (Exception e) when (IsUnexpected(e))
        {
            if (LooksLikePassword(e)) throw new ArchivePasswordException(!string.IsNullOrEmpty(password));
            if (e is SharpCompress.Common.ArchiveOperationException && HasZipSignature(path)) throw new ArchivePasswordException(!string.IsNullOrEmpty(password));
            throw new IOException("Couldn't extract: " + e.Message);
        }
    }

    /// <summary>Reads everything without saving it, to find out whether the archive is intact.</summary>
    public static Task<string> TestAsync(string path, string? password, IProgress<(double, string)>? progress, CancellationToken ct) =>
        Task.Run(() =>
        {
            string temp = Path.Combine(Path.GetTempPath(), "utylix-test-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                var result = Extract(path, temp, null, password, OverwriteMode.Overwrite, progress, ct);
                return $"OK: {result.Files} file{(result.Files == 1 ? "" : "s")}, {Format.Bytes(result.Bytes)}, nothing wrong found.";
            }
            finally { try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch (Exception) { } }
        }, ct);

    // ---------- creating ----------
    public static string Extension(ArchiveFormat f) => f switch { ArchiveFormat.Zip => ".zip", ArchiveFormat.TarGz => ".tar.gz", _ => ".tar" };

    /// <summary>"name.zip", or "name (1).zip" ... when taken.</summary>
    public static string UniquePath(string dir, string baseName, string ext)
    {
        baseName = Util.Sanitize(baseName);
        string candidate = Path.Combine(dir, baseName + ext);
        for (int i = 1; File.Exists(candidate) && i < 1000; i++) candidate = Path.Combine(dir, $"{baseName} ({i}){ext}");
        return candidate;
    }

    /// <summary>Every file below the given files/folders, with the name it gets inside the archive (top folder name + relative path).</summary>
    public static List<(string Full, string Name, long Size)> Expand(IEnumerable<string> sources)
    {
        var list = new List<(string, string, long)>();
        foreach (var src in sources)
        {
            if (File.Exists(src)) { list.Add((src, Path.GetFileName(src), new FileInfo(src).Length)); continue; }
            if (!Directory.Exists(src)) continue;
            string top = Path.GetFileName(src.TrimEnd(Path.DirectorySeparatorChar));
            foreach (var f in Directory.EnumerateFiles(src, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                list.Add((f, (top + "/" + Path.GetRelativePath(src, f)).Replace('\\', '/'), new FileInfo(f).Length));
        }
        return list;
    }

    public static Task<string> CreateAsync(IReadOnlyList<string> sources, string outputPath, ArchiveFormat format, CompressionLevel level,
                                          IProgress<(double Fraction, string Current)>? progress, CancellationToken ct, string? password = null) =>
        Task.Run(() =>
        {
            var files = Expand(sources);
            if (files.Count == 0) throw new IOException("There is nothing to add: the files may be empty folders or no longer exist.");
            long total = Math.Max(1, files.Sum(f => f.Size));
            long done = 0;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                if (format == ArchiveFormat.Zip && !string.IsNullOrEmpty(password))
                {
                    using var writer = new AesZipWriter(new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None), password);
                    foreach (var (full, name, size) in files)
                    {
                        ct.ThrowIfCancellationRequested();
                        progress?.Report((done / (double)total, name));
                        try
                        {
                            using var src = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
                            long before = done;
                            var modified = new FileInfo(full).LastWriteTime;
                            writer.AddFile(name, src, src.Length, modified, level, read => { done = before + read; progress?.Report((done / (double)total, name)); }, ct);
                            done = before + size;
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new IOException($"Couldn't add \"{name}\": {e.Message}"); }
                    }
                    writer.Finish();
                }
                else if (format == ArchiveFormat.Zip)
                {
                    using var fs = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
                    var buf = new byte[1 << 16];
                    foreach (var (full, name, _) in files)
                    {
                        ct.ThrowIfCancellationRequested();
                        progress?.Report((done / (double)total, name));
                        try
                        {
                            using var src = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
                            var entry = zip.CreateEntry(name, level);
                            entry.LastWriteTime = new FileInfo(full).LastWriteTime is var t && t.Year >= 1980 ? t : DateTimeOffset.Now;
                            using var dst = entry.Open();
                            int n;
                            while ((n = src.Read(buf, 0, buf.Length)) > 0) { ct.ThrowIfCancellationRequested(); dst.Write(buf, 0, n); done += n; progress?.Report((done / (double)total, name)); }
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new IOException($"Couldn't read \"{name}\": {e.Message}"); }
                    }
                }
                else
                {
                    using var fs = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    Stream outer = format == ArchiveFormat.TarGz ? new GZipStream(fs, level == CompressionLevel.NoCompression ? CompressionLevel.Fastest : level) : fs;
                    using (outer)
                    using (var tar = new System.Formats.Tar.TarWriter(outer, System.Formats.Tar.TarEntryFormat.Pax, leaveOpen: true))
                    {
                        foreach (var (full, name, size) in files)
                        {
                            ct.ThrowIfCancellationRequested();
                            progress?.Report((done / (double)total, name));
                            tar.WriteEntry(full, name);
                            done += size;
                        }
                    }
                }
                progress?.Report((1, ""));
                return outputPath;
            }
            catch
            {
                try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch (Exception) { }     // never leave a half-written archive
                throw;
            }
        }, ct);
}
