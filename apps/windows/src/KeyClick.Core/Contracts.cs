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

public interface IStatisticsStore
{
  Task<string> GetStatisticsSourceIdAsync(CancellationToken cancellationToken = default);
  Task MergeStatisticsAsync(IReadOnlyCollection<StatisticsAggregateDelta> deltas, CancellationToken cancellationToken = default);
  Task MergeApplicationStatisticsAsync(IReadOnlyCollection<ApplicationStatisticsAggregateDelta> deltas, CancellationToken cancellationToken = default);
  Task<StatisticsSnapshot> QueryStatisticsAsync(StatisticsQuery query, CancellationToken cancellationToken = default);
  Task<IReadOnlyList<ApplicationStatisticsRow>> QueryApplicationStatisticsAsync(StatisticsQuery query, CancellationToken cancellationToken = default);
  Task DeleteStatisticsAsync(StatisticsDeleteRequest request, CancellationToken cancellationToken = default);
  Task<IReadOnlyList<WellnessAchievement>> LoadWellnessAchievementsAsync(CancellationToken cancellationToken = default);
  Task SaveWellnessAchievementAsync(WellnessAchievement achievement, CancellationToken cancellationToken = default);
  Task<StatisticsTransferBundle> ExportStatisticsAsync(bool includeWellness, CancellationToken cancellationToken = default);
  Task ImportStatisticsAsync(StatisticsTransferBundle bundle, bool includeWellness, CancellationToken cancellationToken = default);
}

public interface ITypingChallengeStore
{
  Task SaveTypingChallengeResultAsync(TypingChallengeResult result, CancellationToken cancellationToken = default);
  Task<IReadOnlyList<TypingChallengeResult>> QueryTypingChallengeResultsAsync(TypingChallengeQuery query, CancellationToken cancellationToken = default);
  Task DeleteTypingChallengeResultsAsync(TypingChallengeDeleteRequest request, CancellationToken cancellationToken = default);
  Task<IReadOnlyList<SavedTypingPrompt>> LoadSavedTypingPromptsAsync(CancellationToken cancellationToken = default);
  Task SaveTypingPromptAsync(SavedTypingPrompt prompt, CancellationToken cancellationToken = default);
  Task DeleteTypingPromptAsync(string promptId, CancellationToken cancellationToken = default);
  Task<IReadOnlyList<TypingChallengeAchievement>> LoadTypingChallengeAchievementsAsync(CancellationToken cancellationToken = default);
  Task SaveTypingChallengeAchievementAsync(TypingChallengeAchievement achievement, CancellationToken cancellationToken = default);
  Task<TypingChallengeTransferBundle> ExportTypingChallengesAsync(bool includeHistory, bool includePrompts, CancellationToken cancellationToken = default);
  Task ImportTypingChallengesAsync(TypingChallengeTransferBundle bundle, bool includeHistory, bool includePrompts, CancellationToken cancellationToken = default);
}

public readonly record struct StatisticsAggregateKey(
  DateTimeOffset BucketUtc,
  InputKind Kind,
  DeviceFamily DeviceFamily,
  int PhysicalCode,
  bool Extended,
  InputGroup Group);

public sealed record StatisticsAggregateDelta(
  StatisticsAggregateKey Key,
  long Count,
  long ActiveMilliseconds,
  long KeyboardActiveMilliseconds,
  long PointerActiveMilliseconds,
  int PeakTypingKeysPerMinute,
  int PeakClicksPerFiveSeconds,
  long Revision);

public readonly record struct ApplicationStatisticsAggregateKey(
  DateTimeOffset BucketUtc,
  string ApplicationId,
  string DisplayName);

public sealed record ApplicationStatisticsAggregateDelta(
  ApplicationStatisticsAggregateKey Key,
  long KeyboardPresses,
  long PointerClicks,
  long VerticalScroll,
  long HorizontalScroll,
  long Revision);

public interface ISoundEngine : IDisposable
{
  Task InitializeAsync(string outputDeviceId = "default", CancellationToken cancellationToken = default);
  Task ChangeOutputDeviceAsync(string outputDeviceId, CancellationToken cancellationToken = default);
  Task LoadPackAsync(SoundPackDefinition pack, IReadOnlyDictionary<string, string>? customSamplePaths = null, CancellationToken cancellationToken = default);
  Task LoadCustomSampleAsync(string sampleId, string wavPath, CancellationToken cancellationToken = default);
  bool TryPlay(SoundTrigger trigger);
  IReadOnlyList<AudioOutputDevice> OutputDevices { get; }
}

public interface IRawInputService : IDisposable
{
  event EventHandler<InputActionEvent>? InputAction;
  event EventHandler<InputDeviceDescriptor>? DeviceChanged;
  event Action<PointerMovementSignal>? PointerMoved;
  IReadOnlyList<InputDeviceDescriptor> EnumeratePointerDevices();
  void Start();
}

public interface IGlobalShortcutService : IDisposable
{
  event EventHandler<string>? CommandInvoked;
  bool ReplaceBindings(IEnumerable<ShortcutBinding> bindings, out string? error);
}
