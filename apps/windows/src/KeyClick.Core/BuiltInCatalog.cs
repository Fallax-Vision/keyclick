namespace KeyClick.Core;

public static class BuiltInCatalog
{
  public const string DefaultPackId = "cream-keys";

  public static IReadOnlyList<SoundPackDefinition> Packs { get; } =
  [
    new("cream-keys", "Cream Keys", "Keyboard focused", "Soft, polished cream-switch taps captured from Cloudflare Pay's name field.", 0, 0, 0, 0, "#F6821F", false,
      CategoryAssetPools("cream-keys-1", "cream-keys-2", "cream-keys-3", "cream-keys-4", "cream-keys-5", "cream-keys-401")),
    new("bright-mechanical", "Bright Mechanical", "Keyboard focused", "A lively mechanical keyboard recording with six short key variants.", 0, 0, 0, 0, "#5C7CFA", false,
      CategoryAssetPools("pixabay-mechanical-1", "pixabay-mechanical-2", "pixabay-mechanical-3", "pixabay-mechanical-4", "pixabay-mechanical-5", "pixabay-mechanical-6")),
    new("compact-mech-tap", "Compact Mech Tap", "Keyboard focused", "A short, dry mechanical tap in three subtle tonal variants.", 0, 0, 0, 0, "#9B7EDE", false,
      CategoryAssetPools("pixabay-mech-tap-1", "pixabay-mech-tap-1", "pixabay-mech-tap-2", "pixabay-mech-tap-2", "pixabay-mech-tap-3", "pixabay-mech-tap-3")),
    new("clicky-switch", "Clicky Switch", "Keyboard focused", "Bright, precise switch clicks.", 2480, 0.34f, 0.026f, 0.94f, "#35E04B"),
    new("tactile-switch", "Tactile Switch", "Keyboard focused", "Rounded tactile bumps with a firm return.", 1760, 0.28f, 0.036f, 0.74f, "#64D978"),
    new("linear-switch", "Linear Switch", "Keyboard focused", "Clean linear clacks with minimal grit.", 1280, 0.18f, 0.030f, 0.62f, "#E05555"),
    new("buckling-spring", "Buckling Spring", "Keyboard focused", "Metallic, energetic spring snaps.", 3140, 0.48f, 0.055f, 1.00f, "#66A7FF"),
    new("silent-switch", "Silent Switch", "Keyboard focused", "Muted office-friendly switch returns.", 720, 0.12f, 0.024f, 0.42f, "#9DA6AF"),
    new("crisp-mechanical", "Crisp Mechanical", "Balanced", "Short aluminum-bodied mechanical ticks.", 2240, 0.24f, 0.022f, 0.88f, "#A5F2B0"),
    new("soft-thock", "Soft Thock", "Balanced", "Deep damped thocks with a soft tail.", 520, 0.16f, 0.064f, 0.34f, "#D6A56B"),
    new("classic-typewriter", "Classic Typewriter", "Balanced", "Layered typebar and carriage-inspired taps.", 1980, 0.52f, 0.072f, 0.82f, "#E3D4B5"),
    new("minimal-tap", "Minimal Tap", "Balanced", "Tiny scissor-key taps for quiet work.", 1060, 0.08f, 0.016f, 0.54f, "#B7C1CC"),
    new("digital-pulse", "Digital Pulse", "Balanced", "Compact electronic pulses and confirmations.", 880, 0.03f, 0.045f, 0.96f, "#55C7FF")
  ];

  public static IReadOnlyList<ShortcutBinding> DefaultShortcuts { get; } =
  [
    new("show-hide", "Show or hide KeyClick", ShortcutScope.Global, ShortcutKind.Chord, [new(true, true, false, false, 0x4B)]),
    new("toggle-sounds", "Toggle all sounds", ShortcutScope.Global, ShortcutKind.Chord, [new(true, true, false, false, 0x4D)]),
    new("previous-pack", "Previous sound pack", ShortcutScope.Global, ShortcutKind.Chord, [new(true, true, false, false, 0x21)]),
    new("next-pack", "Next sound pack", ShortcutScope.Global, ShortcutKind.Chord, [new(true, true, false, false, 0x22)])
  ];

  public static IEnumerable<string> SamplesFor(string packId, InputGroup group, KeyVariant variant)
  {
    var groupId = group.ToString().ToLowerInvariant();
    yield return $"{packId}/{groupId}-base-1";
    yield return $"{packId}/{groupId}-base-2";
    yield return $"{packId}/{groupId}-base-3";
  }

  private static Dictionary<string, string[]> CategoryAssetPools(
    string letter, string number, string punctuation, string modifier, string navigation, string action)
  {
    var pools = new Dictionary<InputGroup, string[]>
    {
      [InputGroup.Letters] = [letter, number, punctuation],
      [InputGroup.Numbers] = [number],
      [InputGroup.Punctuation] = [punctuation],
      [InputGroup.Modifiers] = [modifier],
      [InputGroup.Navigation] = [navigation],
      [InputGroup.FunctionAndMedia] = [modifier],
      [InputGroup.Numpad] = [number],
      [InputGroup.Locks] = [modifier],
      [InputGroup.Space] = [action],
      [InputGroup.Enter] = [action],
      [InputGroup.Editing] = [navigation],
      [InputGroup.PointerPrimary] = [letter],
      [InputGroup.PointerSecondary] = [number],
      [InputGroup.PointerAuxiliary] = [punctuation],
      [InputGroup.Wheel] = [navigation],
      [InputGroup.Outcomes] = [action]
    };
    return Enum.GetValues<InputGroup>()
      .SelectMany(group => Enum.GetValues<KeyVariant>().Select(variant => (Key: SoundPackDefinition.PoolKey(group, variant), Pool: pools[group])))
      .ToDictionary(item => item.Key, item => item.Pool, StringComparer.Ordinal);
  }

}
