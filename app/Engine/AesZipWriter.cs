using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace IdmClone.Engine;

/// <summary>
/// Writes a ZIP whose files are encrypted with AES-256, the "WinZip AE-2" way that 7-Zip, WinRAR, WinZip and Utylix all open.
/// (.NET's own ZIP support cannot encrypt.) File names are not hidden - that is a limit of the ZIP format.
/// Each file: [16 byte salt][2 byte password check][AES-CTR encrypted deflated data][10 byte authentication code].
/// </summary>
public sealed class AesZipWriter : IDisposable
{
    private sealed record Central(string Name, ushort Method, ushort Time, ushort Date, uint CompressedSize, uint Size, uint Offset);

    private readonly FileStream _out;
    private readonly byte[] _password;
    private readonly List<Central> _entries = new();

    public AesZipWriter(FileStream output, string password)
    {
        _out = output;
        _password = Encoding.UTF8.GetBytes(password);
    }

    private static (ushort Time, ushort Date) DosTime(DateTime t)
    {
        if (t.Year < 1980) t = new DateTime(1980, 1, 1);
        return ((ushort)((t.Hour << 11) | (t.Minute << 5) | (t.Second / 2)), (ushort)(((t.Year - 1980) << 9) | (t.Month << 5) | t.Day));
    }

