using KeyClick.Infrastructure.Windows;
using NAudio.Wave;

namespace KeyClick.Tests;

public sealed class AudioImportTests
{
  [Fact]
  public async Task Import_decodes_normalizes_and_deduplicates_short_audio()
  {
    using var folder = new TemporaryFolder();
    var source = System.IO.Path.Combine(folder.Path, "click.wav");
    WriteWave(source, TimeSpan.FromMilliseconds(80));
    var importer = new AudioImportService(new AppPaths(System.IO.Path.Combine(folder.Path, "state")));

    var first = await importer.ImportAsync(source, true);
    var second = await importer.ImportAsync(source, true);

    Assert.Equal(first.Id, second.Id);
    Assert.Equal(first.Path, second.Path);
    Assert.True(File.Exists(first.Path));
    using var reader = new WaveFileReader(first.Path);
    Assert.Equal(48_000, reader.WaveFormat.SampleRate);
    Assert.Equal(1, reader.WaveFormat.Channels);
    Assert.Equal(16, reader.WaveFormat.BitsPerSample);
  }

  [Fact]
  public async Task Import_rejects_duration_and_size_limits_before_persistence()
  {
    using var folder = new TemporaryFolder();
    var importer = new AudioImportService(new AppPaths(System.IO.Path.Combine(folder.Path, "state")));
    var longWave = System.IO.Path.Combine(folder.Path, "long.wav");
    WriteWave(longWave, TimeSpan.FromSeconds(5.1));
    await Assert.ThrowsAsync<InvalidDataException>(() => importer.ImportAsync(longWave, false));

    var huge = System.IO.Path.Combine(folder.Path, "huge.wav");
    await using (var stream = new FileStream(huge, FileMode.CreateNew, FileAccess.Write)) stream.SetLength(AudioImportService.MaxFileBytes + 1);
    await Assert.ThrowsAsync<InvalidDataException>(() => importer.ImportAsync(huge, false));
  }

  [Fact]
  public async Task Import_accepts_exact_decoded_sample_budget()
  {
    using var folder = new TemporaryFolder();
    var source = System.IO.Path.Combine(folder.Path, "boundary.wav");
    WriteWave(source, TimeSpan.FromSeconds(5));
    var importer = new AudioImportService(new AppPaths(System.IO.Path.Combine(folder.Path, "state")));

    var imported = await importer.ImportAsync(source, false);

    Assert.Equal(AudioImportService.MaxDecodedSamples, (int)Math.Round(imported.Duration.TotalSeconds * 48_000));
    Assert.DoesNotContain(Directory.EnumerateFiles(System.IO.Path.Combine(folder.Path, "state", "media", "sounds")),
      path => System.IO.Path.GetFileName(path).StartsWith("import-", StringComparison.OrdinalIgnoreCase));
  }

  private static void WriteWave(string path, TimeSpan duration)
  {
    using var writer = new WaveFileWriter(path, new WaveFormat(48_000, 16, 1));
    var samples = (int)(48_000 * duration.TotalSeconds);
    var buffer = new byte[samples * 2];
    for (var index = 0; index < samples; index++)
    {
      var sample = (short)(Math.Sin(index * Math.PI * 2 * 880 / 48_000) * short.MaxValue * 0.2);
      buffer[index * 2] = (byte)(sample & 0xFF);
      buffer[index * 2 + 1] = (byte)((sample >> 8) & 0xFF);
    }
    writer.Write(buffer);
  }

  private sealed class TemporaryFolder : IDisposable
  {
    public TemporaryFolder()
    {
      Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"KeyClick.AudioTests.{Guid.NewGuid():N}");
      Directory.CreateDirectory(Path);
    }

    public string Path { get; }
    public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
  }
}
