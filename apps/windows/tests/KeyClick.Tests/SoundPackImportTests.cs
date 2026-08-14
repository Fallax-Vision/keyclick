using System.IO.Compression;
using KeyClick.Core;
using KeyClick.Infrastructure.Windows;
using NAudio.Wave;

namespace KeyClick.Tests;

public sealed class SoundPackImportTests
{
  [Fact]
  public async Task Import_validates_normalizes_persists_and_reloads_a_custom_pack()
  {
    using var folder = new TemporaryFolder();
    var wave = Path.Combine(folder.Path, "key.wav");
    WriteWave(wave, TimeSpan.FromMilliseconds(80));
    var archivePath = Path.Combine(folder.Path, "soft-pack.keyclickpack");
    using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
    {
      var manifest = archive.CreateEntry("pack.json");
      await using (var stream = manifest.Open())
      await using (var writer = new StreamWriter(stream))
        await writer.WriteAsync("""
          {
            "version": 1,
            "id": "soft-pack",
            "name": "Soft Pack",
            "family": "Personal",
            "description": "Quiet custom sounds.",
            "accent": "#7BE88B",
            "groups": { "letters": { "base": ["audio/key.wav"] } }
          }
          """);
      archive.CreateEntryFromFile(wave, "audio/key.wav");
    }

    var paths = new AppPaths(Path.Combine(folder.Path, "state"));
    var service = new SoundPackImportService(paths, new AudioImportService(paths));
    var pack = await service.ImportAsync(archivePath, true);

    Assert.True(pack.IsCustom);
    Assert.Equal("soft-pack", pack.Id);
    Assert.Single(pack.SamplesFor(InputGroup.Letters, KeyVariant.Base));
    Assert.Equal(pack.SamplesFor(InputGroup.Letters, KeyVariant.Base), pack.SamplesFor(InputGroup.Letters, KeyVariant.Shift));
    Assert.Equal(pack.SamplesFor(InputGroup.Letters, KeyVariant.Base), pack.SamplesFor(InputGroup.Numbers, KeyVariant.Base));
    Assert.True(File.Exists(Path.Combine(paths.Packs, "soft-pack.json")));

    var loaded = Assert.Single(await service.LoadInstalledAsync());
    Assert.Equal(pack.Id, loaded.Id);
    Assert.Equal(pack.SamplesFor(InputGroup.Letters, KeyVariant.Base), loaded.SamplesFor(InputGroup.Letters, KeyVariant.Base));
  }

  [Fact]
  public async Task Import_rejects_unsafe_audio_paths()
  {
    using var folder = new TemporaryFolder();
    var archivePath = Path.Combine(folder.Path, "unsafe.keyclickpack");
    using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
    {
      var manifest = archive.CreateEntry("pack.json");
      await using var stream = manifest.Open();
      await using var writer = new StreamWriter(stream);
      await writer.WriteAsync("""
        {
          "version": 1,
          "id": "unsafe-pack",
          "name": "Unsafe Pack",
          "groups": { "letters": { "base": ["../outside.wav"] } }
        }
        """);
    }

    var paths = new AppPaths(Path.Combine(folder.Path, "state"));
    var exception = await Assert.ThrowsAsync<SoundPackImportException>(() =>
      new SoundPackImportService(paths, new AudioImportService(paths)).ImportAsync(archivePath, true));
    Assert.Equal("SoundPackPathInvalidFormat", exception.ResourceKey);
  }

  private static void WriteWave(string path, TimeSpan duration)
  {
    using var writer = new WaveFileWriter(path, new WaveFormat(48_000, 16, 1));
    var samples = (int)(48_000 * duration.TotalSeconds);
    var buffer = new byte[samples * 2];
    for (var index = 0; index < samples; index++)
    {
      var sample = (short)(Math.Sin(index * Math.PI * 2 * 660 / 48_000) * short.MaxValue * 0.2);
      buffer[index * 2] = (byte)(sample & 0xFF);
      buffer[index * 2 + 1] = (byte)((sample >> 8) & 0xFF);
    }
    writer.Write(buffer);
  }

  private sealed class TemporaryFolder : IDisposable
  {
    public TemporaryFolder()
    {
      Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"KeyClick.PackTests.{Guid.NewGuid():N}");
      Directory.CreateDirectory(Path);
    }

    public string Path { get; }
    public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
  }
}
