// -----------------------------------------------------------------------------
// HpglViewer - a side-by-side comparison harness for the Hpgl.Rendering library.
//
// Left pane  : our render (Hpgl.Rendering).
// Right pane : an independent reference render. If hp2xx.exe is available it is run
//              live on the same file; otherwise a reference image can be opened by hand.
//
// hp2xx (GNU) is located via the HP2XX_EXE environment variable, then the PATH, then
// %USERPROFILE%\source\hpgl-compare\hp2xx\bin\hp2xx.exe. Note hp2xx needs its GnuWin32
// dependency DLLs (libpng13/zlib1/jpeg62/libtiff3/pdflib5) beside the exe to run.
//
// Hpgl.Rendering's plotter-emulation/render technique is derived from the HP7470A
// Plotter Emulator (7470.cpp) by John Miles, KE5FX - http://www.ke5fx.com/
// -----------------------------------------------------------------------------

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Hpgl.Rendering;

namespace HpglViewer
{
    /// <summary>Renders an HP-GL/2 file with our engine and hp2xx side by side for comparison.</summary>
    internal sealed class MainForm : Form
    {
        private readonly PictureBox _ours;
        private readonly PictureBox _reference;
        private readonly Label _oursTitle;
        private readonly Label _refTitle;
        private readonly ToolStripStatusLabel _status;
        private readonly HpglRenderOptions _options =
            new HpglRenderOptions { Width = 1280, Height = 960, Background = HpglBackground.White };
        private string _currentPath;
        private readonly string _hp2xxExe;

        public MainForm(string initialPath)
        {
            Text = "HpglViewer - ours vs hp2xx";
            Width = 1500;
            Height = 820;
            StartPosition = FormStartPosition.CenterScreen;

            _hp2xxExe = LocateHp2xx();

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,   // two panes side by side
                BackColor = Color.DimGray
            };

            split.Panel1.Controls.Add(BuildPane(out _ours, out _oursTitle, "Hpgl.Rendering (ours)"));
            split.Panel2.Controls.Add(BuildPane(out _reference, out _refTitle,
                _hp2xxExe != null ? "hp2xx (reference)" : "hp2xx (not found - File > Open reference image…)"));

            var status = new StatusStrip();
            _status = new ToolStripStatusLabel("Ready");
            status.Items.Add(_status);

            Controls.Add(split);
            Controls.Add(BuildMenu());
            Controls.Add(status);

            Load += (s, e) =>
            {
                split.SplitterDistance = split.Width / 2;
                ApplyBackgroundColors();
                if (!string.IsNullOrEmpty(initialPath) && File.Exists(initialPath))
                    RenderFile(initialPath);
                else
                    _status.Text = _hp2xxExe != null
                        ? "Ready (hp2xx: " + _hp2xxExe + ") - File > Open HP-GL…"
                        : "Ready (hp2xx not found) - File > Open HP-GL…";
            };
        }

        // ---- layout -----------------------------------------------------------

        private static Control BuildPane(out PictureBox picture, out Label title, string text)
        {
            var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            title = new Label
            {
                Text = text,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.White,
                BackColor = Color.FromArgb(48, 48, 48)
            };
            picture = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom };

