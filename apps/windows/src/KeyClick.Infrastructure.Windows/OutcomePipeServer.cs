using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using KeyClick.Core;

namespace KeyClick.Infrastructure.Windows;

public sealed class OutcomePipeServer : IAsyncDisposable
{
  public const int MaxPayloadBytes = 4096;
  private static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(5);
  private readonly Func<bool> _enabled;
  private readonly Func<string, bool> _clientAllowed;
  private readonly Action<IntegrationResultRequest> _accepted;
  private readonly CancellationTokenSource _stop = new();
  private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
  {
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
  };
  private readonly SlidingRateLimiter _rateLimiter = new(20);
  private Task? _serverTask;

  public OutcomePipeServer(Func<bool> enabled, Func<string, bool> clientAllowed, Action<IntegrationResultRequest> accepted)
  {
    _enabled = enabled;
    _clientAllowed = clientAllowed;
    _accepted = accepted;
    var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
    PipeName = $"KeyClick.ActionResult.{sid.Replace('-', '.')}.v1";
  }

  public string PipeName { get; }

  public void Start() => _serverTask ??= Task.Run(() => ListenAsync(_stop.Token));

  public async ValueTask DisposeAsync()
  {
    _stop.Cancel();
    if (_serverTask is not null)
    {
      try { await _serverTask.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
    }
    _stop.Dispose();
  }

  private async Task ListenAsync(CancellationToken cancellationToken)
  {
    while (!cancellationToken.IsCancellationRequested)
    {
      try
      {
        await using var pipe = new NamedPipeServerStream(
          PipeName,
          PipeDirection.InOut,
          1,
          PipeTransmissionMode.Byte,
          PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance,
          MaxPayloadBytes + 4,
          MaxPayloadBytes + 4);
        await pipe.WaitForConnectionAsync(cancellationToken);
        using var clientTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        clientTimeout.CancelAfter(ClientTimeout);
        var response = await ProcessClientAsync(pipe, clientTimeout.Token);
        var payload = JsonSerializer.SerializeToUtf8Bytes(response, _json);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await pipe.WriteAsync(header, clientTimeout.Token);
        await pipe.WriteAsync(payload, clientTimeout.Token);
        await pipe.FlushAsync(clientTimeout.Token);
      }
      catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
      catch (OperationCanceledException) { }
      catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
      {
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
      }
    }
  }

  private async Task<IntegrationResultResponse> ProcessClientAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
  {
    if (!_enabled()) return new(1, false, "integration-disabled");
    if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var clientProcessId))
      return new(1, false, "client-unavailable");

    string clientPath;
    try
    {
      using var process = Process.GetProcessById((int)clientProcessId);
      clientPath = process.MainModule?.FileName ?? string.Empty;
    }
    catch
    {
      return new(1, false, "client-unavailable");
    }
    if (!_clientAllowed(clientPath)) return new(1, false, "client-not-allowed");

    var header = new byte[4];
    if (!await ReadExactlyAsync(pipe, header, cancellationToken)) return new(1, false, "invalid-frame");
    var length = BinaryPrimitives.ReadInt32LittleEndian(header);
    if (length is <= 0 or > MaxPayloadBytes) return new(1, false, "payload-too-large");
    var bytes = new byte[length];
    if (!await ReadExactlyAsync(pipe, bytes, cancellationToken)) return new(1, false, "invalid-frame");

    IntegrationResultRequest? request;
    try { request = JsonSerializer.Deserialize<IntegrationResultRequest>(bytes, _json); }
    catch (JsonException) { return new(1, false, "invalid-json"); }
    var validationError = IntegrationRequestValidator.Validate(request);
    if (validationError is not null) return new(1, false, validationError);
    if (!WithinRateLimit()) return new(1, false, "rate-limited");
    if (request!.PlayResultSound) _accepted(request);
    return new(1, true);
  }

  private bool WithinRateLimit()
  {
    return _rateLimiter.TryAccept(Stopwatch.GetTimestamp(), Stopwatch.Frequency);
  }

  private static async Task<bool> ReadExactlyAsync(Stream stream, Memory<byte> bytes, CancellationToken cancellationToken)
  {
    var read = 0;
    while (read < bytes.Length)
    {
      var count = await stream.ReadAsync(bytes[read..], cancellationToken);
      if (count == 0) return false;
      read += count;
    }
    return true;
  }

  [DllImport("kernel32.dll", SetLastError = true)]
  private static extern bool GetNamedPipeClientProcessId(nint pipe, out uint clientProcessId);
}
