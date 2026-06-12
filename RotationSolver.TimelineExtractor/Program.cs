using System.Globalization;
using Microsoft.Playwright;
using Newtonsoft.Json;

namespace RotationSolver.TimelineExtractor;

internal static class Program
{
	private const string TimelineSelector = "#timeline-container .timeline-line .timeline-box";
	private const int DefaultTimeoutMs = 60000;
	private const string DefaultProfileDirectoryName = "RotationSolverTimelineExtractor";

	public static async Task<int> Main(string[] args)
	{
		try
		{
			var options = CliOptions.Parse(args);

			if (options.ShowHelp)
			{
				PrintHelp();
				return 0;
			}

			var profile = await ExtractProfileAsync(options).ConfigureAwait(false);
			var outputPath = ResolveOutputPath(options, profile);

			var directory = Path.GetDirectoryName(outputPath);
			if (!string.IsNullOrWhiteSpace(directory))
			{
				Directory.CreateDirectory(directory);
			}

			await File.WriteAllTextAsync(outputPath,
				JsonConvert.SerializeObject(profile, Formatting.Indented)).ConfigureAwait(false);

			Console.WriteLine($"Extracted {profile.Actions.Count} timeline events.");
			Console.WriteLine($"Output: {outputPath}");
			return 0;
		}
		catch (ArgumentException ex)
		{
			Console.Error.WriteLine(ex.Message);
			Console.Error.WriteLine();
			PrintHelp();
			return 2;
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"Timeline extraction failed: {ex.Message}");
			return 1;
		}
	}

	private static async Task<ExtractedTimelineProfile> ExtractProfileAsync(CliOptions options)
	{
		using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
		var profileDirectory = ResolveProfileDirectory(options);
		Directory.CreateDirectory(profileDirectory);

		await using var context = await LaunchBrowserContextAsync(playwright, options, profileDirectory).ConfigureAwait(false);
		var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync().ConfigureAwait(false);
		page.SetDefaultTimeout(options.TimeoutMs);

		Console.WriteLine($"Browser profile: {profileDirectory}");
		if (options.Headed)
		{
			Console.WriteLine("A visible browser window has been opened.");
			Console.WriteLine("If FFLogs shows a Cloudflare challenge, complete it in that window before the timeout expires.");
		}

		await page.GotoAsync(options.Url, new PageGotoOptions
		{
			Timeout = options.TimeoutMs,
			WaitUntil = WaitUntilState.DOMContentLoaded,
		}).ConfigureAwait(false);

		await WaitForTimelineAsync(page, options, profileDirectory).ConfigureAwait(false);

		var rawSnapshot = await page.EvaluateAsync<string>("""
			() => JSON.stringify((() => {
				const pageUrl = new URL(location.href);
				const sourceId = pageUrl.searchParams.get('source') || '';
				const sourceLink = Array.from(document.querySelectorAll('a.actor-menu-link')).find((anchor) => {
					const href = anchor.getAttribute('href');
					if (!href) {
						return false;
					}

					try {
						return new URL(href, location.href).searchParams.get('source') === sourceId;
					} catch {
						return false;
					}
				});

				const sourceJob = (sourceLink?.className || '')
					.split(/\s+/)
					.filter(Boolean)
					.find((value) => value !== 'actor-menu-link' && value !== 'has-submenu' && value !== 'submenu-open') || '';

				const actions = Array.from(document.querySelectorAll('#timeline-container .timeline-line .timeline-box'))
					.map((node) => {
						const mouseOver = node.getAttribute('onmouseover') || '';
						const start = mouseOver.indexOf('printEvent(');
						const end = mouseOver.lastIndexOf(', true, 0)');

						if (start < 0 || end <= start) {
							return null;
						}

						const jsonText = mouseOver.slice(start + 'printEvent('.length, end).trim();
						let eventData;
						try {
							eventData = JSON.parse(jsonText);
						} catch {
							return null;
						}

						const timestamp = Number(eventData.timestamp || 0);
						const fightStartTime = typeof globalThis.filterFightStartTime === 'number'
							? globalThis.filterFightStartTime
							: 0;

						return {
							combatTimeSeconds: fightStartTime > 0 ? (timestamp - fightStartTime) / 1000 : 0,
							timestamp,
							name: eventData.ability?.name || '',
							id: Number(eventData.ability?.guid || 0),
							icon: eventData.ability?.abilityIcon || '',
							type: eventData.type || '',
							sourceId: Number(eventData.sourceID || 0),
							targetId: Number(eventData.targetID || 0),
							sourceIsFriendly: Boolean(eventData.sourceIsFriendly),
							targetIsFriendly: Boolean(eventData.targetIsFriendly),
						};
					})
					.filter((eventData) => eventData !== null)
					.sort((left, right) => left.timestamp - right.timestamp || left.id - right.id);

				return {
					provider: location.hostname.includes('fflogs') ? 'FFLogs' : location.hostname,
					currentUrl: location.href,
					reportTitle: document.title || '',
					bossCaption: document.querySelector('.report-overview-boss-caption')?.textContent?.trim() || '',
					fightId: pageUrl.searchParams.get('fight') || '',
					sourceId,
					sourceJob,
					fightStartTime: typeof globalThis.filterFightStartTime === 'number' ? globalThis.filterFightStartTime : null,
					fightEndTime: typeof globalThis.filterFightEndTime === 'number' ? globalThis.filterFightEndTime : null,
					reportStartTime: typeof globalThis.start_time === 'number' ? globalThis.start_time : null,
					reportEndTime: typeof globalThis.end_time === 'number' ? globalThis.end_time : null,
					actions,
				};
			})())
		""").ConfigureAwait(false);

		var snapshot = JsonConvert.DeserializeObject<ExtractionSnapshot>(rawSnapshot)
			?? throw new InvalidOperationException("Failed to deserialize the extracted timeline snapshot.");

		if (snapshot.Actions.Count == 0)
		{
			throw new InvalidOperationException("No timeline actions were found on the page.");
		}

		var profileName = !string.IsNullOrWhiteSpace(options.ProfileName)
			? options.ProfileName
			: BuildDefaultProfileName(snapshot);

		return new ExtractedTimelineProfile
		{
			ProfileId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
			ProfileName = profileName,
			Source = new ExtractedTimelineSource
			{
				Provider = snapshot.Provider,
				Url = snapshot.CurrentUrl,
				FightId = snapshot.FightId,
				SourceId = snapshot.SourceId,
				SourceJob = options.SourceJob ?? snapshot.SourceJob,
				FightName = snapshot.BossCaption,
				ReportTitle = snapshot.ReportTitle,
				FightStartTime = snapshot.FightStartTime,
				FightEndTime = snapshot.FightEndTime,
				ReportStartTime = snapshot.ReportStartTime,
				ReportEndTime = snapshot.ReportEndTime,
				ExtractedAt = DateTimeOffset.UtcNow,
			},
			Actions = [.. snapshot.Actions
				.Where(action => action.Id > 0 && action.CombatTimeSeconds >= 0)
				.OrderBy(action => action.CombatTimeSeconds)
				.ThenBy(action => action.Id)
				.Select(action => new ExtractedTimelineAction
				{
					CombatTimeSeconds = Math.Round(action.CombatTimeSeconds, 3),
					Timestamp = action.Timestamp,
					Name = action.Name,
					Id = action.Id,
					Icon = action.Icon,
					Type = action.Type,
					SourceId = action.SourceId,
					TargetId = action.TargetId,
					SourceIsFriendly = action.SourceIsFriendly,
					TargetIsFriendly = action.TargetIsFriendly,
				})],
		};
	}

	private static async Task<IBrowserContext> LaunchBrowserContextAsync(IPlaywright playwright, CliOptions options, string profileDirectory)
	{
		var executablePath = FindBrowserExecutable();

		try
		{
			return await playwright.Chromium.LaunchPersistentContextAsync(profileDirectory,
				CreateLaunchOptions(options, executablePath, executablePath == null ? "msedge" : null)).ConfigureAwait(false);
		}
		catch (PlaywrightException)
		{
			if (!string.IsNullOrWhiteSpace(executablePath))
			{
				try
				{
					return await playwright.Chromium.LaunchPersistentContextAsync(profileDirectory,
						CreateLaunchOptions(options, null, "msedge")).ConfigureAwait(false);
				}
				catch (PlaywrightException)
				{
					// Fall through to the default Chromium launcher.
				}
			}

			return await playwright.Chromium.LaunchPersistentContextAsync(profileDirectory,
				CreateLaunchOptions(options, null, null)).ConfigureAwait(false);
		}
	}

	private static BrowserTypeLaunchPersistentContextOptions CreateLaunchOptions(CliOptions options, string? executablePath, string? channel)
	{
		return new BrowserTypeLaunchPersistentContextOptions
		{
			Headless = !options.Headed,
			ExecutablePath = executablePath,
			Channel = channel,
			Locale = "ja-JP",
			TimezoneId = "Asia/Tokyo",
			ViewportSize = new ViewportSize { Width = 1600, Height = 1200 },
			Args =
			[
				"--disable-blink-features=AutomationControlled",
				"--lang=ja-JP,ja,en-US,en"
			],
		};
	}

	private static async Task WaitForTimelineAsync(IPage page, CliOptions options, string profileDirectory)
	{
		try
		{
			await page.WaitForSelectorAsync(TimelineSelector, new PageWaitForSelectorOptions
			{
				Timeout = options.TimeoutMs,
				State = WaitForSelectorState.Attached,
			}).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex.Message.Contains("Timeout", StringComparison.OrdinalIgnoreCase))
		{
			var pageTitle = await SafeGetTitleAsync(page).ConfigureAwait(false);
			var bodyText = await SafeGetBodyTextAsync(page).ConfigureAwait(false);

			if (LooksLikeCloudflareChallenge(pageTitle, bodyText))
			{
				if (options.Headed)
				{
					throw new InvalidOperationException(
						$"FFLogs is showing a Cloudflare challenge in the browser window. Complete the challenge there, then rerun with a larger --timeout-ms if needed. Browser profile: {profileDirectory}");
				}

				throw new InvalidOperationException(
					$"FFLogs blocked the automation browser with a Cloudflare challenge. Rerun with --headed to complete the challenge once. Browser profile: {profileDirectory}");
			}

			throw new InvalidOperationException(
				$"Timeline selector did not appear before timeout. Page title: {pageTitle}");
		}
	}

	private static async Task<string> SafeGetTitleAsync(IPage page)
	{
		try
		{
			return await page.TitleAsync().ConfigureAwait(false);
		}
		catch
		{
			return string.Empty;
		}
	}

	private static async Task<string> SafeGetBodyTextAsync(IPage page)
	{
		try
		{
			return await page.Locator("body").InnerTextAsync(new LocatorInnerTextOptions
			{
				Timeout = 5000,
			}).ConfigureAwait(false);
		}
		catch
		{
			return string.Empty;
		}
	}

	private static bool LooksLikeCloudflareChallenge(string pageTitle, string bodyText)
	{
		return pageTitle.Contains("Just a moment", StringComparison.OrdinalIgnoreCase)
			|| pageTitle.Contains("しばらくお待ちください", StringComparison.OrdinalIgnoreCase)
			|| bodyText.Contains("Enable JavaScript and cookies to continue", StringComparison.OrdinalIgnoreCase)
			|| bodyText.Contains("JavaScript と Cookie を有効", StringComparison.OrdinalIgnoreCase)
			|| bodyText.Contains("challenge", StringComparison.OrdinalIgnoreCase);
	}

	private static string ResolveProfileDirectory(CliOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.ProfileDirectory))
		{
			return Path.GetFullPath(options.ProfileDirectory);
		}

		return Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			DefaultProfileDirectoryName,
			"BrowserProfile");
	}

	private static string? FindBrowserExecutable()
	{
		var candidates = new[]
		{
			@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
			@"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
			@"C:\Program Files\Google\Chrome\Application\chrome.exe",
		};

		foreach (var candidate in candidates)
		{
			if (File.Exists(candidate))
			{
				return candidate;
			}
		}

		return null;
	}

	private static string ResolveOutputPath(CliOptions options, ExtractedTimelineProfile profile)
	{
		if (!string.IsNullOrWhiteSpace(options.OutputPath))
		{
			return Path.GetFullPath(options.OutputPath);
		}

		var fileName = $"{MakeSafeFileName(profile.ProfileName)}.json";
		return Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, fileName));
	}

	private static string BuildDefaultProfileName(ExtractionSnapshot snapshot)
	{
		var pieces = new List<string>();

		if (!string.IsNullOrWhiteSpace(snapshot.BossCaption))
		{
			pieces.Add(snapshot.BossCaption);
		}

		if (!string.IsNullOrWhiteSpace(snapshot.SourceJob))
		{
			pieces.Add(snapshot.SourceJob);
		}

		if (!string.IsNullOrWhiteSpace(snapshot.FightId))
		{
			pieces.Add($"fight-{snapshot.FightId}");
		}

		if (pieces.Count == 0)
		{
			pieces.Add("timeline-profile");
		}

		return string.Join(" - ", pieces);
	}

	private static string MakeSafeFileName(string value)
	{
		Span<char> invalid = stackalloc char[Path.GetInvalidFileNameChars().Length];
		Path.GetInvalidFileNameChars().CopyTo(invalid);

		var builder = new char[value.Length];
		for (var i = 0; i < value.Length; i++)
		{
			builder[i] = invalid.IndexOf(value[i]) >= 0 ? '_' : value[i];
		}

		return new string(builder).Trim().Trim('.');
	}

	private static void PrintHelp()
	{
		Console.WriteLine("RotationSolver timeline extractor");
		Console.WriteLine();
		Console.WriteLine("Required:");
		Console.WriteLine("  --url <logs page url>");
		Console.WriteLine();
		Console.WriteLine("Optional:");
		Console.WriteLine("  --out <output json path>");
		Console.WriteLine("  --name <profile name>");
		Console.WriteLine("  --source-job <job name override>");
		Console.WriteLine("  --profile-dir <persistent browser profile dir>");
		Console.WriteLine($"  --timeout-ms <page timeout>    default: {DefaultTimeoutMs}");
		Console.WriteLine("  --headed                       use a visible browser window");
		Console.WriteLine();
		Console.WriteLine("Notes:");
		Console.WriteLine("  FFLogs may require a visible first run because of Cloudflare.");
		Console.WriteLine("  When that happens, rerun with --headed and keep the same profile dir.");
		Console.WriteLine("  --help");
	}

	private sealed class CliOptions
	{
		public string Url { get; private set; } = string.Empty;
		public string? OutputPath { get; private set; }
		public string? ProfileName { get; private set; }
		public string? SourceJob { get; private set; }
		public string? ProfileDirectory { get; private set; }
		public int TimeoutMs { get; private set; } = DefaultTimeoutMs;
		public bool Headed { get; private set; }
		public bool ShowHelp { get; private set; }

		public static CliOptions Parse(string[] args)
		{
			var options = new CliOptions();

			for (var i = 0; i < args.Length; i++)
			{
				switch (args[i])
				{
					case "--help":
					case "-h":
						options.ShowHelp = true;
						break;

					case "--url":
						options.Url = ReadValue(args, ref i, "--url");
						break;

					case "--out":
						options.OutputPath = ReadValue(args, ref i, "--out");
						break;

					case "--name":
						options.ProfileName = ReadValue(args, ref i, "--name");
						break;

					case "--source-job":
						options.SourceJob = ReadValue(args, ref i, "--source-job");
						break;

					case "--profile-dir":
						options.ProfileDirectory = ReadValue(args, ref i, "--profile-dir");
						break;

					case "--timeout-ms":
						if (!int.TryParse(ReadValue(args, ref i, "--timeout-ms"), out var timeoutMs) || timeoutMs <= 0)
						{
							throw new ArgumentException("--timeout-ms must be a positive integer.");
						}
						options.TimeoutMs = timeoutMs;
						break;

					case "--headed":
						options.Headed = true;
						break;

					default:
						throw new ArgumentException($"Unknown argument: {args[i]}");
				}
			}

			if (!options.ShowHelp)
			{
				if (string.IsNullOrWhiteSpace(options.Url))
				{
					throw new ArgumentException("--url is required.");
				}

				if (!Uri.TryCreate(options.Url, UriKind.Absolute, out _))
				{
					throw new ArgumentException("--url must be an absolute URL.");
				}
			}

			return options;
		}

		private static string ReadValue(string[] args, ref int index, string option)
		{
			if (index + 1 >= args.Length)
			{
				throw new ArgumentException($"{option} requires a value.");
			}

			index++;
			return args[index];
		}
	}

	private sealed class ExtractionSnapshot
	{
		[JsonProperty("provider")]
		public string Provider { get; set; } = string.Empty;

		[JsonProperty("currentUrl")]
		public string CurrentUrl { get; set; } = string.Empty;

		[JsonProperty("reportTitle")]
		public string ReportTitle { get; set; } = string.Empty;

		[JsonProperty("bossCaption")]
		public string BossCaption { get; set; } = string.Empty;

		[JsonProperty("fightId")]
		public string FightId { get; set; } = string.Empty;

		[JsonProperty("sourceId")]
		public string SourceId { get; set; } = string.Empty;

		[JsonProperty("sourceJob")]
		public string SourceJob { get; set; } = string.Empty;

		[JsonProperty("fightStartTime")]
		public long? FightStartTime { get; set; }

		[JsonProperty("fightEndTime")]
		public long? FightEndTime { get; set; }

		[JsonProperty("reportStartTime")]
		public long? ReportStartTime { get; set; }

		[JsonProperty("reportEndTime")]
		public long? ReportEndTime { get; set; }

		[JsonProperty("actions")]
		public List<SnapshotAction> Actions { get; set; } = [];
	}

	private sealed class SnapshotAction
	{
		[JsonProperty("combatTimeSeconds")]
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

	private sealed class ExtractedTimelineProfile
	{
		[JsonProperty("format")]
		public string Format { get; set; } = "RotationSolverTimelineProfile";

		[JsonProperty("version")]
		public int Version { get; set; } = 1;

		[JsonProperty("profileId")]
		public string ProfileId { get; set; } = string.Empty;

		[JsonProperty("profileName")]
		public string ProfileName { get; set; } = string.Empty;

		[JsonProperty("source")]
		public ExtractedTimelineSource Source { get; set; } = new();

		[JsonProperty("actions")]
		public List<ExtractedTimelineAction> Actions { get; set; } = [];
	}

	private sealed class ExtractedTimelineSource
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

	private sealed class ExtractedTimelineAction
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
}