using System.Diagnostics;
using System.Globalization;

namespace KeyClick.Core;

public sealed class TypingChallengeSession
{
  private readonly Func<long> _timestamp;
  private readonly long _frequency;
  private readonly List<string> _target = [];
  private readonly List<string> _response = [];
  private readonly Dictionary<int, MutableSample> _samples = [];
  private long? _startedAt;
  private long? _pausedAt;
  private long _pausedTicks;
  private long _completedRetained;
  private long _completedWords;

  public TypingChallengeSession(TypingChallengeDefinition? definition, TypingChallengeRunMode runMode,
    TypingChallengeMistakeMode mistakeMode, int? durationLimitSeconds = null, Func<long>? timestamp = null, long? frequency = null)
  {
    Definition = definition;
    RunMode = runMode;
    MistakeMode = mistakeMode;
    DurationLimitSeconds = durationLimitSeconds;
    _timestamp = timestamp ?? Stopwatch.GetTimestamp;
    _frequency = frequency ?? Stopwatch.Frequency;
    SetTarget(definition?.Text ?? string.Empty);
  }

  public TypingChallengeDefinition? Definition { get; private set; }
  public TypingChallengeRunMode RunMode { get; }
  public TypingChallengeMistakeMode MistakeMode { get; }
  public int? DurationLimitSeconds { get; }
  public long CharacterAttempts { get; private set; }
  public long CorrectCharacters { get; private set; }
  public long ErrorAttempts { get; private set; }
  public long Corrections { get; private set; }
  public bool IsStarted => _startedAt is not null;
  public bool IsPaused => _pausedAt is not null;
  public bool ReferenceTextCompleted => _target.Count > 0 && _response.Count >= _target.Count;
  public string TargetText => string.Concat(_target);
  public string ResponseText => string.Concat(_response);
  public int CurrentPosition => _response.Count;
  public long RetainedCharacters => _completedRetained + _response.Count;
  public long Words => _completedWords + CountWords(ResponseText);
  public long ActiveMilliseconds => ElapsedMilliseconds(_timestamp());
  public bool TimeExpired => DurationLimitSeconds is not null && ActiveMilliseconds >= DurationLimitSeconds.Value * 1000L;
  public double GrossWordsPerMinute => Rate(CharacterAttempts);
  public double NetWordsPerMinute => Rate(CorrectCharacters);
  public double KeysPerMinute => ActiveMilliseconds <= 0 ? 0 : CharacterAttempts * 60000d / ActiveMilliseconds;
  public double AccuracyPercent => CharacterAttempts <= 0 ? 0 : CorrectCharacters * 100d / CharacterAttempts;

  public bool Input(string text)
  {
    if (IsPaused || string.IsNullOrEmpty(text) || TimeExpired) return false;
    StartIfNeeded();
    var accepted = false;
    var enumerator = StringInfo.GetTextElementEnumerator(text);
    while (enumerator.MoveNext())
    {
      var element = enumerator.GetTextElement();
      if (string.IsNullOrEmpty(element)) continue;
      CharacterAttempts++;
      var correct = Definition?.Source == TypingChallengeSource.FreeWriting || _target.Count == 0
        || _response.Count < _target.Count && string.Equals(_target[_response.Count], element, StringComparison.Ordinal);
      if (correct)
      {
        CorrectCharacters++;
        _response.Add(element);
        accepted = true;
      }
      else
      {
        ErrorAttempts++;
        if (MistakeMode == TypingChallengeMistakeMode.Flow)
        {
          _response.Add(element);
          accepted = true;
        }
      }
      AddSample(correct);
      if (ReferenceTextCompleted) break;
    }
    return accepted;
  }

  public bool Backspace()
  {
    if (IsPaused || _response.Count == 0) return false;
    _response.RemoveAt(_response.Count - 1);
    Corrections++;
    return true;
  }

  public void Pause()
  {
    if (!IsStarted || IsPaused) return;
    _pausedAt = _timestamp();
  }

  public void Resume()
  {
    if (_pausedAt is not { } paused) return;
    _pausedTicks += Math.Max(0, _timestamp() - paused);
    _pausedAt = null;
  }

  public void ContinueWith(TypingChallengeDefinition definition)
  {
    _completedRetained += _response.Count;
    _completedWords += CountWords(ResponseText);
    Definition = definition;
    _response.Clear();
    SetTarget(definition.Text);
  }

  public IReadOnlyList<TypingChallengeSample> CreateSamples()
  {
    var result = new List<TypingChallengeSample>();
    foreach (var item in _samples.OrderBy(value => value.Key))
    {
      var minutes = 5d / 60d;
      result.Add(new(item.Key, item.Value.Attempts, item.Value.Correct, item.Value.Errors, item.Value.Correct / 5d / minutes));
    }
    return result;
  }

  public double ConsistencyPercent
  {
    get
    {
      var rates = CreateSamples().Select(value => value.NetWordsPerMinute).Where(value => value > 0).ToArray();
      if (rates.Length < 2) return rates.Length == 1 ? 100 : 0;
      var average = rates.Average();
      var deviation = Math.Sqrt(rates.Select(value => Math.Pow(value - average, 2)).Average());
      return Math.Clamp(100d * (1d - deviation / average), 0, 100);
    }
  }

  private void StartIfNeeded() => _startedAt ??= _timestamp();

  private void AddSample(bool correct)
  {
    var index = (int)Math.Min(719, ActiveMilliseconds / 5000);
    if (!_samples.TryGetValue(index, out var sample)) _samples[index] = sample = new();
    sample.Attempts++;
    if (correct) sample.Correct++;
    else sample.Errors++;
  }

  private long ElapsedMilliseconds(long now)
  {
    if (_startedAt is not { } started) return 0;
    var end = _pausedAt ?? now;
    return Math.Max(0, (end - started - _pausedTicks) * 1000L / _frequency);
  }

  private double Rate(long characters) => ActiveMilliseconds <= 0 ? 0 : characters / 5d * 60000d / ActiveMilliseconds;

  private void SetTarget(string text)
  {
    _target.Clear();
    var enumerator = StringInfo.GetTextElementEnumerator(text);
    while (enumerator.MoveNext()) _target.Add(enumerator.GetTextElement());
  }

  private static long CountWords(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).LongLength;

  private sealed class MutableSample
  {
    public long Attempts;
    public long Correct;
    public long Errors;
  }
}
