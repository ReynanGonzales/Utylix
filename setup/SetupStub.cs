// Utylix-Setup.exe: a tiny starter for the Utylix program folder.
//
// Built with the C# compiler that comes with Windows' own .NET Framework 4.8 (C# 5, see tools\pack-setup.ps1), so it runs on every
// Windows 10 / 11 PC with nothing installed. The Utylix program folder is attached to the end of this exe as a ZIP, followed by its
// length (8 bytes) and the mark "UTYLIXPK". The starter unpacks it into %TEMP%\UtylixSetup\<id>\ and starts the Utylix.exe in it with
// --setup (the real setup wizard), or with the command it was given (an update: --setup-update --dir ...).
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Windows.Forms;

static class SetupStub
{
    const string Mark = "UTYLIXPK";

    [STAThread]
    static int Main(string[] args)
    {
        string self = Application.ExecutablePath;
        long start, length;
        try
        {
            using (var f = new FileStream(self, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (f.Length < 16) throw new InvalidDataException();
                f.Seek(-16, SeekOrigin.End);
                var tail = new byte[16];
                if (f.Read(tail, 0, 16) != 16 || Encoding.ASCII.GetString(tail, 8, 8) != Mark) throw new InvalidDataException();
                length = BitConverter.ToInt64(tail, 0);
                start = f.Length - 16 - length;
                if (length <= 0 || start <= 0) throw new InvalidDataException();
            }
        }
        catch (Exception)
        {
            Fail("This Utylix setup file is incomplete or damaged. Download Utylix-Setup.exe again.");
            return 1;
        }

        // one folder per setup file (its size and date), reused when started again; older ones are cleared away
        string root = Path.Combine(Path.GetTempPath(), "UtylixSetup");
        string id = length.ToString("x") + "-" + File.GetLastWriteTimeUtc(self).Ticks.ToString("x");
        string dir = Path.Combine(root, id);
        CleanOld(root, id);

        if (!File.Exists(Path.Combine(dir, ".complete")))
        {
            string error = null;
            var form = new Unpacking();
            var worker = new Thread(delegate ()
            {
                try { Unpack(self, start, length, dir, form); }
                catch (Exception e) { error = e.Message; }
                form.Finish();
            });
            worker.IsBackground = true;
            form.Shown += delegate { worker.Start(); };
            Application.EnableVisualStyles();
            Application.Run(form);
            if (error != null) { Fail("Utylix setup could not unpack its files:\n" + error + "\n\nIs the disk full? Try again."); return 1; }
        }

        string exe = Path.Combine(dir, "Utylix.exe");
        var command = new StringBuilder();
        bool hasMode = false;
        foreach (string a in args) { if (a == "--setup" || a == "--setup-auto" || a == "--setup-update") hasMode = true; }
        if (!hasMode) command.Append("--setup");
        foreach (string a in args) { if (command.Length > 0) command.Append(' '); command.Append(Quote(a)); }
        try
        {
            var psi = new ProcessStartInfo(exe, command.ToString()) { UseShellExecute = false, WorkingDirectory = dir };
            using (var p = Process.Start(psi))
            {
                p.WaitForExit();
                return p.ExitCode;
            }
        }
        catch (Exception e)
        {
            Fail("Utylix setup could not start:\n" + e.Message);
            return 1;
        }
    }

    static void Unpack(string self, long start, long length, string dir, Unpacking form)
    {
        if (Directory.Exists(dir)) { try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        Directory.CreateDirectory(dir);
        string full = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
        using (var f = new FileStream(self, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var part = new Slice(f, start, length))
        using (var zip = new ZipArchive(part, ZipArchiveMode.Read))
        {
            long total = 0, done = 0;
            foreach (var e in zip.Entries) total += e.Length;
            var buf = new byte[1 << 20];
            foreach (var e in zip.Entries)
            {
                string target = Path.GetFullPath(Path.Combine(dir, e.FullName));
                if (!target.StartsWith(full, StringComparison.OrdinalIgnoreCase)) continue;        // (nothing outside the folder)
                if (e.FullName.EndsWith("/") || e.FullName.EndsWith("\\")) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                using (var src = e.Open())
                using (var dst = new FileStream(target, FileMode.Create, FileAccess.Write))
                {
                    int n;
                    while ((n = src.Read(buf, 0, buf.Length)) > 0)
                    {
                        dst.Write(buf, 0, n);
                        done += n;
                        form.Progress(total == 0 ? 0 : (int)(done * 1000 / total));
                    }
                }
            }
        }
        File.WriteAllText(Path.Combine(dir, ".complete"), "ok");
    }

    static void CleanOld(string root, string keep)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (string d in Directory.GetDirectories(root))
            {
                if (string.Equals(Path.GetFileName(d), keep, StringComparison.OrdinalIgnoreCase)) continue;
                if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(d) < TimeSpan.FromHours(6)) continue;        // (maybe still in use)
                try { Directory.Delete(d, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    static string Quote(string a)
    {
        if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
        return "\"" + a.Replace("\\\"", "\\\\\"").Replace("\"", "\\\"") + (a.EndsWith("\\") ? "\\" : "") + "\"";
    }

    static void Fail(string message)
    {
        MessageBox.Show(message, "Utylix Setup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    /// <summary>A part of a file, read as a stream of its own (the ZIP at the end of this exe).</summary>
    sealed class Slice : Stream
    {
        readonly Stream _s; readonly long _start, _length; long _pos;
        public Slice(Stream s, long start, long length) { _s = s; _start = start; _length = length; }
        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return true; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { return _length; } }
        public override long Position { get { return _pos; } set { _pos = value; } }
        public override int Read(byte[] buffer, int offset, int count)
        {
            long left = _length - _pos;
            if (left <= 0) return 0;
            if (count > left) count = (int)left;
            _s.Seek(_start + _pos, SeekOrigin.Begin);
            int n = _s.Read(buffer, offset, count);
            _pos += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            _pos = origin == SeekOrigin.Begin ? offset : origin == SeekOrigin.Current ? _pos + offset : _length + offset;
            return _pos;
        }
        public override void Flush() { }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
    }

    /// <summary>"Preparing Utylix setup\u2026" with a bar, in Utylix's dark colours (white text on dark: easy to read).</summary>
    sealed class Unpacking : Form
    {
        readonly ProgressBar _bar = new ProgressBar { Minimum = 0, Maximum = 1000, Left = 24, Top = 58, Width = 372, Height = 10, Style = ProgressBarStyle.Continuous };
        public Unpacking()
        {
            Text = "Utylix Setup";
            ClientSize = new Size(420, 96);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(0x12, 0x15, 0x1C);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }
            var label = new Label { Text = "Preparing Utylix setup\u2026", ForeColor = Color.FromArgb(0xE6, 0xE9, 0xF0), Font = new Font("Segoe UI", 10.5f), Left = 22, Top = 22, AutoSize = true };
            Controls.Add(label);
            Controls.Add(_bar);
        }
        public void Progress(int permille)
        {
            if (!IsHandleCreated) return;
            BeginInvoke((MethodInvoker)delegate { _bar.Value = Math.Max(0, Math.Min(1000, permille)); });
        }
        public void Finish()
        {
            if (!IsHandleCreated) return;
            BeginInvoke((MethodInvoker)delegate { Close(); });
        }
    }
}
