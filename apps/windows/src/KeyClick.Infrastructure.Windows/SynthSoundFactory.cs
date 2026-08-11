using KeyClick.Core;

namespace KeyClick.Infrastructure.Windows;

public static class SynthSoundFactory
{
  public const int SampleRate = 48_000;
  public const int BitsPerSample = 16;
  public const int Channels = 1;

  public static byte[] Generate(SoundPackDefinition pack, InputGroup group, KeyVariant variant, int variation)
  {
    var groupFrequency = group switch
    {
      InputGroup.Enter => 0.78f,
      InputGroup.Editing => 0.64f,
      InputGroup.Space => 0.58f,
      InputGroup.Modifiers => 0.84f,
      InputGroup.Numpad => 1.08f,
      InputGroup.Locks => variant == KeyVariant.Enabled ? 1.35f : 0.72f,
      InputGroup.PointerPrimary => 1.12f,
      InputGroup.PointerSecondary => 0.86f,
      InputGroup.PointerAuxiliary => 1.24f,
      InputGroup.Wheel => 1.42f,
      InputGroup.Outcomes => variant switch
      {
        KeyVariant.Enabled => 1.48f,
        KeyVariant.Disabled => 0.66f,
        _ => 1.0f
      },
      _ => 1.0f
    };
    var variantFrequency = variant switch
    {
      KeyVariant.Shift => 1.10f,
      KeyVariant.AltGr => 1.18f,
      _ => 1.0f
    };
    var duration = Math.Clamp(pack.Decay * (group == InputGroup.Space ? 1.35f : 1.0f), 0.012f, 0.11f);
    var count = Math.Max(128, (int)(SampleRate * duration));
    var samples = new byte[count * 2];
    var random = new Random(HashCode.Combine(pack.Id, group, variant, variation));
    var frequency = pack.BaseFrequency * groupFrequency * variantFrequency * (0.97f + variation * 0.025f);

    for (var i = 0; i < count; i++)
    {
      var t = (double)i / SampleRate;
      var envelope = Math.Exp(-t / Math.Max(0.003, duration * 0.24));
      var attack = Math.Min(1, i / 18.0);
      var body = Math.Sin(2 * Math.PI * frequency * t) * 0.52;
      body += Math.Sin(2 * Math.PI * frequency * 1.93 * t) * 0.21 * pack.Brightness;
      body += Math.Sin(2 * Math.PI * frequency * 0.51 * t) * 0.16 * (1 - pack.Brightness);
      var noise = (random.NextDouble() * 2 - 1) * pack.Noise * Math.Exp(-t / 0.008);
      if (pack.Id == "classic-typewriter" && i > count * 0.32)
      {
        noise += Math.Sin(2 * Math.PI * 4200 * t) * 0.09 * Math.Exp(-(t - duration * 0.32) / 0.012);
      }
      if (pack.Id == "digital-pulse")
      {
        body = Math.Sign(body) * 0.34 + Math.Sin(2 * Math.PI * frequency * 0.5 * t) * 0.14;
      }
      var value = Math.Clamp((body + noise) * envelope * attack * 0.62, -1, 1);
      var pcm = (short)(value * short.MaxValue);
      samples[i * 2] = (byte)(pcm & 0xFF);
      samples[i * 2 + 1] = (byte)((pcm >> 8) & 0xFF);
    }

    return samples;
  }
}
