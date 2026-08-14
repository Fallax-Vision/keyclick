using System.Runtime.InteropServices;
using System.Threading.Channels;
using KeyClick.Core;
using NAudio.CoreAudioApi;
using Vortice.Multimedia;
using Vortice.XAudio2;
using static Vortice.XAudio2.XAudio2;

namespace KeyClick.Infrastructure.Windows;

public sealed class XAudio2SoundEngine : ISoundEngine
{
  private const int VoiceCount = 32;
  private readonly Channel<SoundTrigger> _queue = Channel.CreateBounded<SoundTrigger>(new BoundedChannelOptions(256)
  {
    FullMode = BoundedChannelFullMode.DropOldest,
    SingleReader = true,
    SingleWriter = false
  });
  private readonly CancellationTokenSource _stop = new();
  private readonly Dictionary<string, PinnedSample> _samples = new(StringComparer.Ordinal);
  private readonly List<VoiceSlot> _voices = [];
  private Task? _worker;
  private IXAudio2? _engine;
  private IXAudio2MasteringVoice? _masteringVoice;
  private long _serial;

  public IReadOnlyList<AudioOutputDevice> OutputDevices { get; private set; } = [new("default", "System default")];

  public Task InitializeAsync(string outputDeviceId = "default", CancellationToken cancellationToken = default)
  {
    if (_engine is not null) return Task.CompletedTask;

    _engine = XAudio2Create(ProcessorSpecifier.DefaultProcessor, registerCallback: false);
    OutputDevices = EnumerateOutputs();
    _masteringVoice = CreateMasteringVoice(outputDeviceId);

    var format = new WaveFormat(SynthSoundFactory.SampleRate, SynthSoundFactory.BitsPerSample, SynthSoundFactory.Channels);
    for (var index = 0; index < VoiceCount; index++)
    {
      var voice = _engine.CreateSourceVoice(format, true);
      _voices.Add(new VoiceSlot(voice));
    }

    _engine.StartEngine();
    _worker = Task.Factory.StartNew(
      () => ProcessQueueAsync(_stop.Token),
      _stop.Token,
      TaskCreationOptions.LongRunning,
      TaskScheduler.Default).Unwrap();
    return Task.CompletedTask;
  }

  public Task ChangeOutputDeviceAsync(string outputDeviceId, CancellationToken cancellationToken = default)
  {
    cancellationToken.ThrowIfCancellationRequested();
    if (_engine is null) return InitializeAsync(outputDeviceId, cancellationToken);
    lock (_samples)
    {
      StopAndFlushVoices();
      _masteringVoice?.Dispose();
      _masteringVoice = CreateMasteringVoice(outputDeviceId);
    }
    return Task.CompletedTask;
  }

  public Task LoadPackAsync(SoundPackDefinition pack, IReadOnlyDictionary<string, string>? customSamplePaths = null, CancellationToken cancellationToken = default)
  {
    return Task.Run(() =>
    {
      var next = new Dictionary<string, PinnedSample>(StringComparer.Ordinal);
      var committed = false;
      try
      {
        if (!pack.IsCustom && pack.SamplePools is { Count: > 0 })
        {
          foreach (var sampleId in pack.AllSampleIds())
          {
            cancellationToken.ThrowIfCancellationRequested();
            next[sampleId] = ReadBuiltInSample(sampleId, cancellationToken);
          }
        }
        else if (!pack.IsCustom)
        {
          foreach (var group in Enum.GetValues<InputGroup>())
          {
            foreach (var variant in Enum.GetValues<KeyVariant>())
            {
              for (var variation = 1; variation <= 3; variation++)
              {
                cancellationToken.ThrowIfCancellationRequested();
                var id = $"{pack.Id}/{group.ToString().ToLowerInvariant()}-{variant.ToString().ToLowerInvariant()}-{variation}";
                next[id] = new PinnedSample(SynthSoundFactory.Generate(pack, group, variant, variation));
              }
            }
          }
        }

        if (customSamplePaths is not null)
        {
          foreach (var pair in customSamplePaths)
          {
            cancellationToken.ThrowIfCancellationRequested();
            next[pair.Key] = ReadNormalizedSample(pair.Value, cancellationToken);
          }
        }

        lock (_samples)
        {
          StopAndFlushVoices();
          foreach (var sample in _samples.Values) sample.Dispose();
          _samples.Clear();
          foreach (var pair in next) _samples.Add(pair.Key, pair.Value);
          committed = true;
        }
      }
      finally
      {
        if (!committed)
          foreach (var sample in next.Values) sample.Dispose();
      }
    }, cancellationToken);
  }

  public bool TryPlay(SoundTrigger trigger) => _queue.Writer.TryWrite(trigger);

  public Task LoadCustomSampleAsync(string sampleId, string wavPath, CancellationToken cancellationToken = default)
  {
    return Task.Run(() =>
    {
      var sample = ReadNormalizedSample(wavPath, cancellationToken);
      lock (_samples)
      {
        if (_samples.Remove(sampleId, out var existing)) existing.Dispose();
        _samples[sampleId] = sample;
      }
    }, cancellationToken);
  }

