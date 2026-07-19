using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Klip.Engine.Audio;
using Klip.Model;

namespace Klip.App;

/// <summary>
/// DAW (fase A1): faixas de áudio, waveform desenhada, volume/pan/mute/solo, e transporte
/// colado ao playhead da animação. O mix é PRÉ-CALCULADO num buffer (sem alocações no
/// callback de áudio) e o mesmo buffer serve para muxar no MP4.
/// </summary>
public partial class MainWindow : Window
{
    private readonly List<AudioTrack> _audio = new();
    private readonly AudioPlayer _player = new();
    private MixBuffer? _mix;
    private bool _mixDirty = true;
    private StackPanel? _dawList;
    private Image? _waveImg;
    private TextBlock? _dawInfo;

    /// <summary>Reconstrói a mistura só quando algo mudou (import, volume, pan, trim…).</summary>
    private MixBuffer EnsureMix()
    {
        if (_mix is null || _mixDirty)
        {
            _mix = AudioMixer.Mix(_audio, Math.Max(0.1, _motionDur));
            _mixDirty = false;
        }
        return _mix;
    }

    private void DawChanged()
    {
        _mixDirty = true;
        BuildDawPanel();
        DrawWaveform();
    }

    // ---------- transporte (chamado pelo OnPlay/StopPlayback da timeline) ----------
    private void AudioPlayFrom(double t)
    {
        try { if (_audio.Count > 0) _player.Play(EnsureMix(), t); } catch { }
    }

    private void AudioStop()
    {
        try { _player.Stop(); } catch { }
    }

