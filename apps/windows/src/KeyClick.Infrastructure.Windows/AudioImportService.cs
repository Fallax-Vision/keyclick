using System.Security.Cryptography;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace KeyClick.Infrastructure.Windows;

public sealed record ImportedSound(string Id, string Path, TimeSpan Duration, long SourceBytes, float SourcePeak);

public sealed class AudioImportService(AppPaths paths)
{
  public const long MaxFileBytes = 20 * 1024 * 1024;
  public const int MaxDecodedSamples = SynthSoundFactory.SampleRate * 5;
  public static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(5);
  private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase) { ".wav", ".mp3", ".ogg" };

  public async Task<ImportedSound> ImportAsync(string sourcePath, bool normalize, CancellationToken cancellationToken = default)
  {
    var source = new FileInfo(sourcePath);
    if (!source.Exists) throw new FileNotFoundException("The selected sound file no longer exists.", sourcePath);
    if (!AllowedExtensions.Contains(source.Extension)) throw new InvalidDataException("Choose a WAV, MP3, or OGG audio file.");
    if (source.Length > MaxFileBytes) throw new InvalidDataException("Sound files must be 20 MB or smaller.");
    paths.EnsureCreated();

    return await Task.Run(() =>
    {
      cancellationToken.ThrowIfCancellationRequested();
      using WaveStream reader = source.Extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase)
        ? new VorbisWaveReader(source.FullName)
        : new AudioFileReader(source.FullName);
      if (reader.TotalTime < TimeSpan.Zero || reader.TotalTime > MaxDuration) throw new InvalidDataException("Sound clips must be five seconds or shorter.");
      if (reader.WaveFormat.Channels is < 1 or > 2) throw new InvalidDataException("Only mono and stereo sound clips are supported.");

      ISampleProvider samples = reader.ToSampleProvider();
      if (samples.WaveFormat.Channels == 2)
        samples = new StereoToMonoSampleProvider(samples) { LeftVolume = 0.5f, RightVolume = 0.5f };
      if (samples.WaveFormat.SampleRate != SynthSoundFactory.SampleRate)
        samples = new WdlResamplingSampleProvider(samples, SynthSoundFactory.SampleRate);

      var values = new List<float>((int)(SynthSoundFactory.SampleRate * reader.TotalTime.TotalSeconds));
      var buffer = new float[4096];
      float peak = 0;
      int read;
      while ((read = samples.Read(buffer, 0, buffer.Length)) > 0)
      {
        cancellationToken.ThrowIfCancellationRequested();
        if (read > MaxDecodedSamples - values.Count)
          throw new InvalidDataException("Sound clips must decode to five seconds or shorter.");
        for (var index = 0; index < read; index++)
        {
          peak = Math.Max(peak, Math.Abs(buffer[index]));
          values.Add(buffer[index]);
        }
      }

      var scale = normalize && peak > 0.0001f ? Math.Min(1.0f, 0.90f / peak) : 1.0f;
      var temporary = Path.Combine(paths.Sounds, $"import-{Guid.NewGuid():N}.wav");
      try
      {
        using (var writer = new WaveFileWriter(temporary, new WaveFormat(SynthSoundFactory.SampleRate, 16, 1)))
        {
          var pcm = new byte[values.Count * 2];
          for (var index = 0; index < values.Count; index++)
          {
            var value = (short)(Math.Clamp(values[index] * scale, -1, 1) * short.MaxValue);
            pcm[index * 2] = (byte)(value & 0xFF);
            pcm[index * 2 + 1] = (byte)((value >> 8) & 0xFF);
          }
          writer.Write(pcm, 0, pcm.Length);
        }

        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(temporary))).ToLowerInvariant();
        var destination = Path.Combine(paths.Sounds, $"{hash}.wav");
        if (File.Exists(destination)) File.Delete(temporary);
        else File.Move(temporary, destination);
        return new ImportedSound(hash, destination, TimeSpan.FromSeconds((double)values.Count / SynthSoundFactory.SampleRate), source.Length, peak);
      }
      finally
      {
        if (File.Exists(temporary)) File.Delete(temporary);
      }
    }, cancellationToken);
  }
}
