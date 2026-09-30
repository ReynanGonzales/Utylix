using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using System.Threading.Tasks;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>One file (or folder) that is dragged out of an archive.</summary>
/// <param name="Name">Where it goes inside the folder it is dropped on ("readme.txt", "data\numbers.csv").</param>
/// <param name="Key">Its name inside the archive ("proj/data/numbers.csv").</param>
public sealed record DragEntry(string Name, string Key, bool IsDirectory, long Size, DateTime? Modified);

/// <summary>
/// Dragging files out of the archive window onto an Explorer folder (or the desktop): the "virtual files" way Windows itself does it.
/// Nothing is unpacked while you drag. When the files are dropped, Explorer asks for their contents, and only then are they
/// extracted (once, into a temporary folder, in a single pass) and handed over.
/// </summary>
public sealed class ArchiveDragData : System.Runtime.InteropServices.ComTypes.IDataObject
{
    // ---------- Windows' constants ----------
    private const int DV_E_FORMATETC = unchecked((int)0x80040064), OLE_E_ADVISENOTSUPPORTED = unchecked((int)0x80040003), E_NOTIMPL = unchecked((int)0x80004001);
    private const int DATA_S_SAMEFORMATETC = 0x00040130;
    private const int DVASPECT_CONTENT = 1;
    private const uint FD_ATTRIBUTES = 0x04, FD_FILESIZE = 0x40, FD_WRITESTIME = 0x20, FD_PROGRESSUI = 0x4000;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10, FILE_ATTRIBUTE_NORMAL = 0x80;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("shell32.dll")] private static extern int SHCreateStdEnumFmtEtc(uint count, FORMATETC[] formats, out IEnumFORMATETC enumerator);
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FILEDESCRIPTORW
    {
        public uint dwFlags;
        public Guid clsid;
        public int sizelCx, sizelCy, pointlX, pointlY;
        public uint dwFileAttributes;
        public uint ftCreationLow, ftCreationHigh, ftAccessLow, ftAccessHigh, ftWriteLow, ftWriteHigh;
        public uint nFileSizeHigh, nFileSizeLow;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
    }

    private static readonly short CfDescriptor = (short)RegisterClipboardFormat("FileGroupDescriptorW");
    private static readonly short CfContents = (short)RegisterClipboardFormat("FileContents");
    private static readonly short CfPreferredEffect = (short)RegisterClipboardFormat("Preferred DropEffect");

    private readonly IReadOnlyList<DragEntry> _entries;
    private readonly string _archive;
    private readonly IReadOnlyCollection<string> _only;
    private readonly string? _password;
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "Utylix-drag", Guid.NewGuid().ToString("N")[..10]);
    private bool _extracted;

    /// <summary>Set if unpacking at the moment of the drop went wrong (the window shows it afterwards).</summary>
    public Exception? Failure { get; private set; }
    public int FileCount => _entries.Count(e => !e.IsDirectory);

    public ArchiveDragData(IReadOnlyList<DragEntry> entries, string archive, IReadOnlyCollection<string> only, string? password)
    {
        _entries = entries; _archive = archive; _only = only; _password = password;
    }

    /// <summary>The password that worked (or null), so the window can remember it.</summary>
    public string? Password => _password;

    /// <summary>Remove the temporary folder (later: Explorer may still be copying out of it).</summary>
    public void CleanUpLater()
    {
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 20; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(30));
                try { if (Directory.Exists(_temp)) Directory.Delete(_temp, true); return; }
                catch (Exception) { /* still in use: try again a little later */ }
            }
        });
    }

    // ---------- what Explorer asks for ----------
    private FORMATETC[] Formats() => new[]
    {
        new FORMATETC { cfFormat = CfDescriptor, dwAspect = (DVASPECT)DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL },
        new FORMATETC { cfFormat = CfContents, dwAspect = (DVASPECT)DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_ISTREAM },
        new FORMATETC { cfFormat = CfPreferredEffect, dwAspect = (DVASPECT)DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL },
    };

    public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
    {
        if (direction != DATADIR.DATADIR_GET) Marshal.ThrowExceptionForHR(E_NOTIMPL);
        var formats = Formats();
        Marshal.ThrowExceptionForHR(SHCreateStdEnumFmtEtc((uint)formats.Length, formats, out var e));
        return e;
    }

    public int QueryGetData(ref FORMATETC format)
    {
        if (format.cfFormat == CfDescriptor && (format.tymed & TYMED.TYMED_HGLOBAL) != 0) return 0;
        if (format.cfFormat == CfContents && (format.tymed & TYMED.TYMED_ISTREAM) != 0) return 0;
        if (format.cfFormat == CfPreferredEffect && (format.tymed & TYMED.TYMED_HGLOBAL) != 0) return 0;
        return DV_E_FORMATETC;
    }

    public void GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        medium = default;
        if (QueryGetData(ref format) != 0) Marshal.ThrowExceptionForHR(DV_E_FORMATETC);

        if (format.cfFormat == CfPreferredEffect)
        {
            IntPtr mem = Marshal.AllocHGlobal(4);
            Marshal.WriteInt32(mem, 1);                                       // DROPEFFECT_COPY
            medium = new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = mem, pUnkForRelease = null };
            return;
        }
        if (format.cfFormat == CfDescriptor)
        {
            medium = new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = BuildDescriptors(), pUnkForRelease = null };
            return;
        }

        // FileContents: the number of the file is in "lindex"
        int index = format.lindex;
        if (index < 0 || index >= _entries.Count || _entries[index].IsDirectory) Marshal.ThrowExceptionForHR(DV_E_FORMATETC);
        string path;
        try { path = Extracted(_entries[index]); }
        catch (Exception e)
        {
            Failure ??= e;
            throw new COMException("Couldn't extract the file: " + e.Message, unchecked((int)0x80004005));
        }
        var stream = new TempFileStream(path);
        medium = new STGMEDIUM { tymed = TYMED.TYMED_ISTREAM, unionmember = Marshal.GetComInterfaceForObject<TempFileStream, IStream>(stream), pUnkForRelease = null };
    }

    /// <summary>Everything that was dragged is unpacked in one pass, the first time Explorer asks for a file's contents.</summary>
    private string Extracted(DragEntry entry)
    {
        if (!_extracted)
        {
            ArchiveService.ExtractAsync(_archive, _temp, _only, _password, OverwriteMode.Overwrite, null, CancellationToken.None).GetAwaiter().GetResult();
            _extracted = true;
        }
        return ArchiveService.SafeTarget(_temp, entry.Key) is { } p && File.Exists(p) ? p : throw new FileNotFoundException("That file is not in the archive.", entry.Name);
    }

    private IntPtr BuildDescriptors()
    {
        int size = Marshal.SizeOf<FILEDESCRIPTORW>();
        IntPtr mem = Marshal.AllocHGlobal(4 + size * _entries.Count);
        Marshal.WriteInt32(mem, _entries.Count);
        for (int i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            var time = e.Modified is { } m && m.Year >= 1980 ? m.ToFileTimeUtc() : DateTime.Now.ToFileTimeUtc();
            var d = new FILEDESCRIPTORW
            {
                dwFlags = FD_ATTRIBUTES | FD_FILESIZE | FD_WRITESTIME | FD_PROGRESSUI,
                dwFileAttributes = e.IsDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL,
                ftWriteLow = (uint)(time & 0xFFFFFFFF), ftWriteHigh = (uint)(time >> 32),
                nFileSizeHigh = (uint)((e.IsDirectory ? 0 : Math.Max(0, e.Size)) >> 32), nFileSizeLow = (uint)((e.IsDirectory ? 0 : Math.Max(0, e.Size)) & 0xFFFFFFFF),
                cFileName = e.Name.Length > 259 ? e.Name[^259..] : e.Name,
            };
            Marshal.StructureToPtr(d, mem + 4 + i * size, false);
        }
        return mem;
    }

    public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => Marshal.ThrowExceptionForHR(E_NOTIMPL);
    public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut) { formatOut = formatIn; formatOut.ptd = IntPtr.Zero; return DATA_S_SAMEFORMATETC; }
    public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release) { if (release) ReleaseStgMedium(ref medium); }   // (Explorer reports how the drop went)
    public int DAdvise(ref FORMATETC pFormatetc, ADVF advf, IAdviseSink adviseSink, out int connection) { connection = 0; return OLE_E_ADVISENOTSUPPORTED; }
    public void DUnadvise(int connection) => Marshal.ThrowExceptionForHR(OLE_E_ADVISENOTSUPPORTED);
    public int EnumDAdvise(out IEnumSTATDATA enumAdvise) { enumAdvise = null!; return OLE_E_ADVISENOTSUPPORTED; }

    // ---------- the stream Explorer reads a file from ----------
    private sealed class TempFileStream : IStream
    {
        private readonly FileStream _file;
        public TempFileStream(string path) => _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);

        public void Read(byte[] pv, int cb, IntPtr pcbRead)
        {
            int n = _file.Read(pv, 0, cb);
            if (pcbRead != IntPtr.Zero) Marshal.WriteInt32(pcbRead, n);
        }

        public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
        {
            long p = _file.Seek(dlibMove, (SeekOrigin)dwOrigin);
            if (plibNewPosition != IntPtr.Zero) Marshal.WriteInt64(plibNewPosition, p);
        }

        public void Stat(out STATSTG pstatstg, int grfStatFlag)
        {
            pstatstg = new STATSTG { type = 2, cbSize = _file.Length, grfMode = 0 };
        }

        public void Write(byte[] pv, int cb, IntPtr pcbWritten) => throw new COMException("", E_NOTIMPL);
        public void SetSize(long libNewSize) => throw new COMException("", E_NOTIMPL);
        public void CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten) => throw new COMException("", E_NOTIMPL);
        public void Commit(int grfCommitFlags) { }
        public void Revert() { }
        public void LockRegion(long libOffset, long cb, int dwLockType) => throw new COMException("", E_NOTIMPL);
        public void UnlockRegion(long libOffset, long cb, int dwLockType) => throw new COMException("", E_NOTIMPL);
        public void Clone(out IStream ppstm) => throw new COMException("", E_NOTIMPL);
        ~TempFileStream() { _file.Dispose(); }
    }

    // ---------- starting the drag ----------
    [ComImport, Guid("00000121-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDropSource
    {
        [PreserveSig] int QueryContinueDrag([MarshalAs(UnmanagedType.Bool)] bool escapePressed, int keyState);
        [PreserveSig] int GiveFeedback(int effect);
    }

    private sealed class DropSource : IDropSource
    {
        public int QueryContinueDrag(bool escapePressed, int keyState)
        {
            if (escapePressed) return 0x00040101;                              // DRAGDROP_S_CANCEL
            if ((keyState & 1) == 0) return 0x00040100;                        // the mouse button was let go: DRAGDROP_S_DROP
            return 0;
        }

        public int GiveFeedback(int effect) => 0x00040102;                     // DRAGDROP_S_USEDEFAULTCURSORS
    }

    [DllImport("ole32.dll")]
    private static extern int DoDragDrop([MarshalAs(UnmanagedType.Interface)] System.Runtime.InteropServices.ComTypes.IDataObject dataObject,
                                         [MarshalAs(UnmanagedType.Interface)] IDropSource dropSource, int allowedEffects, out int effect);

    /// <summary>What Windows answered when the drag ended (0x00040100 = dropped, 0x00040101 = cancelled).</summary>
    public int Result { get; private set; }
    public int Effect { get; private set; }

    /// <summary>Runs the drag (returns when it is dropped or cancelled). true when the files were dropped somewhere.</summary>
    public bool Drag()
    {
        Result = DoDragDrop(this, new DropSource(), 1, out int effect);       // 1 = copy
        Effect = effect;
        return Result == 0x00040100 && effect != 0;
    }
}
