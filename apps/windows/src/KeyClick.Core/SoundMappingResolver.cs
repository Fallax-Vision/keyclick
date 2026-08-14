namespace KeyClick.Core;

public sealed class SoundMappingResolver
{
  public ResolvedSound Resolve(
    AppSettings settings,
    SoundPackDefinition pack,
    InputActionEvent input,
    GroupMapping? groupMapping,
    InputOverride? inputOverride,
    float packVolume = 1.0f)
  {
    var categoryEnabled = input.Input.Kind switch
    {
      InputKind.KeyboardKey => settings.KeyboardEnabled,
      InputKind.PointerButton => settings.PointerEnabled,
      InputKind.Wheel => settings.PointerEnabled && settings.WheelEnabled,
      _ => false
    };

    var correctKeyboardPhase = input.Input.Kind != InputKind.KeyboardKey ||
      InputEventRules.ShouldPlayKeyboardSound(settings.KeyboardSoundTiming, input.Phase);
    var excluded = input.ForegroundExecutable is not null &&
      settings.ExcludedExecutables.Any(item => string.Equals(item, input.ForegroundExecutable, StringComparison.OrdinalIgnoreCase));

    if (!settings.SoundsEnabled || !categoryEnabled || !correctKeyboardPhase || excluded)
    {
      return new(false, 0, [], inputOverride is not null);
    }

    if (inputOverride is { Enabled: false })
    {
      return new(false, 0, [], true);
    }

    if (groupMapping is { Enabled: false } && inputOverride is null)
    {
      return new(false, 0, [], false);
    }

    var categoryVolume = input.Input.Kind == InputKind.KeyboardKey ? settings.KeyboardVolume : settings.PointerVolume;
    var groupVolume = groupMapping?.Volume ?? 1.0f;
    var inputVolume = inputOverride?.Volume ?? 1.0f;
    var samples = inputOverride?.SampleIds is { Count: > 0 }
      ? inputOverride.SampleIds
      : groupMapping?.SampleIds is { Count: > 0 }
        ? groupMapping.SampleIds
        : pack.SamplesFor(input.Group, input.Variant);
    var gain = Math.Clamp(settings.MasterVolume * categoryVolume * packVolume * groupVolume * inputVolume, 0, 1);
    return new(samples.Count > 0 && gain > 0, gain, samples, inputOverride is not null);
  }

  public string SelectStable(ResolvedSound sound, string identity)
  {
    if (sound.SampleIds.Count == 0)
    {
      throw new InvalidOperationException("The sound pool is empty.");
    }

    uint hash = 2166136261;
    foreach (var character in identity)
    {
      hash ^= character;
      hash *= 16777619;
    }
    return sound.SampleIds[(int)(hash % (uint)sound.SampleIds.Count)];
  }
}
