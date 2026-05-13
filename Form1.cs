using NAudio.Wave;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace WavPlayer
{
    public class Form1 : Form
    {
        // ── Audio engine ──────────────────────────────────────────────
        private AudioFileReader? _reader;
        private WaveOutEvent?   _waveOut;
        private System.Windows.Forms.Timer _timer = new() { Interval = 200 };
        private bool   _dragging   = false;
        private float  _volume     = 0.8f;
        private float[]? _waveData = null;   // downsampled waveform

        // ── Colour palette ────────────────────────────────────────────
        private static readonly Color BgDeep   = Color.FromArgb(15, 15, 22);
        private static readonly Color BgPanel  = Color.FromArgb(24, 24, 34);
        private static readonly Color BgCard   = Color.FromArgb(32, 32, 46);
        private static readonly Color Accent   = Color.FromArgb(82, 160, 255);
        private static readonly Color AccentG  = Color.FromArgb(60, 210, 150);
        private static readonly Color AccentR  = Color.FromArgb(230, 80, 80);
        private static readonly Color TextMain = Color.FromArgb(230, 230, 240);
        private static readonly Color TextSub  = Color.FromArgb(130, 130, 155);

        // ── Controls ──────────────────────────────────────────────────
        private PictureBox  _waveformBox  = null!;
        private Label       _lblFileName  = null!;
        private Label       _lblInfo      = null!;
        private Label       _lblCurrent   = null!;
        private Label       _lblTotal     = null!;
        private TrackBar    _seekBar      = null!;
        private Button      _btnOpen      = null!;
        private Button      _btnPlay      = null!;
        private Button      _btnStop      = null!;
        private TrackBar    _volBar       = null!;
        private Label       _lblVolVal    = null!;
        private Panel       _dropHint     = null!;

        // ─────────────────────────────────────────────────────────────
        public Form1()
        {
            BuildUI();
            _timer.Tick += OnTimerTick;

            this.AllowDrop  = true;
            this.DragEnter += (_, e) =>
            {
                if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
                    e.Effect = DragDropEffects.Copy;
            };
            this.DragDrop += (_, e) =>
            {
                var files = (string[]?)e.Data?.GetData(DataFormats.FileDrop);
                if (files?.Length > 0) LoadFile(files[0]);
            };
        }

        // ═══════════════════════════════════════════════════════════════
        //  UI CONSTRUCTION
        // ═══════════════════════════════════════════════════════════════
        private void BuildUI()
        {
            SuspendLayout();

            Text            = "WAV 音效檔播放器";
            Size            = new Size(720, 540);
            MinimumSize     = new Size(640, 480);
            BackColor       = BgDeep;
            ForeColor       = TextMain;
            Font            = new Font("Segoe UI", 9f);
            StartPosition   = FormStartPosition.CenterScreen;
            DoubleBuffered  = true;

            // ── Top bar ──────────────────────────────────────────────
            var header = MakePanel(DockStyle.Top, 56, BgPanel);
            var logo = MakeLabel("🎵  WAV Player", new Font("Segoe UI", 15f, FontStyle.Bold), Accent);
            logo.Location  = new Point(18, 14);
            logo.AutoSize  = true;

            var verLabel = MakeLabel("v1.0", new Font("Segoe UI", 8f), TextSub);
            verLabel.Anchor   = AnchorStyles.Top | AnchorStyles.Right;
            verLabel.AutoSize = true;
            header.Controls.AddRange(new Control[] { logo, verLabel });
            header.Resize += (_, _) => verLabel.Location = new Point(header.Width - 50, 22);
            verLabel.Location = new Point(660, 22);

            // ── Waveform ─────────────────────────────────────────────
            var wfPanel = MakePanel(DockStyle.Top, 130, BgDeep);
            wfPanel.Padding = new Padding(12, 10, 12, 10);

            _waveformBox = new PictureBox
            {
                Dock      = DockStyle.Fill,
                BackColor = BgDeep
            };
            _waveformBox.Paint += OnWaveformPaint;
            wfPanel.Controls.Add(_waveformBox);

            // drop hint overlay
            _dropHint = new Panel
            {
                Dock      = DockStyle.Fill,
                BackColor = Color.Transparent
            };
            _dropHint.Paint += (_, e) =>
            {
                if (_reader != null) return;
                var r = _dropHint.ClientRectangle;
                using var pen = new Pen(Color.FromArgb(60, 100, 180), 2f) { DashStyle = DashStyle.Dash };
                e.Graphics.DrawRectangle(pen, new Rectangle(r.X+4, r.Y+4, r.Width-9, r.Height-9));
                var msg = "拖曳 WAV 檔案至此，或點擊「開啟」按鈕";
                using var fnt = new Font("Segoe UI", 10f);
                var sz  = e.Graphics.MeasureString(msg, fnt);
                e.Graphics.DrawString(msg, fnt, new SolidBrush(Color.FromArgb(90, Accent)),
                    (r.Width  - sz.Width)  / 2f,
                    (r.Height - sz.Height) / 2f);
            };
            _dropHint.AllowDrop = true;
            _dropHint.DragEnter += (_, e) =>
            {
                if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
                    e.Effect = DragDropEffects.Copy;
            };
            _dropHint.DragDrop += (_, e) =>
            {
                var files = (string[]?)e.Data?.GetData(DataFormats.FileDrop);
                if (files?.Length > 0) LoadFile(files[0]);
            };
            _waveformBox.Controls.Add(_dropHint);

            // ── File info ────────────────────────────────────────────
            var infoPanel = MakePanel(DockStyle.Top, 52, BgCard);
            infoPanel.Padding = new Padding(16, 6, 16, 6);

            _lblFileName = MakeLabel("未載入檔案", new Font("Segoe UI", 10f, FontStyle.Bold), TextMain);
            _lblFileName.Dock = DockStyle.Top;

            _lblInfo = MakeLabel("請開啟 WAV 檔案或拖曳至視窗", new Font("Segoe UI", 8.5f), TextSub);
            _lblInfo.Dock = DockStyle.Top;

            infoPanel.Controls.Add(_lblInfo);
            infoPanel.Controls.Add(_lblFileName);

            // ── Seek bar ─────────────────────────────────────────────
            var seekPanel = MakePanel(DockStyle.Top, 42, BgDeep);

            _lblCurrent = MakeLabel("00:00", new Font("Consolas", 9f), TextSub);
            _lblCurrent.Location = new Point(14, 12);
            _lblCurrent.AutoSize = true;

            _lblTotal   = MakeLabel("00:00", new Font("Consolas", 9f), TextSub);
            _lblTotal.AutoSize = true;
            _lblTotal.Anchor   = AnchorStyles.Top | AnchorStyles.Right;

            _seekBar = new TrackBar
            {
                Minimum        = 0,
                Maximum        = 1000,
                Value          = 0,
                TickStyle      = TickStyle.None,
                BackColor      = BgDeep,
                Anchor         = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Location       = new Point(58, 8),
                Height         = 28,
                Enabled        = false
            };
            _seekBar.MouseDown += (_, _) => _dragging = true;
            _seekBar.MouseUp   += (_, _) =>
            {
                _dragging = false;
                if (_reader == null) return;
                _reader.CurrentTime = TimeSpan.FromSeconds(
                    _reader.TotalTime.TotalSeconds * _seekBar.Value / 1000.0);
            };

            seekPanel.Controls.AddRange(new Control[] { _lblCurrent, _seekBar, _lblTotal });
            seekPanel.Resize += (_, _) =>
            {
                _seekBar.Width      = seekPanel.Width - 130;
                _lblTotal.Location  = new Point(seekPanel.Width - 58, 12);
            };
            // initial positions
            _seekBar.Width     = 510;
            _lblTotal.Location = new Point(580, 12);

            // ── Controls ─────────────────────────────────────────────
            var ctrlPanel = MakePanel(DockStyle.Top, 72, BgCard);
            ctrlPanel.Padding = new Padding(16, 12, 16, 12);

            _btnOpen  = MakeButton("📂  開啟", Color.FromArgb(55, 110, 195));
            _btnPlay  = MakeButton("▶  播放", Color.FromArgb(38, 155, 100));
            _btnStop  = MakeButton("⏹  停止", Color.FromArgb(185, 55, 55));

            _btnOpen.Location  = new Point(16, 14);
            _btnPlay.Location  = new Point(134, 14);
            _btnStop.Location  = new Point(252, 14);

            _btnPlay.Enabled = false;
            _btnStop.Enabled = false;

            _btnOpen.Click  += OnOpenClick;
            _btnPlay.Click  += OnPlayPauseClick;
            _btnStop.Click  += OnStopClick;

            // Volume
            var lblVolIcon = MakeLabel("🔊", new Font("Segoe UI", 11f), TextSub);
            lblVolIcon.Location = new Point(390, 18);
            lblVolIcon.AutoSize = true;

            _volBar = new TrackBar
            {
                Minimum   = 0,
                Maximum   = 100,
                Value     = (int)(_volume * 100),
                TickStyle = TickStyle.None,
                BackColor = BgCard,
                Width     = 150,
                Height    = 30,
                Location  = new Point(425, 16)
            };
            _volBar.ValueChanged += (_, _) =>
            {
                _volume = _volBar.Value / 100f;
                if (_reader  != null) _reader.Volume = _volume;
                _lblVolVal.Text = $"{_volBar.Value}%";
            };

            _lblVolVal = MakeLabel($"{_volBar.Value}%", new Font("Segoe UI", 8.5f), TextSub);
            _lblVolVal.Location = new Point(580, 22);
            _lblVolVal.AutoSize = true;

            ctrlPanel.Controls.AddRange(new Control[]
                { _btnOpen, _btnPlay, _btnStop, lblVolIcon, _volBar, _lblVolVal });

            // ── Status bar ───────────────────────────────────────────
            var statusBar = MakePanel(DockStyle.Bottom, 26, BgPanel);
            var lblStatus  = MakeLabel("就緒  ·  支援格式：WAV（PCM）  ·  可拖曳檔案至視窗",
                                       new Font("Segoe UI", 8f), TextSub);
            lblStatus.Location = new Point(12, 6);
            lblStatus.AutoSize = true;
            statusBar.Controls.Add(lblStatus);

            // ── Add panels (reverse = bottom to top for DockStyle.Top) ─
            Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = BgDeep }); // filler
            Controls.Add(ctrlPanel);
            Controls.Add(seekPanel);
            Controls.Add(infoPanel);
            Controls.Add(wfPanel);
            Controls.Add(header);
            Controls.Add(statusBar);

            ResumeLayout();
        }

        // ═══════════════════════════════════════════════════════════════
        //  AUDIO LOGIC
        // ═══════════════════════════════════════════════════════════════
        private void LoadFile(string path)
        {
            if (!path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("僅支援 .wav 格式的音效檔案。", "格式不支援",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            StopPlayback();
            DisposeAudio();

            try
            {
                _reader  = new AudioFileReader(path) { Volume = _volume };
                _waveOut = new WaveOutEvent();
                _waveOut.Init(_reader);
                _waveOut.PlaybackStopped += OnPlaybackStopped;

                // Update UI
                var info = _reader.WaveFormat;
                _lblFileName.Text = Path.GetFileName(path);
                _lblInfo.Text =
                    $"取樣率：{info.SampleRate:N0} Hz  ·  " +
                    $"聲道：{(info.Channels == 1 ? "單聲道" : "立體聲")}  ·  " +
                    $"位元深度：{info.BitsPerSample} bit  ·  " +
                    $"時長：{FormatTime(_reader.TotalTime)}";

                _lblTotal.Text   = FormatTime(_reader.TotalTime);
                _lblCurrent.Text = "00:00";
                _seekBar.Value   = 0;
                _seekBar.Enabled = true;
                _btnPlay.Enabled = true;
                _btnStop.Enabled = false;

                BuildWaveformData(path);
                _dropHint.Visible = false;
                _waveformBox.Invalidate();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"無法開啟檔案：\n{ex.Message}", "錯誤",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>Downsample the WAV to ~2000 points for waveform display.</summary>
        private void BuildWaveformData(string path)
        {
            try
            {
                using var reader = new AudioFileReader(path);
                int   total   = (int)reader.Length / sizeof(float);
                int   samples = Math.Min(total, 4096);
                int   step    = Math.Max(1, total / samples);
                var   buf     = new float[step];
                var   points  = new System.Collections.Generic.List<float>(samples);

                while (reader.Read(buf, 0, step) == step)
                {
                    float max = 0f;
                    foreach (var v in buf) if (Math.Abs(v) > max) max = Math.Abs(v);
                    points.Add(max);
                }
                _waveData = points.ToArray();
            }
            catch { _waveData = null; }
        }

        private void StopPlayback()
        {
            _waveOut?.Stop();
            _timer.Stop();
        }

        private void DisposeAudio()
        {
            _waveOut?.Dispose(); _waveOut = null;
            _reader?.Dispose();  _reader  = null;
        }

        // ═══════════════════════════════════════════════════════════════
        //  EVENT HANDLERS
        // ═══════════════════════════════════════════════════════════════
        private void OnOpenClick(object? s, EventArgs e)
        {
            using var dlg = new OpenFileDialog
            {
                Title  = "開啟 WAV 音效檔",
                Filter = "WAV 音效檔 (*.wav)|*.wav|所有檔案 (*.*)|*.*"
            };
            if (dlg.ShowDialog() == DialogResult.OK) LoadFile(dlg.FileName);
        }

        private void OnPlayPauseClick(object? s, EventArgs e)
        {
            if (_waveOut == null || _reader == null) return;

            if (_waveOut.PlaybackState == PlaybackState.Playing)
            {
                _waveOut.Pause();
                _btnPlay.Text    = "▶  播放";
                _btnPlay.BackColor = Color.FromArgb(38, 155, 100);
                _timer.Stop();
            }
            else
            {
                // restart from beginning if at end
                if (_reader.CurrentTime >= _reader.TotalTime - TimeSpan.FromMilliseconds(500))
                    _reader.CurrentTime = TimeSpan.Zero;

                _waveOut.Play();
                _btnPlay.Text      = "⏸  暫停";
                _btnPlay.BackColor = Color.FromArgb(180, 130, 20);
                _btnStop.Enabled   = true;
                _timer.Start();
            }
        }

        private void OnStopClick(object? s, EventArgs e)
        {
            if (_waveOut == null || _reader == null) return;
            _waveOut.Stop();
            _reader.CurrentTime = TimeSpan.Zero;
            _seekBar.Value      = 0;
            _lblCurrent.Text    = "00:00";
            _btnPlay.Text       = "▶  播放";
            _btnPlay.BackColor  = Color.FromArgb(38, 155, 100);
            _btnStop.Enabled    = false;
            _timer.Stop();
        }

        private void OnPlaybackStopped(object? s, StoppedEventArgs e)
        {
            // Called from audio thread — marshal to UI thread
            Invoke(() =>
            {
                _timer.Stop();
                _btnPlay.Text      = "▶  播放";
                _btnPlay.BackColor = Color.FromArgb(38, 155, 100);
                _btnStop.Enabled   = false;
                if (_reader != null)
                {
                    _seekBar.Value   = 0;
                    _lblCurrent.Text = "00:00";
                    _reader.CurrentTime = TimeSpan.Zero;
                }
            });
        }

        private void OnTimerTick(object? s, EventArgs e)
        {
            if (_reader == null || _waveOut == null) return;
            if (_dragging) return;

            var cur   = _reader.CurrentTime;
            var total = _reader.TotalTime;
            if (total.TotalSeconds > 0)
                _seekBar.Value = (int)(cur.TotalSeconds / total.TotalSeconds * 1000);
            _lblCurrent.Text = FormatTime(cur);

            // Animate waveform playhead
            _waveformBox.Invalidate();
        }

        private void VolumeBar_ValueChanged(object? s, EventArgs e) { /* handled inline */ }

        // ═══════════════════════════════════════════════════════════════
        //  WAVEFORM RENDERING
        // ═══════════════════════════════════════════════════════════════
        private void OnWaveformPaint(object? s, PaintEventArgs e)
        {
            var g   = e.Graphics;
            var rc  = _waveformBox.ClientRectangle;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Background gradient
            using var bgBrush = new LinearGradientBrush(rc,
                Color.FromArgb(10, 10, 16), Color.FromArgb(18, 18, 28),
                LinearGradientMode.Vertical);
            g.FillRectangle(bgBrush, rc);

            if (_waveData == null || _waveData.Length == 0) return;

            int    w      = rc.Width;
            int    h      = rc.Height;
            float  cy     = h / 2f;
            int    n      = _waveData.Length;
            float  dx     = (float)w / n;

            // Playback progress ratio
            double progress = (_reader != null && _reader.TotalTime.TotalSeconds > 0)
                ? _reader.CurrentTime.TotalSeconds / _reader.TotalTime.TotalSeconds
                : 0.0;
            int playX = (int)(w * progress);

            // Draw waveform bars
            for (int i = 0; i < n; i++)
            {
                float x    = i * dx;
                float amp  = _waveData[i] * cy * 0.90f;
                bool  past = x <= playX;

                Color col = past
                    ? Color.FromArgb(180, Accent.R, Accent.G, Accent.B)
                    : Color.FromArgb(60, 80, 80, 120);

                using var pen = new Pen(col, Math.Max(1f, dx * 0.6f));
                g.DrawLine(pen, x, cy - amp, x, cy + amp);
            }

            // Playhead line
            if (progress > 0)
            {
                using var headPen = new Pen(Color.FromArgb(220, 255, 255, 255), 1.5f);
                g.DrawLine(headPen, playX, 2, playX, h - 2);
            }

            // Centre line
            using var midPen = new Pen(Color.FromArgb(40, 255, 255, 255), 0.5f);
            g.DrawLine(midPen, 0, cy, w, cy);
        }

        // ═══════════════════════════════════════════════════════════════
        //  HELPERS
        // ═══════════════════════════════════════════════════════════════
        private static string FormatTime(TimeSpan t)
            => t.TotalHours >= 1
                ? $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}"
                : $"{t.Minutes:D2}:{t.Seconds:D2}";

        private static Panel MakePanel(DockStyle dock, int h, Color bg)
            => new() { Dock = dock, Height = h, BackColor = bg };

        private static Label MakeLabel(string text, Font font, Color fore)
            => new() { Text = text, Font = font, ForeColor = fore, BackColor = Color.Transparent };

        private static Button MakeButton(string text, Color baseCol)
        {
            var btn = new Button
            {
                Text      = text,
                Width     = 108,
                Height    = 40,
                FlatStyle = FlatStyle.Flat,
                BackColor = baseCol,
                ForeColor = Color.White,
                Font      = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Cursor    = Cursors.Hand
            };
            btn.FlatAppearance.BorderSize  = 0;
            btn.FlatAppearance.MouseOverBackColor  = ControlPaint.Light(baseCol, 0.25f);
            btn.FlatAppearance.MouseDownBackColor  = ControlPaint.Dark(baseCol, 0.1f);
            return btn;
        }

        // ═══════════════════════════════════════════════════════════════
        //  CLEANUP
        // ═══════════════════════════════════════════════════════════════
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            StopPlayback();
            DisposeAudio();
            base.OnFormClosing(e);
        }
    }
}
