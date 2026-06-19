using ECommons.DalamudServices;
using ECommons.ExcelServices;
using ECommons.GameHelpers;
using ECommons.Logging;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Lumina.Excel.Sheets;
using RotationSolver.Basic.Data;
using RotationSolver.Basic.Helpers;

namespace RotationSolver.Basic.TimelineProfiles;

public static class ImportedTimelineManager
{
	private const string ProfileFormat = "RotationSolverTimelineProfile";
	private static Dictionary<string, ImportedTimelineProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);

	public readonly record struct TimelineProfileAssignment(uint TerritoryType, Job Job, CombatType CombatType, string ProfileId);

	public static string ProfilesDirectory
	{
		get
		{
			var path = Path.Combine(Svc.PluginInterface.ConfigDirectory.FullName, "ImportedTimelines");
			Directory.CreateDirectory(path);
			return path;
		}
	}

	public static ImportedTimelineProfile[] Profiles
		=> [.. _profiles.Values.OrderBy(profile => profile.ProfileName, StringComparer.OrdinalIgnoreCase)];

	public static void LoadProfiles()
	{
		var loaded = new Dictionary<string, ImportedTimelineProfile>(StringComparer.OrdinalIgnoreCase);
		var loadedSourcePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var filePaths = Directory.GetFiles(ProfilesDirectory, "*.json", SearchOption.TopDirectoryOnly);
		Array.Sort(filePaths, StringComparer.OrdinalIgnoreCase);

		foreach (var filePath in filePaths)
		{
			try
			{
				var profile = ReadProfile(filePath);
				if (loadedSourcePaths.TryGetValue(profile.ProfileId, out var existingPath))
				{
					PluginLog.Warning($"Duplicate imported timeline profile id '{profile.ProfileId}'. '{filePath}' overrides '{existingPath}'.");
				}

				loaded[profile.ProfileId] = profile;
				loadedSourcePaths[profile.ProfileId] = filePath;
			}
			catch (Exception ex)
			{
				PluginLog.Warning($"Failed to load imported timeline profile '{filePath}': {ex.Message}");
			}
		}

		_profiles = loaded;
	}

	public static ImportedTimelineProfile ImportProfile(string filePath)
	{
		if (string.IsNullOrWhiteSpace(filePath))
		{
			throw new ArgumentException("Import path is empty.", nameof(filePath));
		}

		var normalizedPath = filePath.Trim().Trim('"');
		if (!File.Exists(normalizedPath))
		{
			throw new FileNotFoundException("Timeline profile file was not found.", normalizedPath);
		}

		var profile = ReadProfile(normalizedPath);
		return SaveImportedProfile(profile);
	}

	public static ImportedTimelineProfile ImportProfileFromText(string jsonText)
	{
		if (string.IsNullOrWhiteSpace(jsonText))
		{
			throw new ArgumentException("Timeline profile JSON is empty.", nameof(jsonText));
		}

		var profile = ReadProfileText(jsonText, "clipboard");
		return SaveImportedProfile(profile);
	}

	private static ImportedTimelineProfile SaveImportedProfile(ImportedTimelineProfile profile)
	{
		var outputPath = Path.Combine(ProfilesDirectory, $"{MakeSafeFileName(profile.ProfileId)}.json");
		File.WriteAllText(outputPath, JsonConvert.SerializeObject(profile, Formatting.Indented));

		_profiles[profile.ProfileId] = profile;
		return profile;
	}

	public static bool TryGetProfile(string? profileId, out ImportedTimelineProfile? profile)
	{
		if (!string.IsNullOrWhiteSpace(profileId) && _profiles.TryGetValue(profileId, out var loadedProfile))
		{
			profile = loadedProfile;
			return true;
		}

		profile = null;
		return false;
	}

	public static bool TryGetDutyTimelineProfileChoice(uint territoryType, Job job, CombatType combatType, out string? profileId)
	{
		if (Service.Config.DutyTimelineProfileChoice == null)
		{
			profileId = null;
			return false;
		}

		var key = GetDutyTimelineProfileChoiceKey(territoryType, job, combatType);
		if (territoryType != 0
			&& Service.Config.DutyTimelineProfileChoice.TryGetValue(key, out var value)
			&& !string.IsNullOrWhiteSpace(value))
		{
			profileId = value;
			return true;
		}

		profileId = null;
		return false;
	}

	public static bool TryGetAssignedProfile(uint territoryType, Job job, CombatType combatType, out ImportedTimelineProfile? profile)
	{
		if (TryGetDutyTimelineProfileChoice(territoryType, job, combatType, out var profileId)
			&& TryGetProfile(profileId, out profile))
		{
			return true;
		}

		profile = null;
		return false;
	}

	public static void SetDutyTimelineProfileChoice(uint territoryType, Job job, CombatType combatType, string? profileId)
	{
		if (territoryType == 0)
		{
			return;
		}

		var key = GetDutyTimelineProfileChoiceKey(territoryType, job, combatType);
		Service.Config.DutyTimelineProfileChoice ??= [];
		Service.Config.DutyTimelineProfileChoice.TryGetValue(key, out var previousProfileId);
		if (string.IsNullOrWhiteSpace(profileId))
		{
			Service.Config.DutyTimelineProfileChoice.TryRemove(key, out _);
		}
		else
		{
			Service.Config.DutyTimelineProfileChoice[key] = profileId;
		}

		if (!string.Equals(previousProfileId, profileId, StringComparison.OrdinalIgnoreCase)
			&& IsCurrentRuntimeAssignment(territoryType, job, combatType))
		{
			ImportedTimelineRuntime.Reset();
			ActionTracer.Note($"Timeline assignment changed territory={territoryType} job={job} combat={combatType} profile='{profileId ?? "Disabled"}'");
		}
	}

	private static bool IsCurrentRuntimeAssignment(uint territoryType, Job job, CombatType combatType)
	{
		var currentCombatType = DataCenter.IsPvP ? CombatType.PvP : CombatType.PvE;
		if (job != DataCenter.Job || combatType != currentCombatType)
		{
			return false;
		}

		if (DataCenter.IsInDuty && Svc.ClientState.TerritoryType != 0)
		{
			return territoryType == Svc.ClientState.TerritoryType;
		}

		return !DataCenter.IsPvP
			&& Service.Config.DutyTimelineProfileTestTerritory != 0
			&& territoryType == Service.Config.DutyTimelineProfileTestTerritory;
	}

	public static uint[] GetDutyTimelineProfileTerritories(Job job, CombatType combatType)
	{
		SortedSet<uint> territories = [];
		if (Service.Config.DutyTimelineProfileChoice == null)
		{
			return [];
		}

		foreach (var pair in Service.Config.DutyTimelineProfileChoice)
		{
			if (!string.IsNullOrWhiteSpace(pair.Value)
				&& TryParseDutyTimelineProfileChoiceKey(pair.Key, out var territoryType, out var keyJob, out var keyCombatType)
				&& keyJob == job
				&& keyCombatType == combatType)
			{
				territories.Add(territoryType);
			}
		}

		return [.. territories];
	}

	public static TimelineProfileAssignment[] GetAssignments(string profileId)
	{
		if (string.IsNullOrWhiteSpace(profileId) || Service.Config.DutyTimelineProfileChoice == null)
		{
			return [];
		}

		List<TimelineProfileAssignment> assignments = [];
		foreach (var pair in Service.Config.DutyTimelineProfileChoice)
		{
			if (string.Equals(pair.Value, profileId, StringComparison.OrdinalIgnoreCase)
				&& TryParseDutyTimelineProfileChoiceKey(pair.Key, out var territoryType, out var job, out var combatType))
			{
				assignments.Add(new TimelineProfileAssignment(territoryType, job, combatType, profileId));
			}
		}

		return [.. assignments
			.OrderBy(assignment => assignment.CombatType)
			.ThenBy(assignment => assignment.Job)
			.ThenBy(assignment => assignment.TerritoryType)];
	}

	public static bool DeleteProfile(string profileId)
	{
		if (string.IsNullOrWhiteSpace(profileId))
		{
			return false;
		}

		if (!_profiles.Remove(profileId, out _))
		{
			return false;
		}

		if (Service.Config.DutyTimelineProfileChoice != null)
		{
			foreach (var pair in Service.Config.DutyTimelineProfileChoice.Where(pair => string.Equals(pair.Value, profileId, StringComparison.OrdinalIgnoreCase)).ToArray())
			{
				Service.Config.DutyTimelineProfileChoice.TryRemove(pair.Key, out _);
			}
		}

		if (ImportedTimelineRuntime.IsUsingProfile(profileId))
		{
			ImportedTimelineRuntime.Reset();
		}

		foreach (var filePath in Directory.EnumerateFiles(ProfilesDirectory, "*.json", SearchOption.TopDirectoryOnly))
		{
			try
			{
				var profile = ReadProfile(filePath);
				if (string.Equals(profile.ProfileId, profileId, StringComparison.OrdinalIgnoreCase))
				{
					File.Delete(filePath);
					break;
				}
			}
			catch
			{
				// Ignore unrelated invalid files while looking for the profile file.
			}
		}

		if (Service.Config.DutyTimelineProfileTestTerritory != 0)
		{
			var combatType = DataCenter.IsPvP ? CombatType.PvP : CombatType.PvE;
			if (TryGetDutyTimelineProfileChoice(Service.Config.DutyTimelineProfileTestTerritory, DataCenter.Job, combatType, out var assignedProfileId)
				&& string.Equals(assignedProfileId, profileId, StringComparison.OrdinalIgnoreCase))
			{
				Service.Config.DutyTimelineProfileTestTerritory = 0;
			}
		}

		return true;
	}

	private static string GetDutyTimelineProfileChoiceKey(uint territoryType, Job job, CombatType combatType)
		=> $"{territoryType}:{(int)job}:{(int)combatType}";

	private static bool TryParseDutyTimelineProfileChoiceKey(string key, out uint territoryType, out Job job, out CombatType combatType)
	{
		territoryType = 0;
		job = Job.ADV;
		combatType = CombatType.None;

		var parts = key.Split(':');
		if (parts.Length != 3
			|| !uint.TryParse(parts[0], out territoryType)
			|| !int.TryParse(parts[1], out var jobValue)
			|| !int.TryParse(parts[2], out var combatTypeValue)
			|| !Enum.IsDefined(typeof(Job), jobValue)
			|| !Enum.IsDefined(typeof(CombatType), combatTypeValue))
		{
			return false;
		}

		job = (Job)jobValue;
		combatType = (CombatType)combatTypeValue;
		return true;
	}

	private static ImportedTimelineProfile ReadProfile(string filePath)
	{
		var text = File.ReadAllText(filePath);
		return ReadProfileText(text, filePath);
	}

	private static ImportedTimelineProfile ReadProfileText(string text, string sourceName)
	{
		var normalizedText = text.Trim().TrimStart('\uFEFF');
		var profile = JsonConvert.DeserializeObject<ImportedTimelineProfile>(normalizedText)
			?? throw new InvalidDataException("Timeline profile JSON could not be parsed.");

		NormalizeProfile(profile);
		ValidateProfile(profile, sourceName);
		return profile;
	}

	private static void NormalizeProfile(ImportedTimelineProfile profile)
	{
		profile.Format = profile.Format?.Trim() ?? string.Empty;
		profile.ProfileId = string.IsNullOrWhiteSpace(profile.ProfileId)
			? Guid.NewGuid().ToString("N")
			: profile.ProfileId.Trim();

		profile.ProfileName = string.IsNullOrWhiteSpace(profile.ProfileName)
			? BuildFallbackProfileName(profile)
			: profile.ProfileName.Trim();

		profile.Source ??= new ImportedTimelineSource();
		profile.Syncs ??= [];
		profile.Syncs =
		[
			.. profile.Syncs
				.Where(sync => sync != null && IsValidSync(sync))
				.Select(NormalizeSync)
				.OrderBy(sync => sync.CombatTimeSeconds)
				.ThenBy(sync => sync.Type, StringComparer.OrdinalIgnoreCase)
				.ThenBy(sync => sync.Id)
		];

		profile.Actions ??= [];
		profile.Actions =
		[
			.. profile.Actions
				.Where(action => action != null && action.Id > 0 && action.CombatTimeSeconds >= 0)
				.OrderBy(action => action.CombatTimeSeconds)
				.ThenBy(action => action.Id)
		];
	}

	private static ImportedTimelineSync NormalizeSync(ImportedTimelineSync sync)
	{
		sync.Type = sync.Type?.Trim() ?? string.Empty;
		sync.Pattern = sync.Pattern?.Trim() ?? string.Empty;
		sync.Name = sync.Name?.Trim() ?? string.Empty;
		sync.Phase = sync.Phase?.Trim() ?? string.Empty;
		sync.WindowBeforeSeconds = Math.Max(0, sync.WindowBeforeSeconds);
		sync.WindowAfterSeconds = Math.Max(0, sync.WindowAfterSeconds);
		return sync;
	}

	private static bool IsValidSync(ImportedTimelineSync sync)
	{
		if (sync.CombatTimeSeconds < 0 || string.IsNullOrWhiteSpace(sync.Type))
		{
			return false;
		}

		var type = sync.Type.Trim();
		if (IsChatSyncType(type))
		{
			return !string.IsNullOrWhiteSpace(sync.Pattern);
		}

		if (IsCastSyncType(type))
		{
			return sync.Id > 0;
		}

		return sync.Id > 0 || !string.IsNullOrWhiteSpace(sync.Pattern);
	}

	private static bool IsChatSyncType(string type)
		=> string.Equals(type, "chat", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(type, "message", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(type, "dialog", StringComparison.OrdinalIgnoreCase);

	private static bool IsCastSyncType(string type)
		=> string.Equals(type, "cast", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(type, "enemyCast", StringComparison.OrdinalIgnoreCase);

	private static void ValidateProfile(ImportedTimelineProfile profile, string filePath)
	{
		if (!string.Equals(profile.Format, ProfileFormat, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException($"Unsupported timeline profile format in '{filePath}'.");
		}

		if (profile.Version <= 0)
		{
			throw new InvalidDataException($"Timeline profile version is invalid in '{filePath}'.");
		}

		if (profile.Actions.Count == 0)
		{
			throw new InvalidDataException($"Timeline profile '{filePath}' does not contain any actions.");
		}
	}

	private static string BuildFallbackProfileName(ImportedTimelineProfile profile)
	{
		var pieces = new List<string>();

		if (!string.IsNullOrWhiteSpace(profile.Source.FightName))
		{
			pieces.Add(profile.Source.FightName);
		}

		if (!string.IsNullOrWhiteSpace(profile.Source.SourceJob))
		{
			pieces.Add(profile.Source.SourceJob);
		}

		if (!string.IsNullOrWhiteSpace(profile.Source.FightId))
		{
			pieces.Add($"fight-{profile.Source.FightId}");
		}

		if (pieces.Count == 0)
		{
			pieces.Add("Imported timeline");
		}

		return string.Join(" - ", pieces);
	}

	private static string MakeSafeFileName(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return "timeline-profile";
		}

		var chars = value.ToCharArray();
		var invalidChars = Path.GetInvalidFileNameChars();
		for (var i = 0; i < chars.Length; i++)
		{
			if (Array.IndexOf(invalidChars, chars[i]) >= 0)
			{
				chars[i] = '_';
			}
		}

		var result = new string(chars).Trim().Trim('.');
		return string.IsNullOrWhiteSpace(result) ? "timeline-profile" : result;
	}
}

public static class ImportedTimelineRuntime
{
	private const float ExecuteLeadSeconds = 0.6f;
	private const float AssistLeadSeconds = 10.0f;
	private const float MissWindowSeconds = 1.5f;
	private const float UnavailableSkipDelaySeconds = 0.35f;
	private const float CountdownEndGraceSeconds = 2.0f;
	private const float SyncTransitionGraceSeconds = 45.0f;
	private const float SyncLeadSeconds = ExecuteLeadSeconds;
	private const float GeneralGcdRecoverySeconds = 2.0f;
	private const uint TimelineItemIdFlag = 0x02000000;
	private const uint HighQualityItemIdOffset = 1_000_000;
	private static readonly TimeSpan ReturnedScheduledActionDeferBypassDuration = TimeSpan.FromMilliseconds(250);
	private const string SyncEventChat = "chat";
	private const string SyncEventCast = "cast";
	private static string _activeProfileSignature = string.Empty;
	private static int _nextActionIndex;
	private static DateTime _lastObservedActionTime = DateTime.Now;
	private static DateTime _lastCountdownObservedTime = DateTime.MinValue;
	private static DateTime _lastSyncAppliedTime = DateTime.MinValue;
	private static DateTime _lastReturnedScheduledActionTime = DateTime.MinValue;
	private static uint _lastReturnedScheduledActionId;
	private static uint _lastReturnedScheduledAdjustedActionId;
	private static uint _lastReturnedScheduledSourceActionId;
	private static uint _lastReturnedScheduledSourceAdjustedActionId;
	private static float _lastCombatTime;
	private static float _lastRawCombatTime;
	private static float _timelineOffsetSeconds;
	private static float _generalGcdRecoveryUntilCombatTime;
	private static bool _hasTimelineOffset;
	private static bool _wasInCombat;
	private static bool _wasCountdownActive;
	private static bool _preparedFromCountdown;
	private static bool _preparedFromSync;
	private static bool _lastReturnedScheduledActionWasGcd;
	private static readonly HashSet<int> _completedActionIndices = [];
	private static readonly HashSet<int> _completedSyncIndices = [];
	private static readonly Dictionary<ulong, uint> _observedHostileCasts = [];
	private static readonly Dictionary<uint, IBaseItem?> _resolvedTimelineItems = [];

	public static bool TryGetActiveProfile(out ImportedTimelineProfile? profile)
		=> TryGetActiveProfile(out _, out profile);

	public static bool TryGetCurrentTimelineTime(out float combatTime, out float rawCombatTime, out float offsetSeconds)
	{
		rawCombatTime = DataCenter.InCombat
			? DataCenter.CombatTimeRaw
			: _lastRawCombatTime;
		offsetSeconds = _hasTimelineOffset ? _timelineOffsetSeconds : 0;
		combatTime = rawCombatTime >= 0 ? rawCombatTime + offsetSeconds : rawCombatTime;
		return TryGetActiveProfile(out _, out var profile) && profile != null;
	}

	public static void NotifyChatMessage(string chatType, string sender, string message)
	{
		if (string.IsNullOrWhiteSpace(message))
		{
			return;
		}

		if (!TryGetActiveProfile(out var territoryType, out var profile) || profile == null)
		{
			TraceIgnoredChatSync("noProfile", chatType, sender, message);
			return;
		}

		if (profile.Syncs.Count == 0)
		{
			TraceIgnoredChatSync("noSyncs", chatType, sender, message);
			return;
		}

		var signature = BuildActiveProfileSignature(profile.ProfileId, territoryType);
		if (_activeProfileSignature != signature)
		{
			ResetState(signature);
		}

		var rawCombatTime = DataCenter.InCombat ? DataCenter.CombatTimeRaw : 0;
		if (!TryApplyTimelineSync(profile, rawCombatTime, SyncEventChat, 0, message, sender, chatType))
		{
			ActionTracer.Note($"Timeline chat sync no match profile='{profile.ProfileName}' raw={rawCombatTime:F3} inCombat={DataCenter.InCombat} rotation={DataCenter.CurrentRotation != null} chat='{chatType}' sender='{TraceText(sender)}' message='{TraceText(message)}'");
		}
	}

	private static void TraceIgnoredChatSync(string reason, string chatType, string sender, string message)
	{
		if (!DataCenter.InCombat && string.IsNullOrEmpty(_activeProfileSignature))
		{
			return;
		}

		ActionTracer.Note($"Timeline chat sync ignored reason={reason} inCombat={DataCenter.InCombat} rotation={DataCenter.CurrentRotation != null} chat='{chatType}' sender='{TraceText(sender)}' message='{TraceText(message)}'");
	}

	internal static bool IsUsingProfile(string profileId)
		=> !string.IsNullOrWhiteSpace(profileId)
			&& string.Equals(_activeProfileSignature.Split(':').FirstOrDefault(), profileId, StringComparison.OrdinalIgnoreCase);

	private static bool TryGetActiveProfile(out uint territoryType, out ImportedTimelineProfile? profile)
	{
		profile = null;
		if (!TryGetActiveTerritoryType(out territoryType))
		{
			return false;
		}

		var combatType = DataCenter.IsPvP ? CombatType.PvP : CombatType.PvE;
		return ImportedTimelineManager.TryGetAssignedProfile(territoryType, DataCenter.Job, combatType, out profile);
	}

	public static bool TryGetScheduledGCD(out IAction? act)
		=> TryGetScheduledAction(true, out act);

	public static bool TryGetScheduledAbility(out IAction? act)
		=> TryGetScheduledAction(false, out act);

	internal static bool ShouldSuppressGeneralRotation(bool wantsGcd)
	{
		if (!TryPrepareActiveProfile(out var profile, out var combatTime) || profile == null)
		{
			return false;
		}

		if (wantsGcd && IsGeneralGcdRecoveryActive(combatTime))
		{
			if (CanUseGeneralGcdRecovery(profile, combatTime))
			{
				return false;
			}

			ActionTracer.Note($"Timeline suppress recovery general GCD profile='{profile.ProfileName}' t={combatTime:F3}");
			return true;
		}

		for (var index = _nextActionIndex; index < profile.Actions.Count; index++)
		{
			if (_completedActionIndices.Contains(index))
			{
				continue;
			}

			var entry = profile.Actions[index];
			if (combatTime > entry.CombatTimeSeconds + MissWindowSeconds)
			{
				continue;
			}

			if (entry.CombatTimeSeconds > combatTime + AssistLeadSeconds)
			{
				break;
			}

			if (ResolveAction(entry.Id) is not { } action || !IsTimelineEntryEnabled(action))
			{
				continue;
			}

			ActionTracer.Note($"Timeline suppress general profile='{profile.ProfileName}' t={combatTime:F3} entry={entry.Id}@{entry.CombatTimeSeconds:F3}");
			return true;
		}

		return false;
	}

	internal static bool ShouldSuppressGeneralRotation()
		=> ShouldSuppressGeneralRotation(true);

	internal static bool ShouldDeferToScheduledAction(IAction? candidate, bool wantsGcd)
	{
		if (candidate == null)
		{
			return false;
		}

		if (candidate is not IBaseAction action || action.Info.IsRealGCD != wantsGcd)
		{
			return false;
		}

		if (IsRecentlyReturnedScheduledAction(action, wantsGcd))
		{
			return false;
		}

		if (!TryPrepareActiveProfile(out var profile, out var combatTime) || profile == null)
		{
			return false;
		}

		if (wantsGcd && IsGeneralGcdRecoveryActive(combatTime))
		{
			if (CanUseGeneralGcdRecovery(profile, combatTime))
			{
				return false;
			}

			ActionTracer.Note($"Timeline defer recovery general GCD profile='{profile.ProfileName}' t={combatTime:F3} candidate={action.ID}");
			return true;
		}

		if (ShouldHoldForDueScheduledAction(profile, combatTime, action, wantsGcd))
		{
			return true;
		}

		var deferLookaheadSeconds = GetDeferLookaheadSeconds(wantsGcd);
		for (var index = _nextActionIndex; index < profile.Actions.Count; index++)
		{
			if (_completedActionIndices.Contains(index))
			{
				continue;
			}

			var entry = profile.Actions[index];
			if (combatTime > entry.CombatTimeSeconds + MissWindowSeconds)
			{
				continue;
			}

			if (entry.CombatTimeSeconds > combatTime + deferLookaheadSeconds)
			{
				break;
			}

			if (DoesEntryMatchAction(entry, action))
			{
				return combatTime + GetScheduleLeadSeconds(wantsGcd) < entry.CombatTimeSeconds;
			}
		}

		return false;
	}

	private static void RememberReturnedScheduledAction(IAction? returnedAction, IBaseAction sourceAction, bool wantsGcd)
	{
		_lastReturnedScheduledActionTime = DateTime.Now;
		_lastReturnedScheduledActionWasGcd = wantsGcd;
		_lastReturnedScheduledSourceActionId = sourceAction.ID;
		_lastReturnedScheduledSourceAdjustedActionId = sourceAction.AdjustedID;

		if (returnedAction is IBaseAction returnedBaseAction)
		{
			_lastReturnedScheduledActionId = returnedBaseAction.ID;
			_lastReturnedScheduledAdjustedActionId = returnedBaseAction.AdjustedID;
			return;
		}

		_lastReturnedScheduledActionId = sourceAction.ID;
		_lastReturnedScheduledAdjustedActionId = sourceAction.AdjustedID;
	}

	private static bool IsRecentlyReturnedScheduledAction(IBaseAction action, bool wantsGcd)
	{
		if (_lastReturnedScheduledActionWasGcd != wantsGcd
			|| _lastReturnedScheduledActionTime == DateTime.MinValue
			|| DateTime.Now - _lastReturnedScheduledActionTime > ReturnedScheduledActionDeferBypassDuration)
		{
			return false;
		}

		return MatchesRecentReturnedScheduledActionId(action.ID)
			|| MatchesRecentReturnedScheduledActionId(action.AdjustedID);
	}

	private static bool MatchesRecentReturnedScheduledActionId(uint actionId)
		=> actionId != 0
			&& (actionId == _lastReturnedScheduledActionId
				|| actionId == _lastReturnedScheduledAdjustedActionId
				|| actionId == _lastReturnedScheduledSourceActionId
				|| actionId == _lastReturnedScheduledSourceAdjustedActionId);

	private static bool ShouldHoldForDueScheduledAction(ImportedTimelineProfile profile, float combatTime, IBaseAction candidate, bool wantsGcd)
	{
		var leadSeconds = GetScheduleLeadSeconds(wantsGcd);
		for (var index = _nextActionIndex; index < profile.Actions.Count; index++)
		{
			if (_completedActionIndices.Contains(index))
			{
				continue;
			}

			var entry = profile.Actions[index];
			if (combatTime > entry.CombatTimeSeconds + MissWindowSeconds)
			{
				continue;
			}

			if (combatTime + leadSeconds < entry.CombatTimeSeconds)
			{
				break;
			}

			var resolved = ResolveAction(entry.Id);
			if (resolved is IBaseItem scheduledItem)
			{
				if (wantsGcd || !IsTimelineItemEnabled(scheduledItem))
				{
					continue;
				}

				return !DoesEntryMatchAction(entry, candidate);
			}

			if (resolved is not IBaseAction scheduled || scheduled.Info.IsRealGCD != wantsGcd || !scheduled.IsEnabled)
			{
				continue;
			}

			return !DoesEntryMatchAction(entry, candidate);
		}

		return false;
	}

	private static bool TryGetScheduledAction(bool wantsGcd, out IAction? act)
	{
		act = null;

		if (!TryPrepareActiveProfile(out var profile, out var combatTime) || profile == null)
		{
			return false;
		}

		if (_nextActionIndex >= profile.Actions.Count)
		{
			return false;
		}

		var leadSeconds = ExecuteLeadSeconds;
		for (var index = _nextActionIndex; index < profile.Actions.Count; index++)
		{
			if (_completedActionIndices.Contains(index))
			{
				continue;
			}

			var entry = profile.Actions[index];
			if (combatTime + leadSeconds < entry.CombatTimeSeconds)
			{
				break;
			}

			if (combatTime > entry.CombatTimeSeconds + MissWindowSeconds)
			{
				_nextActionIndex = index + 1;
				continue;
			}

			var resolved = ResolveAction(entry.Id);
			if (resolved is IBaseItem item)
			{
				if (wantsGcd)
				{
					ActionTracer.Note($"Timeline pass earlier item profile='{profile.ProfileName}' t={combatTime:F3} entry={entry.Id}@{entry.CombatTimeSeconds:F3}");
					continue;
				}

				if (IsTimelineItemEnabled(item) && item.CanUse(out act))
				{
					ActionTracer.Note($"Timeline accept item profile='{profile.ProfileName}' t={combatTime:F3} entry={entry.Id}@{entry.CombatTimeSeconds:F3} item={item.ID}");
					return true;
				}

				ActionTracer.Note($"Timeline reject unavailable item profile='{profile.ProfileName}' t={combatTime:F3} entry={entry.Id}@{entry.CombatTimeSeconds:F3} item={item.ID}");
				TrySkipUnavailableScheduledAction(profile, index, entry, combatTime);
				continue;
			}

			if (resolved is not IBaseAction action || !action.IsEnabled)
			{
				continue;
			}

			if (action.Info.IsRealGCD != wantsGcd)
			{
				if (wantsGcd && !action.Info.IsRealGCD)
				{
					ActionTracer.Note($"Timeline pass earlier ability profile='{profile.ProfileName}' t={combatTime:F3} entry={entry.Id}@{entry.CombatTimeSeconds:F3}");
					continue;
				}

				if (!wantsGcd && action.Info.IsRealGCD && IsRecentlyReturnedScheduledAction(action, true))
				{
					ActionTracer.Note($"Timeline pass returned GCD profile='{profile.ProfileName}' t={combatTime:F3} entry={entry.Id}@{entry.CombatTimeSeconds:F3}");
					continue;
				}

				if (!wantsGcd && action.Info.IsRealGCD && !action.Cooldown.CooldownCheck(true, 0))
				{
					ActionTracer.Note($"Timeline pass earlier GCD profile='{profile.ProfileName}' t={combatTime:F3} entry={entry.Id}@{entry.CombatTimeSeconds:F3}");
					continue;
				}

				ActionTracer.Note($"Timeline wait earlier profile='{profile.ProfileName}' t={combatTime:F3} entry={entry.Id}@{entry.CombatTimeSeconds:F3}");
				return false;
			}

			try
			{
				IBaseAction.ForceEnable = true;
				IBaseAction.IgnoreActionCheck = true;
				if (wantsGcd
					&& ShouldUseTimelineHostileTarget(entry, action)
					&& TryUseScheduledGcdFallback(entry, action, out act))
				{
					ActionTracer.Note($"Timeline reserve hostile GCD profile='{profile.ProfileName}' t={combatTime:F3} entry={entry.Id}@{entry.CombatTimeSeconds:F3}");
					RememberReturnedScheduledAction(act, action, wantsGcd);
					return true;
				}

				if (action.CanUse(out act,
					skipStatusProvideCheck: true,
					skipTargetStatusNeedCheck: true,
					skipComboCheck: true,
					usedUp: true,
					skipAoeCheck: true,
					skipTTKCheck: true))
				{
					if (ShouldUseTimelineHostileTarget(entry, action) && !TryAssignScheduledHostileTarget(action))
					{
						ActionTracer.Note($"Timeline reject no hostile target profile='{profile.ProfileName}' t={combatTime:F3} entry={entry.Id}@{entry.CombatTimeSeconds:F3}");
						TrySkipUnavailableScheduledAction(profile, index, entry, combatTime);
						continue;
					}

					ActionTracer.Note($"Timeline accept profile='{profile.ProfileName}' t={combatTime:F3} entry={entry.Id}@{entry.CombatTimeSeconds:F3}");
					RememberReturnedScheduledAction(act, action, wantsGcd);
					return true;
				}

				if (wantsGcd && TryUseScheduledGcdFallback(entry, action, out act))
				{
					ActionTracer.Note($"Timeline reserve GCD profile='{profile.ProfileName}' t={combatTime:F3} entry={entry.Id}@{entry.CombatTimeSeconds:F3}");
					RememberReturnedScheduledAction(act, action, wantsGcd);
					return true;
				}

				ActionTracer.Note($"Timeline reject unavailable profile='{profile.ProfileName}' t={combatTime:F3} entry={entry.Id}@{entry.CombatTimeSeconds:F3}");
				if (TrySkipUnavailableScheduledAction(profile, index, entry, combatTime, action) && wantsGcd)
				{
					BeginGeneralGcdRecovery(profile, entry, combatTime);
				}
			}
			finally
			{
				IBaseAction.ForceEnable = false;
				IBaseAction.IgnoreActionCheck = false;
			}
		}

		return false;
	}

	private static bool TrySkipUnavailableScheduledAction(ImportedTimelineProfile profile, int index, ImportedTimelineAction entry, float combatTime, IBaseAction? action = null)
	{
		if (combatTime < entry.CombatTimeSeconds + UnavailableSkipDelaySeconds)
		{
			return false;
		}

		if (action is { Info.IsRealGCD: false } && IsTimelineAbilityBlockedByAnimationLock(action))
		{
			ActionTracer.Note($"Timeline wait animation lock profile='{profile.ProfileName}' t={combatTime:F3} entry={entry.Id}@{entry.CombatTimeSeconds:F3} lock={Player.AnimationLock:F3}");
			return false;
		}

		_completedActionIndices.Add(index);
		AdvanceCompletedOrExpiredActions(profile, combatTime);
		ActionTracer.Note($"Timeline skip unavailable profile='{profile.ProfileName}' t={combatTime:F3} entry={entry.Id}@{entry.CombatTimeSeconds:F3}");
		return true;
	}

	private static bool IsTimelineAbilityBlockedByAnimationLock(IBaseAction action)
		=> action.Cooldown.GetCooldownRejectReason(true, 0)?.StartsWith("Cooldown:AnimationLock", StringComparison.Ordinal) == true;

	private static void BeginGeneralGcdRecovery(ImportedTimelineProfile profile, ImportedTimelineAction entry, float combatTime)
	{
		var until = combatTime + GeneralGcdRecoverySeconds;
		if (until <= _generalGcdRecoveryUntilCombatTime)
		{
			return;
		}

		_generalGcdRecoveryUntilCombatTime = until;
		ActionTracer.Note($"Timeline recover general GCD profile='{profile.ProfileName}' t={combatTime:F3} until={until:F3} missed={entry.Id}@{entry.CombatTimeSeconds:F3}");
	}

	private static bool IsGeneralGcdRecoveryActive(float combatTime)
		=> _generalGcdRecoveryUntilCombatTime > 0
			&& combatTime <= _generalGcdRecoveryUntilCombatTime;

	private static bool CanUseGeneralGcdRecovery(ImportedTimelineProfile profile, float combatTime)
	{
		var lookaheadSeconds = GetGcdSuppressionLeadSeconds();
		for (var index = _nextActionIndex; index < profile.Actions.Count; index++)
		{
			if (_completedActionIndices.Contains(index))
			{
				continue;
			}

			var entry = profile.Actions[index];
			if (combatTime > entry.CombatTimeSeconds + MissWindowSeconds)
			{
				continue;
			}

			if (entry.CombatTimeSeconds > combatTime + lookaheadSeconds)
			{
				break;
			}

			if (ResolveAction(entry.Id) is IBaseAction { Info.IsRealGCD: true } action && action.IsEnabled)
			{
				ActionTracer.Note($"Timeline hold recovery general GCD profile='{profile.ProfileName}' t={combatTime:F3} next={entry.Id}@{entry.CombatTimeSeconds:F3}");
				return false;
			}
		}

		return true;
	}

	private static float GetScheduleLeadSeconds(bool wantsGcd)
	{
		if (!wantsGcd)
		{
			return ExecuteLeadSeconds;
		}

		return MathF.Min(AssistLeadSeconds, MathF.Max(ExecuteLeadSeconds, DataCenter.DefaultGCDRemain + ExecuteLeadSeconds));
	}

	private static bool TryUseScheduledGcdFallback(ImportedTimelineAction entry, IBaseAction action, out IAction? act)
	{
		act = null;

		if (Player.Object == null)
		{
			return false;
		}

		if (!action.Cooldown.CooldownCheck(true, 0))
		{
			return false;
		}

		if (!action.Info.BasicCheck(
			skipStatusProvideCheck: true,
			skipStatusNeed: false,
			skipComboCheck: true,
			skipCastingCheck: false,
			checkActionManager: false))
		{
			return false;
		}

		if (TryAssignScheduledHostileTarget(action) || (!ShouldUseTimelineHostileTarget(entry, action) && TryAssignScheduledSelfExecutedTarget(action)))
		{
			act = action;
			return true;
		}

		return false;
	}

	private static bool ShouldUseTimelineHostileTarget(ImportedTimelineAction entry, IBaseAction action)
		=> entry.SourceIsFriendly
			&& !entry.TargetIsFriendly
			&& !action.Setting.IsFriendly
			&& action.Info.CanTargetHostile
			&& !action.TargetInfo.IsTargetArea;

	private static bool TryAssignScheduledHostileTarget(IBaseAction action)
	{
		if (!IsTargetedHostileAction(action))
		{
			return false;
		}

		if (!TryGetScheduledHostileTarget(action, out var target) || target == null)
		{
			return false;
		}

		action.Target = new TargetResult(target, [], target.Position);
		return true;
	}

	private static bool TryGetScheduledHostileTarget(IBaseAction action, out IBattleChara? target)
	{
		target = null;

		if (Svc.Targets.Target is IBattleChara currentTarget && IsValidScheduledHostileTarget(action, currentTarget, false))
		{
			target = currentTarget;
			return true;
		}

		var hostileTarget = DataCenter.HostileTarget;
		if (hostileTarget != null && IsValidScheduledHostileTarget(action, hostileTarget, false))
		{
			target = hostileTarget;
			return true;
		}

		var hostiles = DataCenter.AllHostileTargets;
		for (var i = 0; i < hostiles.Count; i++)
		{
			if (IsValidScheduledHostileTarget(action, hostiles[i], true))
			{
				target = hostiles[i];
				return true;
			}
		}

		return false;
	}

	private static bool IsValidScheduledHostileTarget(IBaseAction action, IBattleChara target, bool requireKnownHostile)
	{
		if (requireKnownHostile && !IsKnownHostileTarget(target))
		{
			return false;
		}

		if (!target.IsEnemy())
		{
			return false;
		}

		var range = GetScheduledTargetRange(action);
		if (range > 0 && target.DistanceToPlayer() > range)
		{
			return false;
		}

		return action.Setting.CanTarget(target);
	}

	private static bool IsKnownHostileTarget(IBattleChara target)
	{
		var hostiles = DataCenter.AllHostileTargets;
		for (var i = 0; i < hostiles.Count; i++)
		{
			if (hostiles[i].GameObjectId == target.GameObjectId)
			{
				return true;
			}
		}

		return false;
	}

	private static float GetScheduledTargetRange(IBaseAction action)
		=> MathF.Max(action.TargetInfo.Range, action.Info.Range);

	private static bool TryAssignScheduledSelfExecutedTarget(IBaseAction action)
	{
		if (!IsSelfExecutedHostileAction(action) || Player.Object == null)
		{
			return false;
		}

		action.Target = new TargetResult(Player.Object, [], Player.Object.Position);
		return true;
	}

	private static bool IsTargetedHostileAction(IBaseAction action)
		=> action.Info.CanTargetHostile
			&& !action.TargetInfo.IsTargetArea
			&& !action.Setting.IsFriendly;

	private static bool IsSelfExecutedHostileAction(IBaseAction action)
		=> action.TargetInfo.Range == 0
			&& action.TargetInfo.EffectRange > 0
			&& !action.Info.CanTargetHostile
			&& !action.TargetInfo.IsSingleTarget
			&& !action.TargetInfo.IsTargetArea
			&& !action.Setting.IsFriendly;

	private static bool TryPrepareActiveProfile(out ImportedTimelineProfile? profile, out float combatTime)
	{
		combatTime = 0;
		profile = null;

		if (DataCenter.CurrentRotation == null)
		{
			ResetState();
			return false;
		}

		var now = DateTime.Now;
		var countdownTime = Service.CountDownTime;
		var isCountdownActive = !DataCenter.InCombat && countdownTime > 0;
		if (isCountdownActive)
		{
			_lastCountdownObservedTime = now;
		}

		var isCountdownJustFinished = !DataCenter.InCombat
			&& _lastCountdownObservedTime != DateTime.MinValue
			&& now - _lastCountdownObservedTime <= TimeSpan.FromSeconds(CountdownEndGraceSeconds);
		var isSyncTransitionActive = !DataCenter.InCombat
			&& _preparedFromSync
			&& now - _lastSyncAppliedTime <= TimeSpan.FromSeconds(SyncTransitionGraceSeconds);

		if (!DataCenter.InCombat && !isCountdownActive && !isCountdownJustFinished && !isSyncTransitionActive)
		{
			ResetState();
			return false;
		}

		if (!TryGetActiveProfile(out var territoryType, out profile) || profile == null || profile.Actions.Count == 0)
		{
			ResetState();
			return false;
		}

		var signature = BuildActiveProfileSignature(profile.ProfileId, territoryType);
		var rawCombatTime = DataCenter.InCombat
			? DataCenter.CombatTimeRaw
			: isCountdownActive
				? -countdownTime
				: 0;

		var profileChanged = _activeProfileSignature != signature;
		var countdownStarted = isCountdownActive && !_wasCountdownActive;
		var combatStartedWithoutCountdown = DataCenter.InCombat
			&& !_wasInCombat
			&& !_preparedFromCountdown
			&& !_preparedFromSync
			&& !isCountdownJustFinished;
		var combatTimeRewound = !isCountdownActive && rawCombatTime + 0.01f < _lastRawCombatTime;
		if (profileChanged || countdownStarted || combatStartedWithoutCountdown || combatTimeRewound)
		{
			ResetState(signature);
			var reason = profileChanged
				? "profile"
				: countdownStarted
					? "countdown"
					: combatStartedWithoutCountdown
						? "combat"
						: "rewind";
			ActionTracer.Note($"Timeline activate profile='{profile.ProfileName}' reason={reason} raw={rawCombatTime:F3} territory={territoryType} inCombat={DataCenter.InCombat} countdown={countdownTime:F3}");
		}

		if (isCountdownActive)
		{
			_lastCountdownObservedTime = now;
			_preparedFromCountdown = true;
		}

		UpdateCastSyncs(profile, rawCombatTime);
		combatTime = ApplyTimelineOffset(rawCombatTime);
		_lastRawCombatTime = rawCombatTime;
		_lastCombatTime = combatTime;
		_wasInCombat = DataCenter.InCombat;
		_wasCountdownActive = isCountdownActive;
		if (DataCenter.InCombat)
		{
			_preparedFromSync = false;
		}
		SyncWithRecentActions(profile, combatTime);
		AdvanceCompletedOrExpiredActions(profile, combatTime);
		return true;
	}

	private static float ApplyTimelineOffset(float rawCombatTime)
		=> _hasTimelineOffset && rawCombatTime >= 0
			? rawCombatTime + _timelineOffsetSeconds
			: rawCombatTime;

	private static void UpdateCastSyncs(ImportedTimelineProfile profile, float rawCombatTime)
	{
		if (!DataCenter.InCombat || rawCombatTime < 0 || profile.Syncs.Count == 0)
		{
			_observedHostileCasts.Clear();
			return;
		}

		HashSet<ulong> observedObjectIds = [];
		var hostiles = DataCenter.AllHostileTargets;
		for (var i = 0; i < hostiles.Count; i++)
		{
			var hostile = hostiles[i];
			var objectId = hostile.GameObjectId;
			if (objectId == 0)
			{
				continue;
			}

			observedObjectIds.Add(objectId);
			var castId = hostile.CastActionId;
			if (castId == 0)
			{
				_observedHostileCasts.Remove(objectId);
				continue;
			}

			if (_observedHostileCasts.TryGetValue(objectId, out var previousCastId) && previousCastId == castId)
			{
				continue;
			}

			_observedHostileCasts[objectId] = castId;
			TryApplyTimelineSync(profile, rawCombatTime, SyncEventCast, castId, string.Empty, hostile.Name.TextValue, string.Empty);
		}

		foreach (var objectId in _observedHostileCasts.Keys.Where(objectId => !observedObjectIds.Contains(objectId)).ToArray())
		{
			_observedHostileCasts.Remove(objectId);
		}
	}

	private static bool TryApplyTimelineSync(
		ImportedTimelineProfile profile,
		float rawCombatTime,
		string eventType,
		uint eventId,
		string message,
		string sender,
		string chatType)
	{
		if (rawCombatTime < 0 || profile.Syncs.Count == 0)
		{
			return false;
		}

		var currentCombatTime = ApplyTimelineOffset(rawCombatTime);
		var fallbackSyncIndex = -1;
		ImportedTimelineSync? fallbackSync = null;
		var fallbackSyncMatches = 0;
		for (var index = 0; index < profile.Syncs.Count; index++)
		{
			var sync = profile.Syncs[index];
			if (sync.Once && _completedSyncIndices.Contains(index))
			{
				continue;
			}

			if (!DoesSyncMatchEvent(sync, eventType, eventId, message))
			{
				continue;
			}

			if (!IsSyncWithinWindow(sync, currentCombatTime))
			{
				ActionTracer.Note(
					$"Timeline sync outside window profile='{profile.ProfileName}' type='{eventType}' marker='{GetSyncTraceName(sync)}' current={currentCombatTime:F3} sync={sync.CombatTimeSeconds:F3} window=[-{sync.WindowBeforeSeconds:F3},+{sync.WindowAfterSeconds:F3}]");

				if (string.Equals(eventType, SyncEventChat, StringComparison.OrdinalIgnoreCase))
				{
					fallbackSyncMatches++;
					if (fallbackSyncMatches == 1)
					{
						fallbackSyncIndex = index;
						fallbackSync = sync;
					}
				}

				continue;
			}

			ApplyTimelineSync(profile, index, sync, rawCombatTime, currentCombatTime, eventType, eventId, sender, chatType);
			return true;
		}

		if (string.Equals(eventType, SyncEventChat, StringComparison.OrdinalIgnoreCase))
		{
			if (fallbackSyncMatches == 1 && fallbackSync != null)
			{
				ActionTracer.Note(
					$"Timeline chat sync fallback profile='{profile.ProfileName}' marker='{GetSyncTraceName(fallbackSync)}' raw={rawCombatTime:F3} from={currentCombatTime:F3} to={fallbackSync.CombatTimeSeconds:F3}");
				ApplyTimelineSync(profile, fallbackSyncIndex, fallbackSync, rawCombatTime, currentCombatTime, eventType, eventId, sender, chatType);
				return true;
			}

			if (fallbackSyncMatches > 1)
			{
				ActionTracer.Note($"Timeline chat sync ambiguous profile='{profile.ProfileName}' raw={rawCombatTime:F3} matches={fallbackSyncMatches} message='{TraceText(message)}'");
			}
		}

		return false;
	}

	private static float GetDeferLookaheadSeconds(bool wantsGcd)
		=> wantsGcd ? GetGcdSuppressionLeadSeconds() : AssistLeadSeconds;

	private static float GetGcdSuppressionLeadSeconds()
		=> MathF.Min(AssistLeadSeconds, MathF.Max(GetScheduleLeadSeconds(true), DataCenter.DefaultGCDTotal + ExecuteLeadSeconds));

	private static bool DoesSyncMatchEvent(ImportedTimelineSync sync, string eventType, uint eventId, string message)
	{
		if (string.Equals(eventType, SyncEventChat, StringComparison.OrdinalIgnoreCase))
		{
			if (!IsChatSyncType(sync.Type))
			{
				return false;
			}

			return DoesChatMessageMatch(sync, message);
		}

		if (string.Equals(eventType, SyncEventCast, StringComparison.OrdinalIgnoreCase))
		{
			return IsCastSyncType(sync.Type) && sync.Id == eventId;
		}

		return false;
	}

	private static bool DoesChatMessageMatch(ImportedTimelineSync sync, string message)
	{
		if (string.IsNullOrWhiteSpace(sync.Pattern) || string.IsNullOrWhiteSpace(message))
		{
			return false;
		}

		if (!sync.Regex)
		{
			return message.Contains(sync.Pattern, StringComparison.OrdinalIgnoreCase);
		}

		try
		{
			return System.Text.RegularExpressions.Regex.IsMatch(
				message,
				sync.Pattern,
				System.Text.RegularExpressions.RegexOptions.CultureInvariant);
		}
		catch (ArgumentException ex)
		{
			PluginLog.Warning($"Invalid imported timeline sync regex '{sync.Pattern}': {ex.Message}");
			return false;
		}
	}

	private static bool IsSyncWithinWindow(ImportedTimelineSync sync, float combatTime)
		=> combatTime >= sync.CombatTimeSeconds - sync.WindowBeforeSeconds
			&& combatTime <= sync.CombatTimeSeconds + sync.WindowAfterSeconds;

	private static void ApplyTimelineSync(
		ImportedTimelineProfile profile,
		int syncIndex,
		ImportedTimelineSync sync,
		float rawCombatTime,
		float previousCombatTime,
		string eventType,
		uint eventId,
		string sender,
		string chatType)
	{
		var syncedCombatTime = (float)sync.CombatTimeSeconds;
		_timelineOffsetSeconds = syncedCombatTime - rawCombatTime;
		_hasTimelineOffset = true;
		_lastRawCombatTime = rawCombatTime;
		_lastCombatTime = syncedCombatTime;
		_wasInCombat = DataCenter.InCombat;
		_wasCountdownActive = false;
		_preparedFromSync = true;
		_lastSyncAppliedTime = DateTime.Now;

		if (sync.Once)
		{
			_completedSyncIndices.Add(syncIndex);
		}

		SetTimelinePosition(profile, syncedCombatTime);

		ActionTracer.Note(
			$"Timeline sync profile='{profile.ProfileName}' type='{eventType}' id={eventId} chat='{chatType}' sender='{TraceText(sender)}' marker='{GetSyncTraceName(sync)}' raw={rawCombatTime:F3} from={previousCombatTime:F3} to={syncedCombatTime:F3} offset={_timelineOffsetSeconds:F3}");
	}

	private static string GetSyncTraceName(ImportedTimelineSync sync)
	{
		if (!string.IsNullOrWhiteSpace(sync.Phase))
		{
			return TraceText(sync.Phase);
		}

		if (!string.IsNullOrWhiteSpace(sync.Name))
		{
			return TraceText(sync.Name);
		}

		if (!string.IsNullOrWhiteSpace(sync.Pattern))
		{
			return TraceText(sync.Pattern);
		}

		return sync.Id.ToString();
	}

	private static string TraceText(string? value)
	{
		if (string.IsNullOrEmpty(value))
		{
			return string.Empty;
		}

		value = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
		return value.Length <= 80 ? value : string.Concat(value.AsSpan(0, 77), "...");
	}

	private static void SetTimelinePosition(ImportedTimelineProfile profile, float combatTime)
	{
		var nextIndex = 0;
		var searchFrom = combatTime - MissWindowSeconds;
		while (nextIndex < profile.Actions.Count && profile.Actions[nextIndex].CombatTimeSeconds < searchFrom)
		{
			nextIndex++;
		}

		_nextActionIndex = nextIndex;
		_completedActionIndices.RemoveWhere(index => index >= _nextActionIndex);
		AdvanceCompletedOrExpiredActions(profile, combatTime);
	}

	private static bool IsChatSyncType(string type)
		=> string.Equals(type, "chat", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(type, "message", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(type, "dialog", StringComparison.OrdinalIgnoreCase);

	private static bool IsCastSyncType(string type)
		=> string.Equals(type, "cast", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(type, "enemyCast", StringComparison.OrdinalIgnoreCase);

	private static void SyncWithRecentActions(ImportedTimelineProfile profile, float combatTime)
	{
		var records = DataCenter.RecordActions;
		if (records.Length == 0 || _nextActionIndex >= profile.Actions.Count)
		{
			return;
		}

		var newRecords = records
			.Where(record => record.UsedTime > _lastObservedActionTime)
			.OrderBy(record => record.UsedTime)
			.ToArray();

		if (newRecords.Length == 0)
		{
			return;
		}

		foreach (var record in newRecords)
		{
			if (_nextActionIndex >= profile.Actions.Count)
			{
				break;
			}

			var latestActionId = record.Action.RowId;
			for (var index = _nextActionIndex; index < profile.Actions.Count; index++)
			{
				if (_completedActionIndices.Contains(index))
				{
					continue;
				}

				var entry = profile.Actions[index];
				if (combatTime < entry.CombatTimeSeconds - SyncLeadSeconds)
				{
					break;
				}

				if (combatTime > entry.CombatTimeSeconds + MissWindowSeconds)
				{
					continue;
				}

				if (!IsWithinTrackingWindow(entry, combatTime))
				{
					continue;
				}

				if (DoesEntryMatchAction(entry, latestActionId))
				{
					_completedActionIndices.Add(index);
					ActionTracer.Note($"Timeline observed profile='{profile.ProfileName}' used={latestActionId} entry={entry.Id}@{entry.CombatTimeSeconds:F3}");
					break;
				}
			}

			AdvanceCompletedOrExpiredActions(profile, combatTime);
		}

		_lastObservedActionTime = newRecords[^1].UsedTime;
	}

	private static void AdvanceCompletedOrExpiredActions(ImportedTimelineProfile profile, float combatTime)
	{
		while (_nextActionIndex < profile.Actions.Count)
		{
			var entry = profile.Actions[_nextActionIndex];
			if (_completedActionIndices.Remove(_nextActionIndex))
			{
				_nextActionIndex++;
				continue;
			}

			if (combatTime <= entry.CombatTimeSeconds + MissWindowSeconds)
			{
				break;
			}

			_nextActionIndex++;
		}
	}

	private static bool IsWithinTrackingWindow(ImportedTimelineAction entry, float combatTime)
		=> combatTime >= entry.CombatTimeSeconds - SyncLeadSeconds
			&& combatTime <= entry.CombatTimeSeconds + MissWindowSeconds;

	private static bool DoesEntryMatchAction(ImportedTimelineAction entry, uint actionId)
	{
		if (entry.Id == actionId)
		{
			return true;
		}

		if (DoesTimelineItemIdMatch(entry.Id, actionId))
		{
			return true;
		}

		var resolved = ResolveAction(entry.Id);
		return resolved != null && (resolved.ID == actionId || resolved.AdjustedID == actionId);
	}

	private static bool DoesEntryMatchAction(ImportedTimelineAction entry, IAction action)
	{
		if (entry.Id == action.ID || entry.Id == action.AdjustedID)
		{
			return true;
		}

		if (DoesTimelineItemIdMatch(entry.Id, action.ID) || DoesTimelineItemIdMatch(entry.Id, action.AdjustedID))
		{
			return true;
		}

		var resolved = ResolveAction(entry.Id);
		return resolved != null
			&& (resolved.ID == action.ID
				|| resolved.ID == action.AdjustedID
				|| resolved.AdjustedID == action.ID
				|| resolved.AdjustedID == action.AdjustedID);
	}

	private static IAction? ResolveAction(uint actionId)
	{
		var rotationActions = DataCenter.CurrentRotation?.AllActions ?? [];
		var dutyActions = DataCenter.CurrentDutyRotation?.AllActions ?? [];
		var id = (ActionID)actionId;

		return id.GetActionFromID(false, rotationActions, dutyActions)
			?? id.GetActionFromID(true, rotationActions, dutyActions)
			?? ResolveTimelineItem(actionId);
	}

	private static bool IsTimelineEntryEnabled(IAction action)
		=> action switch
		{
			IBaseAction baseAction => baseAction.IsEnabled,
			IBaseItem item => IsTimelineItemEnabled(item),
			_ => false,
		};

	private static bool IsTimelineItemEnabled(IBaseItem item)
		=> item.HasIt && (item is not BaseItem baseItem || baseItem.IsEnabled);

	private static IBaseItem? ResolveTimelineItem(uint timelineId)
	{
		if (!TryNormalizeTimelineItemId(timelineId, out var itemId))
		{
			return null;
		}

		if (_resolvedTimelineItems.TryGetValue(itemId, out var item))
		{
			return item;
		}

		try
		{
			var itemRow = Service.GetSheet<Item>().GetRow(itemId)!;
			item = itemRow.RowId == 0 ? null : new BaseItem(itemRow);
		}
		catch
		{
			item = null;
		}

		_resolvedTimelineItems[itemId] = item;
		return item;
	}

	private static bool TryNormalizeTimelineItemId(uint timelineId, out uint itemId)
	{
		itemId = 0;

		if (timelineId >= TimelineItemIdFlag)
		{
			timelineId -= TimelineItemIdFlag;
		}
		else if (timelineId >= HighQualityItemIdOffset)
		{
			timelineId -= HighQualityItemIdOffset;
		}
		else
		{
			return false;
		}

		if (timelineId >= HighQualityItemIdOffset)
		{
			timelineId -= HighQualityItemIdOffset;
		}

		itemId = timelineId;
		return itemId != 0;
	}

	private static bool DoesTimelineItemIdMatch(uint timelineId, uint actionId)
	{
		if (!TryNormalizeTimelineItemId(timelineId, out var itemId))
		{
			return false;
		}

		return actionId == itemId
			|| actionId == itemId + HighQualityItemIdOffset
			|| actionId == itemId + TimelineItemIdFlag
			|| actionId == itemId + TimelineItemIdFlag + HighQualityItemIdOffset;
	}

	private static bool TryGetActiveTerritoryType(out uint territoryType)
	{
		if (DataCenter.IsInDuty && Svc.ClientState.TerritoryType != 0)
		{
			territoryType = Svc.ClientState.TerritoryType;
			return true;
		}

		if (!DataCenter.IsPvP && Service.Config.DutyTimelineProfileTestTerritory != 0)
		{
			territoryType = Service.Config.DutyTimelineProfileTestTerritory;
			return true;
		}

		territoryType = 0;
		return false;
	}

	private static string BuildActiveProfileSignature(string profileId, uint territoryType)
		=> $"{profileId}:{territoryType}:{(int)DataCenter.Job}:{(int)(DataCenter.IsPvP ? CombatType.PvP : CombatType.PvE)}";

	internal static void Reset()
		=> ResetState();

	private static void ResetState(string signature = "")
	{
		_activeProfileSignature = signature;
		_nextActionIndex = 0;
		_lastObservedActionTime = DateTime.Now;
		_lastCountdownObservedTime = DateTime.MinValue;
		_lastSyncAppliedTime = DateTime.MinValue;
		_lastReturnedScheduledActionTime = DateTime.MinValue;
		_lastReturnedScheduledActionId = 0;
		_lastReturnedScheduledAdjustedActionId = 0;
		_lastReturnedScheduledSourceActionId = 0;
		_lastReturnedScheduledSourceAdjustedActionId = 0;
		_lastCombatTime = 0;
		_lastRawCombatTime = 0;
		_timelineOffsetSeconds = 0;
		_generalGcdRecoveryUntilCombatTime = 0;
		_hasTimelineOffset = false;
		_wasInCombat = false;
		_wasCountdownActive = false;
		_preparedFromCountdown = false;
		_preparedFromSync = false;
		_lastReturnedScheduledActionWasGcd = false;
		_completedActionIndices.Clear();
		_completedSyncIndices.Clear();
		_observedHostileCasts.Clear();
		_resolvedTimelineItems.Clear();
	}
}
