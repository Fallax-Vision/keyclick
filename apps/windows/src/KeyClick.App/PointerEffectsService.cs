using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using KeyClick.Core;
using Forms = System.Windows.Forms;

namespace KeyClick.App;

public sealed class PointerEffectsService : IDisposable
{
  private readonly object _gate = new();
  private Thread? _thread;
  private EffectOverlayForm? _form;
  private EffectConfiguration _configuration = EffectConfiguration.Disabled;

  public bool IsRunning => _thread is not null;
  public event Action<string>? HealthChanged;

  public void Configure(PointerStudioSettings settings, PointerThemeDefinition theme, bool reducedMotion)
  {
    settings.Normalize();
    var active = settings.Enabled && !reducedMotion && (settings.MotionEffectsEnabled || settings.ClickIndicatorsEnabled);
    var configuration = active ? EffectConfiguration.From(settings, theme) : EffectConfiguration.Disabled;
    lock (_gate) _configuration = configuration;
    if (active) EnsureStarted();
    else Stop();
    _form?.PostConfiguration(configuration);
  }

  public void SignalMovement(PointerMovementSignal signal)
  {
    EffectOverlayForm? form;
    lock (_gate) { if (!_configuration.MotionEnabled || _thread is null) return; form = _form; }
    form?.PostMovement(signal);
  }

  public void SignalClick(int buttonCode)
  {
    EffectOverlayForm? form;
    EffectConfiguration configuration;
    lock (_gate) { form = _form; configuration = _configuration; }
    if (form is null || !configuration.ClicksEnabled) return;
    form.PostClick(buttonCode);
  }

  public void FindPointer() => _form?.PostFindPointer();

  private void EnsureStarted()
  {
    lock (_gate)
    {
      if (_thread is not null) return;
      _thread = new Thread(() =>
      {
        try
        {
          var form = new EffectOverlayForm(_configuration, message => HealthChanged?.Invoke(message));
          lock (_gate) _form = form;
          Forms.Application.Run(form);
          lock (_gate) _form = null;
        }
        catch (Exception exception) { HealthChanged?.Invoke(exception.Message); }
      }) { IsBackground = true, Name = "KeyClick Pointer Effects", Priority = ThreadPriority.BelowNormal };
      _thread.SetApartmentState(ApartmentState.STA);
      _thread.Start();
    }
  }

  private void Stop()
  {
    Thread? thread;
    EffectOverlayForm? form;
    lock (_gate)
    {
      thread = _thread; form = _form; _thread = null;
    }
    if (form is not null && !form.IsDisposed) form.BeginInvoke(form.Close);
    thread?.Join(TimeSpan.FromSeconds(1));
  }

  public void Dispose() => Stop();

  private sealed record EffectConfiguration(
    bool MotionEnabled,
    bool ClicksEnabled,
    bool FullReplacement,
    bool Adaptive,
    bool PauseOnBattery,
    bool PauseFullscreen,
    bool PauseRemote,
    PointerMotionPreset Preset,
    double VisualScale,
    double Smoothing,
    double Spring,
    double Damping,
    int TrailLength,
    double ShakeSensitivity,
    bool ShakeToEnlarge,
    double ShakeScale,
    Color Primary,
    Color Secondary,
    Color Outline,
    ClickIndicatorSnapshot Left,
    ClickIndicatorSnapshot Right,
    ClickIndicatorSnapshot Middle,
    ClickIndicatorSnapshot Auxiliary)
  {
    public static readonly EffectConfiguration Disabled = new(false, false, false, true, true, true, true, PointerMotionPreset.None, 1, 0.5, 0.6, 0.7, 0, 0.6, false, 1, Color.White, Color.LimeGreen, Color.Black, default, default, default, default);
    public static EffectConfiguration From(PointerStudioSettings settings, PointerThemeDefinition theme) => new(
      settings.MotionEffectsEnabled,
      settings.ClickIndicatorsEnabled,
      settings.MotionMode == PointerMotionMode.FullReplacement && settings.ExperimentalReplacementEnabled,
      settings.AdaptivePerformance,
      settings.PauseOnBatterySaver,
      settings.PauseInFullscreen,
      settings.PauseInRemoteSession,
      settings.MotionPreset,
      settings.VisualScale,
      settings.Smoothing,
      settings.SpringStrength,
      settings.Damping,
      settings.TrailLength,
      settings.ShakeSensitivity,
      settings.ShakeToEnlarge,
      settings.ShakeScale,
      ColorTranslator.FromHtml(theme.Primary),
      ColorTranslator.FromHtml(theme.Secondary),
      ColorTranslator.FromHtml(theme.Outline),
      ClickIndicatorSnapshot.From(settings.LeftClick), ClickIndicatorSnapshot.From(settings.RightClick), ClickIndicatorSnapshot.From(settings.MiddleClick), ClickIndicatorSnapshot.From(settings.AuxiliaryClick));

    public ClickIndicatorSnapshot Indicator(int code) => code switch { 1 => Left, 2 => Right, 3 => Middle, _ => Auxiliary };
  }

