namespace KeyClick.Core;

public sealed class ShortcutMatcher
{
  private ShortcutBinding? _pending;
  private int _nextStep;
  private long _expiresAt;

  public string? Process(ShortcutStep step, long timestampMs, IEnumerable<ShortcutBinding> bindings, int timeoutMs)
  {
    if (_pending is not null && timestampMs <= _expiresAt)
    {
      var expected = _pending.Steps[_nextStep];
      if (expected == step)
      {
        _nextStep++;
        if (_nextStep == _pending.Steps.Count)
        {
          var command = _pending.CommandId;
          Reset();
          return command;
        }
        _expiresAt = timestampMs + timeoutMs;
        return null;
      }
      Reset();
    }

    foreach (var binding in bindings.Where(item => item.Enabled))
    {
      if (binding.Steps.Count == 0 || binding.Steps[0] != step)
      {
        continue;
      }

      if (binding.Steps.Count == 1)
      {
        return binding.CommandId;
      }

      _pending = binding;
      _nextStep = 1;
      _expiresAt = timestampMs + timeoutMs;
      return null;
    }

    return null;
  }

  public void Reset()
  {
    _pending = null;
    _nextStep = 0;
    _expiresAt = 0;
  }
}
