using KeyClick.Core;
using KeyClick.Infrastructure.Windows;
using NAudio.Wave;

namespace KeyClick.Tests;

public sealed class MappingTests
{
  private static readonly SoundPackDefinition Pack = BuiltInCatalog.Packs[0];
  private static readonly InputActionEvent Letter = new(
    new InputIdentity(InputKind.KeyboardKey, 30), 0x41, KeyVariant.Shift, InputGroup.Letters, InputPhase.Down, 42, "editor.exe");

  [Fact]
  public void Exact_override_wins_over_muted_group_and_builtin()
  {
    var resolver = new SoundMappingResolver();
    var settings = new AppSettings();
    var group = new GroupMapping(Pack.Id, InputGroup.Letters, KeyVariant.Shift, false, 0.5f, ["group"]);
    var input = new InputOverride(Pack.Id, Letter.Input.StableId, KeyVariant.Shift, true, 0.5f, ["exact"]);

    var result = resolver.Resolve(settings, Pack, Letter, group, input);

    Assert.True(result.Enabled);
    Assert.True(result.IsOverride);
    Assert.Equal(["exact"], result.SampleIds);
  }

  [Fact]
  public void Disabled_exact_override_mutes_only_that_variant()
  {
    var resolver = new SoundMappingResolver();
    var input = new InputOverride(Pack.Id, Letter.Input.StableId, KeyVariant.Shift, false, null, []);

    var result = resolver.Resolve(new AppSettings(), Pack, Letter, null, input);

    Assert.False(result.Enabled);
    Assert.Empty(result.SampleIds);
  }

  [Fact]
  public void Effective_volume_multiplies_every_layer()
  {
    var settings = new AppSettings { MasterVolume = 0.7f, KeyboardVolume = 0.5f };
    var group = new GroupMapping(Pack.Id, InputGroup.Letters, KeyVariant.Shift, true, 0.5f, ["group"]);
    var input = new InputOverride(Pack.Id, Letter.Input.StableId, KeyVariant.Shift, true, 0.5f, []);

    var result = new SoundMappingResolver().Resolve(settings, Pack, Letter, group, input, 0.8f);

    Assert.Equal(0.07, result.Gain, 3);
  }

  [Fact]
  public void App_exclusion_is_case_insensitive()
  {
    var settings = new AppSettings { ExcludedExecutables = ["EDITOR.EXE"] };
    var result = new SoundMappingResolver().Resolve(settings, Pack, Letter, null, null);
    Assert.False(result.Enabled);
  }

  [Fact]
  public void Physical_input_always_selects_the_same_sample()
  {
    var resolver = new SoundMappingResolver();
    var sound = new ResolvedSound(true, 1, ["one", "two", "three"], false);
    var selected = resolver.SelectStable(sound, Letter.Input.StableId);

    for (var index = 0; index < 100; index++) Assert.Equal(selected, resolver.SelectStable(sound, Letter.Input.StableId));
    Assert.True(Enumerable.Range(0, 32).Select(index => resolver.SelectStable(sound, $"key-{index}")).Distinct().Count() > 1);
  }

  [Fact]
  public void Every_builtin_group_uses_the_same_pool_for_every_modifier_variant()
  {
    foreach (var pack in BuiltInCatalog.Packs)
    foreach (var group in Enum.GetValues<InputGroup>())
    {
      var expected = pack.SamplesFor(group, KeyVariant.Base);
      Assert.NotEmpty(expected);
      foreach (var variant in Enum.GetValues<KeyVariant>()) Assert.Equal(expected, pack.SamplesFor(group, variant));
    }
  }

  [Fact]
  public void Cream_keys_assigns_specific_recordings_to_key_categories()
  {
    var cream = Assert.Single(BuiltInCatalog.Packs, pack => pack.Id == BuiltInCatalog.DefaultPackId);

    Assert.Single(cream.SamplesFor(InputGroup.Numbers, KeyVariant.Base));
    Assert.Single(cream.SamplesFor(InputGroup.Punctuation, KeyVariant.Base));
    Assert.Single(cream.SamplesFor(InputGroup.Modifiers, KeyVariant.Base));
    Assert.False(cream.SamplesFor(InputGroup.Numbers, KeyVariant.Base).SequenceEqual(cream.SamplesFor(InputGroup.Punctuation, KeyVariant.Base)));
    Assert.False(cream.SamplesFor(InputGroup.Modifiers, KeyVariant.Base).SequenceEqual(cream.SamplesFor(InputGroup.Navigation, KeyVariant.Base)));
  }

  [Fact]
  public void Catalog_contains_thirteen_unique_packs()
  {
    Assert.Equal(13, BuiltInCatalog.Packs.Count);
    Assert.Equal(13, BuiltInCatalog.Packs.Select(pack => pack.Id).Distinct().Count());
  }

  [Fact]
  public void Recorded_pack_samples_are_embedded_normalized_pcm()
  {
    var recordedPacks = BuiltInCatalog.Packs.Where(pack => !pack.IsCustom && pack.SamplePools is { Count: > 0 }).ToArray();
    Assert.Equal(3, recordedPacks.Length);
    var sampleIds = recordedPacks.SelectMany(pack => pack.AllSampleIds()).Distinct(StringComparer.Ordinal).ToArray();
    Assert.Equal(15, sampleIds.Length);

    var assembly = typeof(XAudio2SoundEngine).Assembly;
    var resources = assembly.GetManifestResourceNames();
    foreach (var sampleId in sampleIds)
    {
      var resourceName = Assert.Single(resources, name => name.EndsWith($".{sampleId}.wav", StringComparison.Ordinal));
      using var stream = Assert.IsAssignableFrom<Stream>(assembly.GetManifestResourceStream(resourceName));
      using var reader = new WaveFileReader(stream);
      Assert.Equal(48000, reader.WaveFormat.SampleRate);
      Assert.Equal(1, reader.WaveFormat.Channels);
      Assert.Equal(16, reader.WaveFormat.BitsPerSample);
      Assert.True(reader.TotalTime > TimeSpan.Zero);
      Assert.True(reader.TotalTime <= TimeSpan.FromMilliseconds(250));
    }
  }
}