  private readonly record struct ClickIndicatorSnapshot(bool Enabled, PointerClickIndicatorStyle Style, string Color, double Opacity, double Size, double Intensity, int ParticleCount, int DurationMilliseconds)
  {
    public static ClickIndicatorSnapshot From(PointerClickIndicatorSettings settings) => new(settings.Enabled, settings.Style, settings.Color, settings.Opacity, settings.Size, settings.Intensity, settings.ParticleCount, settings.DurationMilliseconds);
  }

  private sealed class EffectOverlayForm : Forms.Form
  {
    private const int ExTransparent = 0x20;
    private const int ExToolWindow = 0x80;
    private const int ExNoActivate = 0x08000000;
    private const int WmMovement = 0x8001;
    private const int WmClick = 0x8002;
    private readonly Forms.Timer _timer;
    private readonly List<TrailPoint> _trail = new(24);
    private readonly List<ClickBurst> _clicks = new(32);
    private readonly Action<string> _health;
    private EffectConfiguration _configuration;
    private PointF _renderPosition;
    private PointF _velocity;
    private long _lastMovement;
    private long _lastFrame;
    private double _shakeEnergy;
    private long _findUntil;
    private int _movementPosted;
    private int _pendingDeltaX;
    private int _pendingDeltaY;
    private long _pendingTimestamp;

    public EffectOverlayForm(EffectConfiguration configuration, Action<string> health)
    {
      _configuration = configuration;
      _health = health;
      FormBorderStyle = Forms.FormBorderStyle.None;
      ShowInTaskbar = false;
      TopMost = true;
      BackColor = Color.Magenta;
      TransparencyKey = Color.Magenta;
      Bounds = Forms.SystemInformation.VirtualScreen;
      StartPosition = Forms.FormStartPosition.Manual;
      DoubleBuffered = true;
      _timer = new Forms.Timer { Interval = FrameInterval(configuration) };
      _timer.Tick += (_, _) => TickFrame();
      GetCursorPos(out var point);
      _renderPosition = new(point.X, point.Y);
    }

    protected override bool ShowWithoutActivation => true;
    protected override Forms.CreateParams CreateParams
    {
      get { var parameters = base.CreateParams; parameters.ExStyle |= ExTransparent | ExToolWindow | ExNoActivate; return parameters; }
    }

    public void PostConfiguration(EffectConfiguration configuration)
    {
      if (IsDisposed) return;
      BeginInvoke(() => { _configuration = configuration; _timer.Interval = FrameInterval(configuration); if (!configuration.MotionEnabled && !configuration.ClicksEnabled) StopAnimation(); });
    }

    public void PostMovement(PointerMovementSignal signal)
    {
      if (IsDisposed || !IsHandleCreated) return;
      Volatile.Write(ref _pendingDeltaX, signal.DeltaX);
      Volatile.Write(ref _pendingDeltaY, signal.DeltaY);
      Volatile.Write(ref _pendingTimestamp, signal.Timestamp);
      if (Interlocked.Exchange(ref _movementPosted, 1) == 0) PostMessage(Handle, WmMovement, 0, 0);
    }

    public void PostClick(int buttonCode)
    {
      if (!IsDisposed && IsHandleCreated) PostMessage(Handle, WmClick, buttonCode, 0);
    }

    protected override void WndProc(ref Forms.Message message)
    {
      if (message.Msg == WmMovement)
      {
        Interlocked.Exchange(ref _movementPosted, 0);
        var deltaX = Volatile.Read(ref _pendingDeltaX);
        var deltaY = Volatile.Read(ref _pendingDeltaY);
        _lastMovement = Volatile.Read(ref _pendingTimestamp);
        _shakeEnergy = Math.Min(1, _shakeEnergy * 0.72 + Math.Sqrt(deltaX * deltaX + deltaY * deltaY) / 90d);
        StartAnimation();
        return;
      }
      if (message.Msg == WmClick)
      {
        var indicator = _configuration.Indicator((int)message.WParam);
        if (indicator.Enabled && indicator.Style != PointerClickIndicatorStyle.None)
        {
          GetCursorPos(out var point);
          if (_clicks.Count == 32) _clicks.RemoveAt(0);
          _clicks.Add(new(new(point.X, point.Y), Stopwatch.GetTimestamp(), indicator));
          StartAnimation();
        }
        return;
      }
      base.WndProc(ref message);
    }

