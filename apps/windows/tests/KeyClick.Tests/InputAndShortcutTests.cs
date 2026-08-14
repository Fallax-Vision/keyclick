using KeyClick.Core;

namespace KeyClick.Tests;

public sealed class InputAndShortcutTests
{
  [Fact]
  public void Keyboard_rules_distinguish_make_break_and_reject_invalid_virtual_key()
  {
    Assert.True(InputEventRules.IsKeyboardDown(0, 0x41));
    Assert.False(InputEventRules.IsKeyboardDown(1, 0x41));
    Assert.False(InputEventRules.IsKeyboardRelease(0, 0x41));
    Assert.True(InputEventRules.IsKeyboardRelease(1, 0x41));
    Assert.False(InputEventRules.IsKeyboardRelease(1, 0xFF));
  }

  [Fact]
  public void Keyboard_sound_timing_plays_only_the_selected_phase()
  {
    Assert.True(InputEventRules.ShouldPlayKeyboardSound(KeyboardSoundTiming.KeyDown, InputPhase.Down));
    Assert.False(InputEventRules.ShouldPlayKeyboardSound(KeyboardSoundTiming.KeyDown, InputPhase.Up));
    Assert.True(InputEventRules.ShouldPlayKeyboardSound(KeyboardSoundTiming.KeyUp, InputPhase.Up));
    Assert.False(InputEventRules.ShouldPlayKeyboardSound(KeyboardSoundTiming.KeyUp, InputPhase.Down));
  }

  [Theory]
  [InlineData(false, false, false, false, KeyVariant.Base)]
  [InlineData(false, true, false, false, KeyVariant.Shift)]
  [InlineData(true, true, false, false, KeyVariant.AltGr)]
  [InlineData(false, false, true, true, KeyVariant.Enabled)]
  [InlineData(true, true, true, false, KeyVariant.Disabled)]
  public void Variant_resolution_prioritizes_locks_then_altgr_then_shift(bool altGr, bool shift, bool isLock, bool lockEnabled, KeyVariant expected)
  {
    Assert.Equal(expected, InputEventRules.ResolveVariant(altGr, shift, isLock, lockEnabled));
  }

  [Fact]
  public void Wheel_accumulates_partial_deltas_and_emits_each_detent()
  {
    var accumulator = new WheelAccumulator();
    Assert.Empty(accumulator.Add(40));
    Assert.Empty(accumulator.Add(40));
    Assert.Equal([1], accumulator.Add(40));
    Assert.Equal([-1, -1], accumulator.Add(-260));
    Assert.Equal(-20, accumulator.Remainder);
  }

  [Fact]
  public void Sequence_matches_within_timeout_and_resets_after_timeout()
  {
    var first = new ShortcutStep(true, false, false, false, 0x4B);
    var second = new ShortcutStep(true, false, false, false, 0x4D);
    var binding = new ShortcutBinding("command", "Command", ShortcutScope.Global, ShortcutKind.Sequence, [first, second]);
    var matcher = new ShortcutMatcher();

    Assert.Null(matcher.Process(first, 1000, [binding], 1200));
    Assert.Equal("command", matcher.Process(second, 2000, [binding], 1200));
    Assert.Null(matcher.Process(first, 3000, [binding], 1200));
    Assert.Null(matcher.Process(second, 4300, [binding], 1200));
  }

  [Fact]
  public void Shortcut_validator_rejects_duplicates_and_ambiguous_sequences()
  {
    var step = new ShortcutStep(true, true, false, false, 0x4B);
    var duplicate = new[]
    {
      new ShortcutBinding("one", "One", ShortcutScope.Global, ShortcutKind.Chord, [step]),
      new ShortcutBinding("two", "Two", ShortcutScope.Global, ShortcutKind.Chord, [step])
    };
    Assert.Contains("overlaps", ShortcutBindingValidator.Validate(duplicate));

    var sequences = new[]
    {
      new ShortcutBinding("one", "One", ShortcutScope.Global, ShortcutKind.Sequence, [step, step with { VirtualKey = 0x41 }]),
      new ShortcutBinding("two", "Two", ShortcutScope.Global, ShortcutKind.Sequence, [step, step with { VirtualKey = 0x42 }])
    };
    Assert.Contains("ambiguous", ShortcutBindingValidator.Validate(sequences));
  }

  [Fact]
  public void App_and_global_can_reuse_a_gesture_without_conflict()
  {
    var step = new ShortcutStep(true, true, false, false, 0x4B);
    var bindings = new[]
    {
      new ShortcutBinding("one", "One", ShortcutScope.Global, ShortcutKind.Chord, [step]),
      new ShortcutBinding("two", "Two", ShortcutScope.App, ShortcutKind.Chord, [step])
    };
    Assert.Null(ShortcutBindingValidator.Validate(bindings));
  }
}