    /// <summary>Adds one file. <paramref name="onBytes"/> is told how many bytes of the original have been read.</summary>
    public void AddFile(string entryName, Stream source, long length, DateTime modified, CompressionLevel level, Action<long> onBytes, CancellationToken ct)
    {
        if (length >= 0xFFFFFFFFL) throw new IOException($"\"{entryName}\" is over 4 GB: too big for a password-protected ZIP.");
        if (_entries.Count >= 65534) throw new IOException("Too many files for one ZIP.");

        byte[] nameBytes = Encoding.UTF8.GetBytes(entryName);
        bool store = level == CompressionLevel.NoCompression || length == 0;      // an empty file has nothing to deflate (.NET would write no data at all)
        ushort actualMethod = store ? (ushort)0 : (ushort)8;
        var (time, date) = DosTime(modified);
        long headerPos = _out.Position;

        // ---- local header (sizes are patched in afterwards) ----
        using (var bw = new BinaryWriter(_out, Encoding.UTF8, leaveOpen: true))
        {
            bw.Write(0x04034b50u);
            bw.Write((ushort)51);                 // version needed (5.1: AES)
            bw.Write((ushort)0x0801);             // encrypted + UTF-8 names
            bw.Write((ushort)99);                 // "AES" method; the real one is in the extra field
            bw.Write(time); bw.Write(date);
            bw.Write(0u);                         // CRC: 0 for AE-2 (the authentication code protects the data)
            bw.Write(0u); bw.Write(0u);           // compressed / original size: patched below
            bw.Write((ushort)nameBytes.Length);
            bw.Write((ushort)11);                 // extra field length
            bw.Write(nameBytes);
            WriteAesExtra(bw, actualMethod);
        }

        // ---- key material: PBKDF2-HMAC-SHA1(password, salt, 1000) -> AES key | HMAC key | 2 byte password check ----
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] derived = Rfc2898DeriveBytes.Pbkdf2(_password, salt, 1000, HashAlgorithmName.SHA1, 66);
        byte[] aesKey = derived[..32], hmacKey = derived[32..64];
        _out.Write(salt);
        _out.Write(derived, 64, 2);

        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA1, hmacKey);
        using var aes = Aes.Create();
        aes.Key = aesKey;
        long encryptedLength = 0;
        using (var encrypt = new CtrStream(_out, aes, hmac, n => encryptedLength += n))
        {
            Stream target = encrypt;
            DeflateStream? deflate = store ? null : new DeflateStream(encrypt, level, leaveOpen: true);
            try
            {
                if (deflate != null) target = deflate;
                var buf = new byte[1 << 16];
                long read = 0;
                int n;
                while ((n = source.Read(buf, 0, buf.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    target.Write(buf, 0, n);
                    read += n;
                    onBytes(read);
                }
            }
            finally { deflate?.Dispose(); }       // writes the end of the compressed data into the encryptor
            encrypt.Flush();
        }
        _out.Write(hmac.GetHashAndReset(), 0, 10);

        long compressed = 16 + 2 + encryptedLength + 10;
        if (compressed >= 0xFFFFFFFFL) throw new IOException($"\"{entryName}\" is too big for a password-protected ZIP.");

        // ---- go back and fill in the sizes ----
        long end = _out.Position;
        _out.Position = headerPos + 18;
        using (var bw = new BinaryWriter(_out, Encoding.UTF8, leaveOpen: true)) { bw.Write((uint)compressed); bw.Write((uint)length); }
        _out.Position = end;
        _entries.Add(new Central(entryName, actualMethod, time, date, (uint)compressed, (uint)length, (uint)headerPos));
    }

    private static void WriteAesExtra(BinaryWriter bw, ushort actualMethod)
    {
        bw.Write((ushort)0x9901);                 // AES extra field
        bw.Write((ushort)7);
        bw.Write((ushort)2);                      // AE-2
        bw.Write((byte)'A'); bw.Write((byte)'E'); // vendor
        bw.Write((byte)3);                        // AES-256
        bw.Write(actualMethod);
    }

    /// <summary>Writes the directory of files at the end and finishes the ZIP.</summary>
    public void Finish()
    {
        long cdStart = _out.Position;
        using var bw = new BinaryWriter(_out, Encoding.UTF8, leaveOpen: true);
        foreach (var e in _entries)
        {
            byte[] name = Encoding.UTF8.GetBytes(e.Name);
            bw.Write(0x02014b50u);
            bw.Write((ushort)0x0033);             // made by: version 5.1
            bw.Write((ushort)51);
            bw.Write((ushort)0x0801);
            bw.Write((ushort)99);
            bw.Write(e.Time); bw.Write(e.Date);
            bw.Write(0u);
            bw.Write(e.CompressedSize); bw.Write(e.Size);
            bw.Write((ushort)name.Length);
            bw.Write((ushort)11);
            bw.Write((ushort)0);                  // comment
            bw.Write((ushort)0); bw.Write((ushort)0);   // disk, internal attributes
            bw.Write(0x20u);                      // external attributes: "archive" file
            bw.Write(e.Offset);
            bw.Write(name);
            WriteAesExtra(bw, e.Method);
        }
        long cdSize = _out.Position - cdStart;
        bw.Write(0x06054b50u);
        bw.Write((ushort)0); bw.Write((ushort)0);
        bw.Write((ushort)_entries.Count); bw.Write((ushort)_entries.Count);
        bw.Write((uint)cdSize); bw.Write((uint)cdStart);
        bw.Write((ushort)0);
        bw.Flush();
    }

    public void Dispose() => _out.Dispose();

    /// <summary>A write-only stream that encrypts what is written with AES in counter mode (WinZip style: 128-bit little-endian counter from 1) and feeds the result to an HMAC.</summary>
    private sealed class CtrStream : Stream
    {
        private const int Blocks = 4096;                                  // 64 KB of key stream at a time
        private readonly Stream _inner;
        private readonly Aes _aes;
        private readonly IncrementalHash _hmac;
        private readonly Action<int> _counted;
        private readonly byte[] _counters = new byte[Blocks * 16];
        private readonly byte[] _keystream = new byte[Blocks * 16];
        private readonly byte[] _scratch = new byte[Blocks * 16];
        private int _used = Blocks * 16;                                  // start with an empty key stream
        private ulong _counter = 1;

        public CtrStream(Stream inner, Aes aes, IncrementalHash hmac, Action<int> counted)
        {
            _inner = inner; _aes = aes; _hmac = hmac; _counted = counted;
        }

        private void Refill()
        {
            for (int i = 0; i < Blocks; i++)
            {
                BitConverter.TryWriteBytes(_counters.AsSpan(i * 16, 8), _counter++);     // little-endian counter in the low 8 bytes, the rest stays 0
            }
            _aes.EncryptEcb(_counters, _keystream, PaddingMode.None);
            _used = 0;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                if (_used >= _keystream.Length) Refill();
                int n = Math.Min(count, _keystream.Length - _used);
                for (int i = 0; i < n; i++) _scratch[i] = (byte)(buffer[offset + i] ^ _keystream[_used + i]);
                _used += n; offset += n; count -= n;
                _hmac.AppendData(_scratch, 0, n);
                _inner.Write(_scratch, 0, n);
                _counted(n);
            }
        }

        public override void Flush() { }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