    public void PostFindPointer()
    {
      if (IsDisposed) return;
      BeginInvoke(() => { _findUntil = Stopwatch.GetTimestamp() + Stopwatch.Frequency; StartAnimation(); });
    }

    private void StartAnimation()
    {
      if (ShouldPause()) return;
      if (!Visible) Show();
      if (!_timer.Enabled) { _lastFrame = Stopwatch.GetTimestamp(); _timer.Start(); }
    }

    private void StopAnimation()
    {
      _timer.Stop(); _trail.Clear(); _clicks.Clear(); Hide();
    }

    private void TickFrame()
    {
      var started = Stopwatch.GetTimestamp();
      if (ShouldPause()) { StopAnimation(); return; }
      GetCursorPos(out var cursor);
      var target = new PointF(cursor.X, cursor.Y);
      var elapsed = Math.Clamp((started - _lastFrame) / (double)Stopwatch.Frequency, 0.001, 0.05);
      _lastFrame = started;
      var stiffness = 8 + _configuration.Spring * 24;
      _velocity.X = (float)((_velocity.X + (target.X - _renderPosition.X) * stiffness * elapsed) * Math.Pow(_configuration.Damping, elapsed * 60));
      _velocity.Y = (float)((_velocity.Y + (target.Y - _renderPosition.Y) * stiffness * elapsed) * Math.Pow(_configuration.Damping, elapsed * 60));
      _renderPosition.X += _velocity.X * (float)(elapsed * (1.2 - _configuration.Smoothing * 0.5));
      _renderPosition.Y += _velocity.Y * (float)(elapsed * (1.2 - _configuration.Smoothing * 0.5));
      _trail.Add(new(target, started));
      while (_trail.Count > Math.Max(2, _configuration.TrailLength) || (_trail.Count > 0 && started - _trail[0].Timestamp > Stopwatch.Frequency)) _trail.RemoveAt(0);
      _shakeEnergy *= 0.9;
      _clicks.RemoveAll(click => (started - click.Started) * 1000d / Stopwatch.Frequency > click.Settings.DurationMilliseconds);
      Invalidate();
      var movingRecently = started - _lastMovement < Stopwatch.Frequency * 0.35;
      var settling = Math.Abs(_velocity.X) + Math.Abs(_velocity.Y) > 0.35 || Distance(_renderPosition, target) > 0.5;
      if (!movingRecently && !settling && _clicks.Count == 0 && started > _findUntil) StopAnimation();
      var frameMilliseconds = (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;
      if (_configuration.Adaptive && frameMilliseconds > 8) { _timer.Interval = 33; _health("Pointer effects reduced to protect performance."); }
    }

    protected override void OnPaint(Forms.PaintEventArgs e)
    {
      base.OnPaint(e);
      var graphics = e.Graphics;
      graphics.SmoothingMode = SmoothingMode.AntiAlias;
      graphics.TranslateTransform(-Left, -Top);
      if (_configuration.MotionEnabled) DrawMotion(graphics);
      foreach (var click in _clicks) DrawClick(graphics, click);
      if (Stopwatch.GetTimestamp() < _findUntil) DrawFinder(graphics);
    }

    private void DrawMotion(Graphics graphics)
    {
      if (_configuration.Preset != PointerMotionPreset.None && _trail.Count > 1)
      {
        for (var index = 1; index < _trail.Count; index++)
        {
          var alpha = (int)(180d * index / _trail.Count);
          using var pen = new Pen(Color.FromArgb(alpha, _configuration.Secondary), (float)Math.Max(2, 8d * index / _trail.Count * _configuration.VisualScale)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
          graphics.DrawLine(pen, _trail[index - 1].Position, _trail[index].Position);
        }
      }
      if (_configuration.Preset is PointerMotionPreset.Glow or PointerMotionPreset.Liquid or PointerMotionPreset.Elastic)
      {
        var radius = (float)(11 * _configuration.VisualScale * (1 + _shakeEnergy * 0.5));
        using var brush = new SolidBrush(Color.FromArgb(55, _configuration.Secondary));
        graphics.FillEllipse(brush, _renderPosition.X - radius, _renderPosition.Y - radius, radius * 2, radius * 2);
      }
      if (_configuration.FullReplacement)
      {
        var scale = (float)(_configuration.VisualScale * (_configuration.ShakeToEnlarge && _shakeEnergy > _configuration.ShakeSensitivity ? _configuration.ShakeScale : 1));
        var points = new[] { new PointF(_renderPosition.X, _renderPosition.Y), new PointF(_renderPosition.X + 3 * scale, _renderPosition.Y + 25 * scale), new PointF(_renderPosition.X + 10 * scale, _renderPosition.Y + 18 * scale), new PointF(_renderPosition.X + 16 * scale, _renderPosition.Y + 29 * scale), new PointF(_renderPosition.X + 21 * scale, _renderPosition.Y + 26 * scale), new PointF(_renderPosition.X + 15 * scale, _renderPosition.Y + 16 * scale), new PointF(_renderPosition.X + 25 * scale, _renderPosition.Y + 15 * scale) };
        using var fill = new SolidBrush(_configuration.Primary); using var outline = new Pen(_configuration.Outline, Math.Max(1.5f, 2 * scale));
        graphics.FillPolygon(fill, points); graphics.DrawPolygon(outline, points);
      }
    }

    private static void DrawClick(Graphics graphics, ClickBurst click)
    {
      var elapsed = (Stopwatch.GetTimestamp() - click.Started) * 1000d / Stopwatch.Frequency;
      var progress = Math.Clamp(elapsed / click.Settings.DurationMilliseconds, 0, 1);
      var color = ColorTranslator.FromHtml(click.Settings.Color);
      var alpha = (int)(255 * click.Settings.Opacity * (1 - progress));
      var radius = (float)(click.Settings.Size * (0.35 + progress * 0.65));
      using var pen = new Pen(Color.FromArgb(alpha, color), (float)Math.Max(1, 3 * click.Settings.Intensity));
      if (click.Settings.Style is PointerClickIndicatorStyle.Ring or PointerClickIndicatorStyle.Ripple or PointerClickIndicatorStyle.Pulse)
      {
        graphics.DrawEllipse(pen, click.Position.X - radius, click.Position.Y - radius, radius * 2, radius * 2);
        if (click.Settings.Style == PointerClickIndicatorStyle.Ripple) graphics.DrawEllipse(pen, click.Position.X - radius * 0.55f, click.Position.Y - radius * 0.55f, radius * 1.1f, radius * 1.1f);
        return;
      }
      var count = Math.Min(48, click.Settings.ParticleCount);
      for (var index = 0; index < count; index++)
      {
        var angle = index * Math.PI * 2 / count + (click.Settings.Style == PointerClickIndicatorStyle.Sparkles ? progress : 0);
        var inner = radius * (click.Settings.Style == PointerClickIndicatorStyle.RadialTicks ? 0.55f : 0.2f);
        var outer = radius * (float)(0.75 + (index % 3) * 0.12);
        var start = new PointF(click.Position.X + inner * (float)Math.Cos(angle), click.Position.Y + inner * (float)Math.Sin(angle));
        var end = new PointF(click.Position.X + outer * (float)Math.Cos(angle), click.Position.Y + outer * (float)Math.Sin(angle));
        graphics.DrawLine(pen, start, end);
      }
    }

    private void DrawFinder(Graphics graphics)
    {
      GetCursorPos(out var point);
      var remaining = Math.Max(0, (_findUntil - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency);
      var radius = (float)(35 + (1 - remaining) * 45);
      using var pen = new Pen(Color.FromArgb((int)(220 * remaining), _configuration.Secondary), 4);
      graphics.DrawEllipse(pen, point.X - radius, point.Y - radius, radius * 2, radius * 2);
    }

    private bool ShouldPause() => _configuration.Adaptive &&
      ((_configuration.PauseRemote && Forms.SystemInformation.TerminalServerSession) ||
       (_configuration.PauseOnBattery && Forms.SystemInformation.PowerStatus.PowerLineStatus == Forms.PowerLineStatus.Offline) ||
       (_configuration.PauseFullscreen && FullscreenDetector.IsForegroundFullscreen()));
    private static int FrameInterval(EffectConfiguration configuration) => configuration.Adaptive && Forms.SystemInformation.PowerStatus.PowerLineStatus == Forms.PowerLineStatus.Offline ? 33 : 16;
    private static float Distance(PointF left, PointF right) { var x = left.X - right.X; var y = left.Y - right.Y; return MathF.Sqrt(x * x + y * y); }
    private readonly record struct TrailPoint(PointF Position, long Timestamp);
    private readonly record struct ClickBurst(PointF Position, long Started, ClickIndicatorSnapshot Settings);
  }

  [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
  [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
  [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
}