            table.Controls.Add(title, 0, 0);
            table.Controls.Add(picture, 0, 1);
            return table;
        }

        private MenuStrip BuildMenu()
        {
            var menu = new MenuStrip();

            var file = new ToolStripMenuItem("&File");
            file.DropDownItems.Add("&Open HP-GL…", null, (s, e) => OpenFile());
            file.DropDownItems.Add("Open &reference image (right pane)…", null, (s, e) => OpenReference());
            file.DropDownItems.Add("&Save comparison PNG…", null, (s, e) => SaveComparison());
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add("E&xit", null, (s, e) => Close());

            var view = new ToolStripMenuItem("&View");
            _blackItem = new ToolStripMenuItem("Black background", null, (s, e) => SetBackground(HpglBackground.Black));
            _whiteItem = new ToolStripMenuItem("White background (matches hp2xx)", null, (s, e) => SetBackground(HpglBackground.White)) { Checked = true };
            view.DropDownItems.Add(_blackItem);
            view.DropDownItems.Add(_whiteItem);
            view.DropDownItems.Add(new ToolStripSeparator());
            view.DropDownItems.Add("&Reload", null, (s, e) => { if (_currentPath != null) RenderFile(_currentPath); });

            menu.Items.Add(file);
            menu.Items.Add(view);
            return menu;
        }

        private ToolStripMenuItem _blackItem;
        private ToolStripMenuItem _whiteItem;

        private void SetBackground(HpglBackground bg)
        {
            _options.Background = bg;
            _blackItem.Checked = bg == HpglBackground.Black;
            _whiteItem.Checked = bg == HpglBackground.White;
            ApplyBackgroundColors();
            if (_currentPath != null) RenderFile(_currentPath);
        }

        private void ApplyBackgroundColors()
        {
            Color c = _options.Background == HpglBackground.White ? Color.White : Color.Black;
            _ours.BackColor = c;
            _reference.BackColor = c;
        }

        // ---- actions ----------------------------------------------------------

        private void OpenFile()
        {
            using (var dlg = new OpenFileDialog
            {
                Title = "Open HP-GL/2 file",
                Filter = "HP-GL/2 plots (*.plt;*.hpg;*.hgl;*.pgl;*.hp)|*.plt;*.hpg;*.hgl;*.pgl;*.hp|All files (*.*)|*.*"
            })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK) RenderFile(dlg.FileName);
            }
        }

        private void OpenReference()
        {
            using (var dlg = new OpenFileDialog { Title = "Open reference image (right pane)", Filter = "Images (*.png;*.bmp;*.gif;*.jpg)|*.png;*.bmp;*.gif;*.jpg|All files (*.*)|*.*" })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    SetImage(_reference, LoadImageUnlocked(dlg.FileName));
                    _refTitle.Text = "reference: " + Path.GetFileName(dlg.FileName);
                }
            }
        }

        private void RenderFile(string path)
        {
            // Left: our renderer.
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                var bmp = HpglRenderer.RenderToBitmap(
                    Encoding.GetEncoding("ISO-8859-1").GetString(bytes), _options);
                SetImage(_ours, bmp);
                _currentPath = path;
                Text = "HpglViewer - " + Path.GetFileName(path);
                _status.Text = string.Format("{0}  |  {1} bytes  ->  ours {2}x{3}", path, bytes.Length, bmp.Width, bmp.Height);
            }
            catch (Exception ex)
            {
                SetImage(_ours, null);
                _status.Text = "Our render failed: " + ex.Message;
            }

            // Right: hp2xx, if available.
            RenderReferenceWithHp2xx(path);
        }

        private void RenderReferenceWithHp2xx(string path)
        {
            if (_hp2xxExe == null)
            {
                _refTitle.Text = "hp2xx not found - set HP2XX_EXE or use File > Open reference image…";
                return;
            }
            try
            {
                string outPng = Path.Combine(Path.GetTempPath(), "hp2xx_ref.png");
                if (File.Exists(outPng)) File.Delete(outPng);

                // PNG, distinct pen colours, 150 DPI, 200x150 mm frame.
                string args = string.Format("-m png -c12345670 -d 150 -w 200 -h 150 -f \"{0}\" \"{1}\"", outPng, path);
                var psi = new ProcessStartInfo(_hp2xxExe, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    WorkingDirectory = Path.GetDirectoryName(_hp2xxExe)
                };
                string err;
                using (var p = Process.Start(psi))
                {
                    err = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(20000)) { try { p.Kill(); } catch { } }
                }

                if (File.Exists(outPng) && new FileInfo(outPng).Length > 0)
                {
                    SetImage(_reference, LoadImageUnlocked(outPng));
                    _refTitle.Text = "hp2xx (reference)";
                }
                else
                {
                    SetImage(_reference, null);
                    _refTitle.Text = "hp2xx produced no output";
                    _status.Text += "  |  hp2xx: " + (string.IsNullOrWhiteSpace(err) ? "no output" : err.Trim());
                }
            }
            catch (Exception ex)
            {
                SetImage(_reference, null);
                _refTitle.Text = "hp2xx failed: " + ex.Message;
            }
        }

        private void SaveComparison()
        {
            if (_ours.Image == null && _reference.Image == null) return;
            using (var dlg = new SaveFileDialog { Title = "Save comparison PNG", Filter = "PNG image (*.png)|*.png", FileName = "comparison.png" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                int w = ((_ours.Image?.Width ?? 0) + (_reference.Image?.Width ?? 0));
                int h = Math.Max(_ours.Image?.Height ?? 1, _reference.Image?.Height ?? 1);
                if (w == 0) return;
                using (var combined = new Bitmap(Math.Max(1, w), Math.Max(1, h)))
                using (var g = Graphics.FromImage(combined))
                {
                    g.Clear(Color.DimGray);
                    int x = 0;
                    if (_ours.Image != null) { g.DrawImage(_ours.Image, 0, 0); x = _ours.Image.Width; }
                    if (_reference.Image != null) g.DrawImage(_reference.Image, x, 0);
                    combined.Save(dlg.FileName, System.Drawing.Imaging.ImageFormat.Png);
                }
            }
        }

        // ---- helpers ----------------------------------------------------------

        private static void SetImage(PictureBox box, Image img)
        {
            var old = box.Image;
            box.Image = img;
            old?.Dispose();
        }

        /// <summary>Loads an image without keeping the file locked (so it can be overwritten next run).</summary>
        private static Image LoadImageUnlocked(string path)
        {
            using (var ms = new MemoryStream(File.ReadAllBytes(path)))
                return Image.FromStream(ms);
        }

        private static string LocateHp2xx()
        {
            string env = Environment.GetEnvironmentVariable("HP2XX_EXE");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;

            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                try { string c = Path.Combine(dir.Trim(), "hp2xx.exe"); if (File.Exists(c)) return c; }
                catch { }
            }

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string[] guesses =
            {
                @"C:\cygwin64\bin\hp2xx.exe",     // Cygwin build (reliable on modern Windows)
                @"C:\cygwin\bin\hp2xx.exe",
                @"C:\msys64\usr\bin\hp2xx.exe",
                Path.Combine(home, "source", "hpgl-compare", "hp2xx", "bin", "hp2xx.exe"),
            };
            foreach (var g in guesses)
                if (File.Exists(g)) return g;
            return null;
        }
    }
}
