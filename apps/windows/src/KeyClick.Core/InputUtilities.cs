namespace KeyClick.Core;

public static class InputEventRules
{
  public static bool IsKeyboardDown(ushort flags, ushort virtualKey) => (flags & 0x0001) == 0 && virtualKey != 0x00FF;
  public static bool IsKeyboardRelease(ushort flags, ushort virtualKey) => (flags & 0x0001) != 0 && virtualKey != 0x00FF;

  public static bool ShouldPlayKeyboardSound(KeyboardSoundTiming timing, InputPhase phase) =>
    timing == KeyboardSoundTiming.KeyDown ? phase == InputPhase.Down : phase == InputPhase.Up;

  public static KeyVariant ResolveVariant(bool altGrDown, bool shiftDown, bool isLockKey, bool lockEnabled)
  {
    if (isLockKey) return lockEnabled ? KeyVariant.Enabled : KeyVariant.Disabled;
    if (altGrDown) return KeyVariant.AltGr;
    return shiftDown ? KeyVariant.Shift : KeyVariant.Base;
  }
}

public sealed class WheelAccumulator
{
  public const int DetentDelta = 120;
  public const int MaximumDetentsPerReport = 8;
  private int _remainder;

  public int AddDetents(int delta)
  {
    var accumulated = _remainder + delta;
    var detents = accumulated / DetentDelta;
    _remainder = accumulated % DetentDelta;
    return Math.Clamp(detents, -MaximumDetentsPerReport, MaximumDetentsPerReport);
  }

  public IReadOnlyList<int> Add(int delta)
  {
    var detents = AddDetents(delta);
    if (detents == 0) return [];
    return Enumerable.Repeat(Math.Sign(detents), Math.Abs(detents)).ToArray();
  }

  public int Remainder => _remainder;
}

public static class CustomSampleId
{
  public const int HashLength = 64;
  public const int TotalLength = 7 + HashLength;

  public static bool IsValid(string? value) =>
    value is { Length: TotalLength } &&
    value.StartsWith("custom:", StringComparison.Ordinal) &&
    value.AsSpan(7).ToString().All(char.IsAsciiHexDigit);

  public static string FileName(string value)
  {
    if (!IsValid(value)) throw new InvalidDataException("The custom sample identifier is invalid.");
    return $"{value[7..].ToLowerInvariant()}.wav";
  }
}

public static class ShortcutBindingValidator
{
  public static string? Validate(IEnumerable<ShortcutBinding> bindings)
  {
    var enabled = bindings.Where(item => item.Enabled).ToArray();
    foreach (var binding in enabled)
    {
      if (binding.Steps.Count == 0) return $"{binding.Name} has no shortcut steps.";
      if (binding.Kind == ShortcutKind.Chord && binding.Steps.Count != 1) return $"{binding.Name} must have one chord.";
      if (binding.Kind == ShortcutKind.Sequence && binding.Steps.Count != 2) return $"{binding.Name} must have two sequence steps.";
      if (binding.Scope == ShortcutScope.Global && binding.Kind == ShortcutKind.Chord)
      {
        var step = binding.Steps[0];
        if (!step.Control && !step.Alt && !step.Shift && !step.Windows) return $"{binding.Name} needs a modifier for global use.";
      }
    }

    for (var left = 0; left < enabled.Length; left++)
    {
      for (var right = left + 1; right < enabled.Length; right++)
      {
        if (enabled[left].Scope != enabled[right].Scope) continue;
        if (enabled[left].Steps.SequenceEqual(enabled[right].Steps))
          return $"{enabled[left].Name} overlaps {enabled[right].Name}.";
        if (enabled[left].Kind == ShortcutKind.Sequence && enabled[right].Kind == ShortcutKind.Sequence &&
            enabled[left].Steps[0] == enabled[right].Steps[0])
          return $"{enabled[left].Name} and {enabled[right].Name} share an ambiguous first step.";
      }
    }
    return null;
  }
}

public static class IntegrationRequestValidator
{
  public static string? Validate(IntegrationResultRequest? request)
  {
    if (request is null) return "invalid-json";
    if (request.Version != 1) return "unsupported-version";
    if (!string.Equals(request.Type, "action-result", StringComparison.Ordinal)) return "unsupported-message";
    if (request.InputId?.Length > 128 || request.ActionId?.Length > 128) return "field-too-long";
    return null;
  }
}

public sealed class SlidingRateLimiter(int limit)
{
  private readonly Queue<long> _accepted = new();

  public bool TryAccept(long now, long windowTicks)
  {
    lock (_accepted)
    {
      var cutoff = now - windowTicks;
      while (_accepted.TryPeek(out var value) && value < cutoff) _accepted.Dequeue();
      if (_accepted.Count >= limit) return false;
      _accepted.Enqueue(now);
      return true;
    }
  }
}
