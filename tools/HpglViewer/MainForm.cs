// -----------------------------------------------------------------------------
// HpglViewer - a simple WinForms harness for the Hpgl.Rendering library.
//
// Hpgl.Rendering's plotter-emulation/render technique is derived from the HP7470A
// Plotter Emulator (7470.cpp) by John Miles, KE5FX - http://www.ke5fx.com/
// -----------------------------------------------------------------------------

using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Hpgl.Rendering;

namespace HpglViewer
{
    /// <summary>Displays the rendered HP-GL/2 plot, with open / save / background controls.</summary>
    internal sealed class MainForm : Form
    {
        private readonly PictureBox _picture;
        private readonly ToolStripStatusLabel _status;
        private readonly HpglRenderOptions _options = new HpglRenderOptions { Width = 1280, Height = 960 };
        private string _currentPath;

        public MainForm(string initialPath)
        {
            Text = "HpglViewer";
            Width = 1100;
            Height = 760;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.Black;

            _picture = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,   // scale to fit, preserve aspect
                BackColor = Color.Black
            };

            var status = new StatusStrip();
            _status = new ToolStripStatusLabel("Ready");
            status.Items.Add(_status);

            Controls.Add(_picture);
            Controls.Add(BuildMenu());
            Controls.Add(status);

            Load += (s, e) =>
            {
                if (!string.IsNullOrEmpty(initialPath) && File.Exists(initialPath))
                    RenderFile(initialPath);
                else
                    _status.Text = "No file loaded - use File > Open...";
            };
        }

        private MenuStrip BuildMenu()
        {
            var menu = new MenuStrip();

            var file = new ToolStripMenuItem("&File");
            file.DropDownItems.Add("&Open...", null, (s, e) => OpenFile());
            file.DropDownItems.Add("&Save PNG...", null, (s, e) => SavePng());
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add("E&xit", null, (s, e) => Close());

            var view = new ToolStripMenuItem("&View");
            var black = new ToolStripMenuItem("Black background", null, (s, e) => SetBackground(HpglBackground.Black)) { Checked = true };
            var white = new ToolStripMenuItem("White background", null, (s, e) => SetBackground(HpglBackground.White));
            _blackItem = black; _whiteItem = white;
            view.DropDownItems.Add(black);
            view.DropDownItems.Add(white);
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
            _picture.BackColor = bg == HpglBackground.White ? Color.White : Color.Black;
            if (_currentPath != null) RenderFile(_currentPath);
        }

        private void OpenFile()
        {
            using (var dlg = new OpenFileDialog
            {
                Title = "Open HP-GL/2 file",
                Filter = "HP-GL/2 plots (*.plt;*.hpg;*.hgl;*.pgl)|*.plt;*.hpg;*.hgl;*.pgl|All files (*.*)|*.*"
            })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK) RenderFile(dlg.FileName);
            }
        }

        private void SavePng()
        {
            if (_picture.Image == null) return;
            using (var dlg = new SaveFileDialog { Title = "Save PNG", Filter = "PNG image (*.png)|*.png", FileName = "plot.png" })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    _picture.Image.Save(dlg.FileName, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        private void RenderFile(string path)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                Bitmap bmp = HpglRenderer.RenderToBitmap(
                    System.Text.Encoding.GetEncoding("ISO-8859-1").GetString(bytes), _options);

                var old = _picture.Image;
                _picture.Image = bmp;
                old?.Dispose();

                _currentPath = path;
                Text = "HpglViewer - " + Path.GetFileName(path);
                _status.Text = string.Format("{0}  |  {1} bytes  ->  {2}x{3}",
                    path, bytes.Length, bmp.Width, bmp.Height);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Failed to render:\n\n" + ex.Message, "HpglViewer",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                _status.Text = "Error: " + ex.Message;
            }
        }
    }
}
