using ECommons.DalamudServices;
using ECommons.ExcelServices;
using ECommons.Logging;
using RotationSolver.Basic.Data;

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

		foreach (var filePath in Directory.EnumerateFiles(ProfilesDirectory, "*.json", SearchOption.TopDirectoryOnly))
		{
			try
			{
				var profile = ReadProfile(filePath);
				loaded[profile.ProfileId] = profile;
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
		if (string.IsNullOrWhiteSpace(profileId))
		{
			Service.Config.DutyTimelineProfileChoice.TryRemove(key, out _);
		}
		else
		{
			Service.Config.DutyTimelineProfileChoice[key] = profileId;
		}
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
		profile.Actions ??= [];
		profile.Actions =
		[
			.. profile.Actions
				.Where(action => action != null && action.Id > 0 && action.CombatTimeSeconds >= 0)
				.OrderBy(action => action.CombatTimeSeconds)
				.ThenBy(action => action.Id)
		];
	}

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
	private const float MissWindowSeconds = 6.0f;
	private static string _activeProfileSignature = string.Empty;
	private static int _nextActionIndex;
	private static DateTime _lastObservedActionTime = DateTime.Now;
	private static float _lastCombatTime;

	public static bool TryGetActiveProfile(out ImportedTimelineProfile? profile)
		=> TryGetActiveProfile(out _, out profile);

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

		if (!TryPrepareActiveProfile(out var profile, out var combatTime) || profile == null)
		{
			return false;
		}

		for (var index = _nextActionIndex; index < profile.Actions.Count; index++)
		{
			var entry = profile.Actions[index];
			if (combatTime > entry.CombatTimeSeconds + MissWindowSeconds)
			{
				continue;
			}

			if (entry.CombatTimeSeconds > combatTime + AssistLeadSeconds)
			{
				break;
			}

			if (DoesEntryMatchAction(entry, action))
			{
				return true;
			}
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

		var entry = profile.Actions[_nextActionIndex];
		if (combatTime + ExecuteLeadSeconds < entry.CombatTimeSeconds)
		{
			return false;
		}

		if (combatTime > entry.CombatTimeSeconds + MissWindowSeconds)
		{
			_nextActionIndex++;
			return false;
		}

		var resolved = ResolveAction(entry.Id);
		if (resolved is not IBaseAction action || action.Info.IsRealGCD != wantsGcd || !action.IsEnabled)
		{
			return false;
		}

		try
		{
			IBaseAction.ForceEnable = true;
			return action.CanUse(out act, usedUp: true, skipAoeCheck: true, skipStatusProvideCheck: true);
		}
		finally
		{
			IBaseAction.ForceEnable = false;
		}
	}

	private static bool TryPrepareActiveProfile(out ImportedTimelineProfile? profile, out float combatTime)
	{
		combatTime = 0;

		if (!DataCenter.InCombat || DataCenter.CurrentRotation == null)
		{
			ResetState();
			profile = null;
			return false;
		}

		if (!TryGetActiveProfile(out var territoryType, out profile) || profile == null || profile.Actions.Count == 0)
		{
			ResetState();
			return false;
		}

		var signature = BuildActiveProfileSignature(profile.ProfileId, territoryType);
		combatTime = DataCenter.CombatTimeRaw;
		if (_activeProfileSignature != signature || combatTime + 0.01f < _lastCombatTime)
		{
			ResetState(signature);
		}

		_lastCombatTime = combatTime;
		SyncWithRecentActions(profile, combatTime);
		AdvanceExpiredActions(profile, combatTime);
		return true;
	}

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

			var entry = profile.Actions[_nextActionIndex];
			if (!IsWithinTrackingWindow(entry, combatTime))
			{
				continue;
			}

			var latestActionId = record.Action.RowId;
			if (DoesEntryMatchAction(entry, latestActionId))
			{
				_nextActionIndex++;
			}
		}

		_lastObservedActionTime = newRecords[^1].UsedTime;
	}

	private static void AdvanceExpiredActions(ImportedTimelineProfile profile, float combatTime)
	{
		while (_nextActionIndex < profile.Actions.Count)
		{
			var entry = profile.Actions[_nextActionIndex];
			if (combatTime <= entry.CombatTimeSeconds + MissWindowSeconds)
			{
				break;
			}

			_nextActionIndex++;
		}
	}

	private static bool IsWithinTrackingWindow(ImportedTimelineAction entry, float combatTime)
		=> combatTime >= entry.CombatTimeSeconds - AssistLeadSeconds
			&& combatTime <= entry.CombatTimeSeconds + MissWindowSeconds;

	private static bool DoesEntryMatchAction(ImportedTimelineAction entry, uint actionId)
	{
		if (entry.Id == actionId)
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

		return id.GetActionFromID(true, rotationActions, dutyActions)
			?? id.GetActionFromID(false, rotationActions, dutyActions);
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
		_lastCombatTime = 0;
	}
}