  private static PinnedSample ReadNormalizedSample(string wavPath, CancellationToken cancellationToken)
  {
    using var reader = new NAudio.Wave.WaveFileReader(wavPath);
    return ReadNormalizedSample(reader, cancellationToken);
  }

  private static PinnedSample ReadBuiltInSample(string sampleId, CancellationToken cancellationToken)
  {
    var suffix = $".{sampleId}.wav";
    var assembly = typeof(XAudio2SoundEngine).Assembly;
    var resourceName = assembly.GetManifestResourceNames().SingleOrDefault(name => name.EndsWith(suffix, StringComparison.Ordinal))
      ?? throw new InvalidDataException($"Built-in sound sample '{sampleId}' is missing.");
    using var stream = assembly.GetManifestResourceStream(resourceName)
      ?? throw new InvalidDataException($"Built-in sound sample '{sampleId}' cannot be opened.");
    using var reader = new NAudio.Wave.WaveFileReader(stream);
    return ReadNormalizedSample(reader, cancellationToken);
  }

  private static PinnedSample ReadNormalizedSample(NAudio.Wave.WaveFileReader reader, CancellationToken cancellationToken)
  {
    if (reader.WaveFormat.SampleRate != SynthSoundFactory.SampleRate ||
        reader.WaveFormat.BitsPerSample != SynthSoundFactory.BitsPerSample ||
        reader.WaveFormat.Channels != SynthSoundFactory.Channels)
      throw new InvalidDataException("Custom sounds must use KeyClick's normalized 48 kHz mono PCM format.");
    var bytes = new byte[reader.Length];
    var read = 0;
    while (read < bytes.Length)
    {
      cancellationToken.ThrowIfCancellationRequested();
      var count = reader.Read(bytes, read, bytes.Length - read);
      if (count == 0) break;
      read += count;
    }
    return new PinnedSample(bytes);
  }

  public void Dispose()
  {
    _stop.Cancel();
    _queue.Writer.TryComplete();
    try { _worker?.Wait(TimeSpan.FromSeconds(1)); } catch { }
    StopAndFlushVoices();
    foreach (var voice in _voices) voice.Voice.Dispose();
    _voices.Clear();
    lock (_samples)
    {
      foreach (var sample in _samples.Values) sample.Dispose();
      _samples.Clear();
    }
    _masteringVoice?.Dispose();
    _engine?.StopEngine();
    _engine?.Dispose();
    _stop.Dispose();
  }

  private async Task ProcessQueueAsync(CancellationToken cancellationToken)
  {
    try
    {
      await foreach (var trigger in _queue.Reader.ReadAllAsync(cancellationToken))
      {
        PlayNow(trigger);
      }
    }
    catch (OperationCanceledException) { }
  }

  private void PlayNow(SoundTrigger trigger)
  {
    PinnedSample sample;
    lock (_samples)
    {
      if (!_samples.TryGetValue(trigger.SampleId, out sample!)) return;
      var slot = _voices.FirstOrDefault(item => item.Voice.State.BuffersQueued == 0)
        ?? _voices.MinBy(item => item.Serial)!;
      slot.Voice.Stop();
      slot.Voice.FlushSourceBuffers();
      slot.Voice.SetVolume(Math.Clamp(trigger.Gain, 0, 1));
      slot.Voice.SubmitSourceBuffer(
        new AudioBuffer(sample.Pointer, (uint)sample.Length, BufferFlags.EndOfStream),
        null);
      slot.Serial = Interlocked.Increment(ref _serial);
      slot.Voice.Start();
    }
  }

  private void StopAndFlushVoices()
  {
    foreach (var slot in _voices)
    {
      slot.Voice.Stop();
      slot.Voice.FlushSourceBuffers();
    }
  }

  private IXAudio2MasteringVoice CreateMasteringVoice(string outputDeviceId) =>
    outputDeviceId == "default"
      ? _engine!.CreateMasteringVoice()
      : _engine!.CreateMasteringVoice(0, 0, 0, outputDeviceId);

  private static IReadOnlyList<AudioOutputDevice> EnumerateOutputs()
  {
    var outputs = new List<AudioOutputDevice> { new("default", "System default") };
    try
    {
      using var enumerator = new MMDeviceEnumerator();
      foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
      {
        outputs.Add(new AudioOutputDevice(device.ID, device.FriendlyName));
        device.Dispose();
      }
    }
    catch { }
    return outputs;
  }

  private sealed class VoiceSlot(IXAudio2SourceVoice voice)
  {
    public IXAudio2SourceVoice Voice { get; } = voice;
    public long Serial { get; set; }
  }

  private sealed class PinnedSample : IDisposable
  {
    private GCHandle _handle;

    public PinnedSample(byte[] bytes)
    {
      Bytes = bytes;
      _handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
    }

    public byte[] Bytes { get; }
    public nint Pointer => _handle.AddrOfPinnedObject();
    public int Length => Bytes.Length;

    public void Dispose()
    {
      if (_handle.IsAllocated) _handle.Free();
    }
  }
}
