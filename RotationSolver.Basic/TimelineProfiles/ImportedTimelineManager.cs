using ECommons.DalamudServices;
using ECommons.ExcelServices;
using ECommons.Logging;
using RotationSolver.Basic.Data;

namespace RotationSolver.Basic.TimelineProfiles;

public static class ImportedTimelineManager
{
	private const string ProfileFormat = "RotationSolverTimelineProfile";
	private static Dictionary<string, ImportedTimelineProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);

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
		if (string.IsNullOrWhiteSpace(profileId))
		{
			Service.Config.DutyTimelineProfileChoice.TryRemove(key, out _);
		}
		else
		{
			Service.Config.DutyTimelineProfileChoice[key] = profileId;
		}
	}

	private static string GetDutyTimelineProfileChoiceKey(uint territoryType, Job job, CombatType combatType)
		=> $"{territoryType}:{(int)job}:{(int)combatType}";

	private static ImportedTimelineProfile ReadProfile(string filePath)
	{
		var text = File.ReadAllText(filePath);
		var profile = JsonConvert.DeserializeObject<ImportedTimelineProfile>(text)
			?? throw new InvalidDataException("Timeline profile JSON could not be parsed.");

		NormalizeProfile(profile);
		ValidateProfile(profile, filePath);
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
	private const float MissWindowSeconds = 6.0f;
	private static string _activeProfileSignature = string.Empty;
	private static int _nextActionIndex;
	private static DateTime _lastObservedActionTime = DateTime.Now;
	private static float _lastCombatTime;

	public static bool TryGetActiveProfile(out ImportedTimelineProfile? profile)
	{
		profile = null;
		if (!DataCenter.IsInDuty || Svc.ClientState.TerritoryType == 0)
		{
			return false;
		}

		var combatType = DataCenter.IsPvP ? CombatType.PvP : CombatType.PvE;
		return ImportedTimelineManager.TryGetAssignedProfile(Svc.ClientState.TerritoryType, DataCenter.Job, combatType, out profile);
	}

	public static bool TryGetScheduledGCD(out IAction? act)
		=> TryGetScheduledAction(true, out act);

	public static bool TryGetScheduledAbility(out IAction? act)
		=> TryGetScheduledAction(false, out act);

	private static bool TryGetScheduledAction(bool wantsGcd, out IAction? act)
	{
		act = null;

		if (!DataCenter.IsInDuty || !DataCenter.InCombat || DataCenter.CurrentRotation == null)
		{
			ResetState();
			return false;
		}

		if (!TryGetActiveProfile(out var profile) || profile == null || profile.Actions.Count == 0)
		{
			ResetState();
			return false;
		}

		var signature = BuildActiveProfileSignature(profile.ProfileId);
		var combatTime = DataCenter.CombatTimeRaw;
		if (_activeProfileSignature != signature || combatTime + 0.01f < _lastCombatTime)
		{
			ResetState(signature);
		}

		_lastCombatTime = combatTime;
		SyncWithRecentActions(profile, combatTime);
		AdvanceExpiredActions(profile, combatTime);

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
		=> combatTime >= entry.CombatTimeSeconds - ExecuteLeadSeconds
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

	private static IAction? ResolveAction(uint actionId)
	{
		var rotationActions = DataCenter.CurrentRotation?.AllActions ?? [];
		var dutyActions = DataCenter.CurrentDutyRotation?.AllActions ?? [];
		var id = (ActionID)actionId;

		return id.GetActionFromID(true, rotationActions, dutyActions)
			?? id.GetActionFromID(false, rotationActions, dutyActions);
	}

	private static string BuildActiveProfileSignature(string profileId)
		=> $"{profileId}:{Svc.ClientState.TerritoryType}:{(int)DataCenter.Job}:{(int)(DataCenter.IsPvP ? CombatType.PvP : CombatType.PvE)}";

	private static void ResetState(string signature = "")
	{
		_activeProfileSignature = signature;
		_nextActionIndex = 0;
		_lastObservedActionTime = DateTime.Now;
		_lastCombatTime = 0;
	}
}