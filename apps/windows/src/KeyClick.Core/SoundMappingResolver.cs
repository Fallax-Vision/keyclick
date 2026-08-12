namespace KeyClick.Core;

public sealed class SoundMappingResolver
{
  private readonly Dictionary<string, int> _shuffleIndexes = new(StringComparer.Ordinal);

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

  public string SelectWithoutImmediateRepeat(ResolvedSound sound, string poolId)
  {
    if (sound.SampleIds.Count == 0)
    {
      throw new InvalidOperationException("The sound pool is empty.");
    }

    if (!_shuffleIndexes.TryGetValue(poolId, out var index))
    {
      index = Random.Shared.Next(sound.SampleIds.Count);
    }
    else
    {
      index = (index + 1 + Random.Shared.Next(Math.Max(1, sound.SampleIds.Count - 1))) % sound.SampleIds.Count;
    }

    _shuffleIndexes[poolId] = index;
    return sound.SampleIds[index];
  }
}
