namespace KeyClick.Core;

public interface IAppStore : IAsyncDisposable
{
  Task InitializeAsync(CancellationToken cancellationToken = default);
  Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default);
  Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default);
  Task<IReadOnlyList<InputOverride>> LoadOverridesAsync(string packId, CancellationToken cancellationToken = default);
  Task SaveOverrideAsync(InputOverride inputOverride, CancellationToken cancellationToken = default);
  Task RemoveOverrideAsync(string packId, string inputId, KeyVariant variant, CancellationToken cancellationToken = default);
  Task<IReadOnlyList<GroupMapping>> LoadGroupMappingsAsync(string packId, CancellationToken cancellationToken = default);
  Task SaveGroupMappingAsync(GroupMapping mapping, CancellationToken cancellationToken = default);
  Task RemoveGroupMappingAsync(string packId, InputGroup group, KeyVariant variant, DeviceFamily? deviceFamily, CancellationToken cancellationToken = default);
  Task<IReadOnlyList<ShortcutBinding>> LoadShortcutsAsync(CancellationToken cancellationToken = default);
  Task SaveShortcutAsync(ShortcutBinding binding, CancellationToken cancellationToken = default);
  Task CheckpointAsync(CancellationToken cancellationToken = default);
}

public interface ISoundEngine : IDisposable
{
  Task InitializeAsync(string outputDeviceId = "default", CancellationToken cancellationToken = default);
  Task ChangeOutputDeviceAsync(string outputDeviceId, CancellationToken cancellationToken = default);
  Task LoadPackAsync(SoundPackDefinition pack, CancellationToken cancellationToken = default);
  Task LoadCustomSampleAsync(string sampleId, string wavPath, CancellationToken cancellationToken = default);
  bool TryPlay(SoundTrigger trigger);
  IReadOnlyList<AudioOutputDevice> OutputDevices { get; }
}

public interface IRawInputService : IDisposable
{
  event EventHandler<InputReleaseEvent>? InputReleased;
  event EventHandler<string>? DeviceChanged;
  void Start();
}

public interface IGlobalShortcutService : IDisposable
{
  event EventHandler<string>? CommandInvoked;
  bool ReplaceBindings(IEnumerable<ShortcutBinding> bindings, out string? error);
}
