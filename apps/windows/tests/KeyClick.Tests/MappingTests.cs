using KeyClick.Core;

namespace KeyClick.Tests;

public sealed class MappingTests
{
  private static readonly SoundPackDefinition Pack = BuiltInCatalog.Packs[0];
  private static readonly InputReleaseEvent Letter = new(
    new InputIdentity(InputKind.KeyboardKey, 30), 0x41, KeyVariant.Shift, InputGroup.Letters, 42, "editor.exe");

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
  public void Shuffle_pool_does_not_repeat_immediately()
  {
    var resolver = new SoundMappingResolver();
    var sound = new ResolvedSound(true, 1, ["one", "two", "three"], false);
    var previous = resolver.SelectWithoutImmediateRepeat(sound, "pool");
    for (var index = 0; index < 100; index++)
    {
      var current = resolver.SelectWithoutImmediateRepeat(sound, "pool");
      Assert.NotEqual(previous, current);
      previous = current;
    }
  }

  [Fact]
  public void Catalog_contains_the_ten_unique_v1_packs()
  {
    Assert.Equal(10, BuiltInCatalog.Packs.Count);
    Assert.Equal(10, BuiltInCatalog.Packs.Select(pack => pack.Id).Distinct().Count());
  }
}