    // ---------- UI ----------
    private void BuildDawPanel()
    {
        if (_dawList is null) return;
        _dawList.Children.Clear();

        if (_audio.Count == 0)
        {
            _dawList.Children.Add(new TextBlock
            {
                Text = "Sem áudio. Importa uma música ou voz para sincronizar com a animação.",
                FontSize = 10.5, Foreground = new SolidColorBrush(Color.Parse("#9A9A97")), TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        for (int i = 0; i < _audio.Count; i++)
        {
            int ix = i;
            var tr = _audio[ix];
            var box = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#FFFFFE")),
                BorderBrush = new SolidColorBrush(Color.Parse("#ECECEA")),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
                Padding = new Thickness(7, 5), Margin = new Thickness(0, 0, 0, 5),
            };
            var st = new StackPanel { Spacing = 2 };

            // cabeçalho: nome + M/S + apagar
            var head = new DockPanel();
            var del = new Button { Content = "🗑", FontSize = 10, Width = 22, Height = 20, Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            del.Click += (_, _) => { _audio.RemoveAt(ix); DawChanged(); };
            DockPanel.SetDock(del, Dock.Right); head.Children.Add(del);
            var solo = MiniToggle("S", tr.Solo, on => { _audio[ix] = _audio[ix] with { Solo = on }; DawChanged(); });
            DockPanel.SetDock(solo, Dock.Right); head.Children.Add(solo);
            var mute = MiniToggle("M", tr.Mute, on => { _audio[ix] = _audio[ix] with { Mute = on }; DawChanged(); });
            DockPanel.SetDock(mute, Dock.Right); head.Children.Add(mute);
            head.Children.Add(new TextBlock
            {
                Text = tr.Name, FontSize = 11, FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(Color.Parse("#3A3A38")), VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            st.Children.Add(head);

            st.Children.Add(DawRow("Volume", 0, 2, tr.Volume, v => { _audio[ix] = _audio[ix] with { Volume = v }; _mixDirty = true; }, "0.00"));
            st.Children.Add(DawRow("Pan", -1, 1, tr.Pan, v => { _audio[ix] = _audio[ix] with { Pan = v }; _mixDirty = true; }, "0.00"));

            var clip = tr.Clips.FirstOrDefault();
            if (clip is not null)
            {
                st.Children.Add(DawRow("Entra a", 0, Math.Max(1, _motionDur), clip.Start,
                    v => { _audio[ix] = ReplaceClip(_audio[ix], c => c with { Start = v }); _mixDirty = true; }, "0.00", "s"));
                st.Children.Add(DawRow("Corte ini.", 0, Math.Max(0.1, AudioMixer.DurationOf(clip.Path)), clip.TrimStart,
                    v => { _audio[ix] = ReplaceClip(_audio[ix], c => c with { TrimStart = v }); _mixDirty = true; }, "0.00", "s"));
                st.Children.Add(DawRow("Fade in", 0, 5, clip.FadeIn,
                    v => { _audio[ix] = ReplaceClip(_audio[ix], c => c with { FadeIn = v }); _mixDirty = true; }, "0.00", "s"));
                st.Children.Add(DawRow("Fade out", 0, 5, clip.FadeOut,
                    v => { _audio[ix] = ReplaceClip(_audio[ix], c => c with { FadeOut = v }); _mixDirty = true; }, "0.00", "s"));
            }
            box.Child = st;
            _dawList.Children.Add(box);
        }
    }

    private static AudioTrack ReplaceClip(AudioTrack t, Func<AudioClip, AudioClip> f)
        => t with { Clips = t.Clips.Select((c, i) => i == 0 ? f(c) : c).ToList() };

    private Button MiniToggle(string label, bool on, Action<bool> set)
    {
        var b = new Button
        {
            Content = label, FontSize = 9.5, Width = 20, Height = 20, Padding = new Thickness(0),
            CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = on ? new SolidColorBrush(Color.Parse("#6D5EF6")) : Brushes.Transparent,
            Foreground = on ? Brushes.White : new SolidColorBrush(Color.Parse("#9A9A97")),
        };
        b.Click += (_, _) => { set(!on); };
        return b;
    }

    private Control DawRow(string label, double min, double max, double val, Action<double> set, string fmt, string suffix = "")
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("58,*,40"), Height = 20 };
        var lb = new TextBlock { Text = label, FontSize = 10, Foreground = new SolidColorBrush(Color.Parse("#6B6B68")), VerticalAlignment = VerticalAlignment.Center };
        Avalonia.Controls.Grid.SetColumn(lb, 0); g.Children.Add(lb);

        var sl = new Slider { Minimum = min, Maximum = max, Value = Math.Clamp(val, min, max), Height = 20, Padding = new Thickness(0), Margin = new Thickness(2, 0), VerticalAlignment = VerticalAlignment.Center };
        Avalonia.Controls.Grid.SetColumn(sl, 1); g.Children.Add(sl);

        var vt = new TextBlock { Text = val.ToString(fmt, CultureInfo.InvariantCulture) + suffix, FontSize = 9.5, Foreground = new SolidColorBrush(Color.Parse("#3A3A38")), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
        Avalonia.Controls.Grid.SetColumn(vt, 2); g.Children.Add(vt);

        sl.PropertyChanged += (_, ev) =>
        {
            if (ev.Property.Name != "Value") return;
            vt.Text = sl.Value.ToString(fmt, CultureInfo.InvariantCulture) + suffix;
            set(sl.Value);
        };
        sl.AddHandler(Avalonia.Input.InputElement.PointerReleasedEvent, (_, _) => DrawWaveform(),
                      Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble);
        return g;
    }

    /// <summary>Desenha a waveform do mix (Skia → WriteableBitmap), com a régua de tempo.</summary>
    private void DrawWaveform()
    {
        if (_waveImg is null) return;
        try
        {
            int w = 288, h = 74;
            var wb = new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
            using (var fb = wb.Lock())
            {
                var info = new SkiaSharp.SKImageInfo(w, h, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
                using var surf = SkiaSharp.SKSurface.Create(info, fb.Address, fb.RowBytes);
                var c = surf.Canvas;
                c.Clear(new SkiaSharp.SKColor(0xFFF7F7F5));
                float mid = h / 2f;
                using var axis = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(0xFFDDDDDA), StrokeWidth = 1 };
                c.DrawLine(0, mid, w, mid, axis);

                if (_audio.Count > 0)
                {
                    var mix = EnsureMix();
                    var (lo, hi) = AudioMixer.Peaks(mix, w);
                    using var wave = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(0xFF6D5EF6), StrokeWidth = 1, IsAntialias = true };
                    for (int x = 0; x < w; x++)
                    {
                        float y0 = mid - hi[x] * (mid - 3);
                        float y1 = mid - lo[x] * (mid - 3);
                        if (Math.Abs(y1 - y0) < 1) { y0 = mid - 0.5f; y1 = mid + 0.5f; }
                        c.DrawLine(x, y0, x, y1, wave);
                    }
                    // playhead
                    double D = Math.Max(0.1, _motionDur);
                    float px = (float)(_previewT / D) * w;
                    using var ph = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(0xFFE0245E), StrokeWidth = 1.5f };
                    c.DrawLine(px, 0, px, h, ph);
                }
                c.Flush();
            }
            _waveImg.Source = wb;
            if (_dawInfo is not null)
                _dawInfo.Text = _audio.Count == 0 ? "—"
                    : $"{_audio.Count} faixa(s) · mix {EnsureMix().Duration:0.0}s @ 48kHz";
        }
        catch { }
    }

    private async void OnImportAudio(object? s, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "Importar áudio",
                AllowMultiple = true,
                FileTypeFilter = new[] { new Avalonia.Platform.Storage.FilePickerFileType("Áudio")
                    { Patterns = new[] { "*.wav", "*.mp3", "*.flac", "*.m4a", "*.aac", "*.ogg" } } },
            });
            if (files is null) return;
            foreach (var f in files)
            {
                var p = f.Path?.LocalPath;
                if (string.IsNullOrEmpty(p)) continue;
                double dur = AudioMixer.DurationOf(p);
                _audio.Add(new AudioTrack(System.IO.Path.GetFileNameWithoutExtension(p),
                    new[] { new AudioClip(p, Start: 0, Name: System.IO.Path.GetFileName(p)) }));
                if (dur > _motionDur) { _motionDur = Math.Min(dur, 600); }   // timeline acompanha a música
            }
            DawChanged();
            Refresh();
        }
        catch (Exception ex) { if (_dawInfo is not null) _dawInfo.Text = "erro: " + ex.Message; }
    }

    /// <summary>Constrói a aba Áudio (uma vez).</summary>
    private void BuildDawTab()
    {
        if (DawStack is null || DawStack.Children.Count > 0) return;

        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Margin = new Thickness(0, 0, 0, 6) };
        var imp = new Button { Content = "＋ Importar áudio", FontSize = 10.5, Height = 24, Padding = new Thickness(9, 0), CornerRadius = new CornerRadius(7) };
        imp.Click += OnImportAudio;
        top.Children.Add(imp);
        var prev = new Button { Content = "▶ Ouvir", FontSize = 10.5, Height = 24, Padding = new Thickness(9, 0), CornerRadius = new CornerRadius(7) };
        prev.Click += (_, _) => { if (_player.IsPlaying) AudioStop(); else AudioPlayFrom(_previewT); };
        top.Children.Add(prev);
        DawStack.Children.Add(top);

        _waveImg = new Image { Height = 74, Stretch = Stretch.Fill, Margin = new Thickness(0, 0, 0, 4) };
        DawStack.Children.Add(_waveImg);

        _dawInfo = new TextBlock { Text = "—", FontSize = 9.5, Foreground = new SolidColorBrush(Color.Parse("#9A9A97")), Margin = new Thickness(0, 0, 0, 6) };
        DawStack.Children.Add(_dawInfo);

        _dawList = new StackPanel { Spacing = 0 };
        DawStack.Children.Add(_dawList);

        BuildDawPanel();
        DrawWaveform();
    }
}
