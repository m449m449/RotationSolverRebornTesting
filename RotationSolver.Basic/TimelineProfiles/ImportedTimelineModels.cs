namespace RotationSolver.Basic.TimelineProfiles;

public sealed class ImportedTimelineProfile
{
	[JsonProperty("format")]
	public string Format { get; set; } = string.Empty;

	[JsonProperty("version")]
	public int Version { get; set; }

	[JsonProperty("profileId")]
	public string ProfileId { get; set; } = string.Empty;

	[JsonProperty("profileName")]
	public string ProfileName { get; set; } = string.Empty;

	[JsonProperty("source")]
	public ImportedTimelineSource Source { get; set; } = new();

	[JsonProperty("syncs")]
	public List<ImportedTimelineSync> Syncs { get; set; } = [];

	[JsonProperty("actions")]
	public List<ImportedTimelineAction> Actions { get; set; } = [];
}

public sealed class ImportedTimelineSource
{
	[JsonProperty("provider")]
	public string Provider { get; set; } = string.Empty;

	[JsonProperty("url")]
	public string Url { get; set; } = string.Empty;

	[JsonProperty("fightId")]
	public string FightId { get; set; } = string.Empty;

	[JsonProperty("sourceId")]
	public string SourceId { get; set; } = string.Empty;

	[JsonProperty("sourceJob")]
	public string SourceJob { get; set; } = string.Empty;

	[JsonProperty("fightName")]
	public string FightName { get; set; } = string.Empty;

	[JsonProperty("reportTitle")]
	public string ReportTitle { get; set; } = string.Empty;

	[JsonProperty("fightStartTime")]
	public long? FightStartTime { get; set; }

	[JsonProperty("fightEndTime")]
	public long? FightEndTime { get; set; }

	[JsonProperty("reportStartTime")]
	public long? ReportStartTime { get; set; }

	[JsonProperty("reportEndTime")]
	public long? ReportEndTime { get; set; }

	[JsonProperty("extractedAt")]
	public DateTimeOffset ExtractedAt { get; set; }
}

public sealed class ImportedTimelineAction
{
	[JsonProperty("combatTime")]
	public double CombatTimeSeconds { get; set; }

	[JsonProperty("timestamp")]
	public long Timestamp { get; set; }

	[JsonProperty("name")]
	public string Name { get; set; } = string.Empty;

	[JsonProperty("id")]
	public uint Id { get; set; }

	[JsonProperty("icon")]
	public string Icon { get; set; } = string.Empty;

	[JsonProperty("type")]
	public string Type { get; set; } = string.Empty;

	[JsonProperty("sourceId")]
	public uint SourceId { get; set; }

	[JsonProperty("targetId")]
	public uint TargetId { get; set; }

	[JsonProperty("sourceIsFriendly")]
	public bool SourceIsFriendly { get; set; }

	[JsonProperty("targetIsFriendly")]
	public bool TargetIsFriendly { get; set; }
}

public sealed class ImportedTimelineSync
{
	[JsonProperty("combatTime")]
	public double CombatTimeSeconds { get; set; }

	[JsonProperty("type")]
	public string Type { get; set; } = string.Empty;

	[JsonProperty("pattern")]
	public string Pattern { get; set; } = string.Empty;

	[JsonProperty("regex")]
	public bool Regex { get; set; }

	[JsonProperty("id")]
	public uint Id { get; set; }

	[JsonProperty("name")]
	public string Name { get; set; } = string.Empty;

	[JsonProperty("sourceId")]
	public uint SourceId { get; set; }

	[JsonProperty("phase")]
	public string Phase { get; set; } = string.Empty;

	[JsonProperty("windowBefore")]
	public double WindowBeforeSeconds { get; set; } = 15;

	[JsonProperty("windowAfter")]
	public double WindowAfterSeconds { get; set; } = 15;

	[JsonProperty("once")]
	public bool Once { get; set; } = true;
}
