using DeskHush.Core.Models;
using DeskHush.Core.Services;
using DeskHush.Windows.ContextMenu;
using DeskHush.Windows.Startup;
using DeskHush.Windows.Windows;

namespace DeskHush.Tests;

internal static class Program
{
    private static readonly List<(string Name, Func<Task> Test)> Tests =
    [
        ("Popup matching modes", TestPopupMatchingModes),
        ("Popup composite matching", TestPopupCompositeMatching),
        ("Popup rule safety validation", TestPopupRuleValidation),
        ("Stable ids", TestStableIds),
        ("JSON settings round trip", TestJsonSettingsRoundTrip),
        ("JSON settings concurrent saves", TestJsonSettingsConcurrentSaves),
        ("JSON settings null collection recovery", TestJsonSettingsNullCollectionRecovery),
        ("JSON settings corruption recovery", TestJsonSettingsCorruptionRecovery),
        ("Visible-window catalog smoke", TestWindowCatalog),
        ("Window-record deduplication and ordering", WindowRecordingTests.TestDeduplicationAndOrdering),
        ("Window-record capacity and clear", WindowRecordingTests.TestCapacityAndClear),
        ("Window-record concurrent deduplication", WindowRecordingTests.TestConcurrentDeduplication),
        ("Window-record absolute retry schedule", WindowRecordingTests.TestSnapshotScheduleAndStableTitle),
        ("Window-record best snapshot retention", WindowRecordingTests.TestSnapshotRetainsBestMetadata),
        ("Window-record clear epoch", WindowRecordingTests.TestClearInvalidatesQueuedWork),
        ("Window-record bounded consumers", WindowRecordingTests.TestBoundedConsumerConcurrency),
        ("Window-record lifecycle serialization", WindowRecordingTests.TestLifecycleSerializesStartAndDispose),
        ("Update release version parsing", UpdateCheckerTests.TestVersionParsing),
        ("Update newer-release detection", UpdateCheckerTests.TestNewerReleaseDetection),
        ("Update current/invalid release rejection", UpdateCheckerTests.TestCurrentAndInvalidReleaseRejection),
        ("Context-menu enumeration smoke", TestContextMenuEnumeration),
        ("StartupApproved binary semantics", StartupTests.TestStartupApprovalSemantics),
        ("Self-start approval semantics", StartupTests.TestSelfStartupApprovalSemantics),
        ("StartupApproved registry mapping", StartupTests.TestStartupApprovalMapping),
        ("StartupApproved state round trip", StartupTests.TestStartupApprovalStateRoundTrip),
        ("Startup enumeration smoke", TestStartupEnumeration)
    ];

    public static async Task<int> Main()
    {
        var failures = new List<string>();
        foreach (var (name, test) in Tests)
        {
            try
            {
                await test();
                Console.WriteLine($"PASS  {name}");
            }
            catch (Exception exception)
            {
                failures.Add(name);
                Console.Error.WriteLine($"FAIL  {name}: {exception.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{Tests.Count - failures.Count}/{Tests.Count} tests passed.");
        return failures.Count == 0 ? 0 : 1;
    }

    private static Task TestPopupMatchingModes()
    {
        Assert(PopupRuleMatcher.MatchText("今日热点广告", "热点", TextMatchMode.Contains), "Contains should match.");
        Assert(PopupRuleMatcher.MatchText("PROMO", "promo", TextMatchMode.Exact), "Exact should ignore case.");
        Assert(PopupRuleMatcher.MatchText("News - Special Offer", "News*Offer", TextMatchMode.Wildcard), "Wildcard should match.");
        Assert(PopupRuleMatcher.MatchText("Advertisement 42", "^advertisement\\s+\\d+$", TextMatchMode.Regex), "Regex should match.");
        Assert(!PopupRuleMatcher.MatchText("Main window", "popup", TextMatchMode.Contains), "Unrelated text should not match.");
        return Task.CompletedTask;
    }

    private static Task TestPopupCompositeMatching()
    {
        var rule = new PopupRule
        {
            Name = "Test",
            ProcessName = "sample.exe",
            ProcessPath = @"C:\Apps\Sample.exe",
            WindowClass = "AdWindow*",
            TitlePattern = "Special offer",
            TitleMatchMode = TextMatchMode.Contains
        };

        var window = new WindowInfo(
            (nint)123,
            42,
            "Sample",
            @"c:\apps\sample.exe",
            "Today's special OFFER",
            "AdWindowV2",
            0,
            0,
            400,
            300);

        Assert(PopupRuleMatcher.IsMatch(rule, window), "Composite rule should match normalized values.");
        rule.ProcessName = "another";
        Assert(!PopupRuleMatcher.IsMatch(rule, window), "Wrong process must not match.");
        return Task.CompletedTask;
    }

    private static Task TestPopupRuleValidation()
    {
        var catchAll = new PopupRule { Name = "Unsafe", TitlePattern = "*", TitleMatchMode = TextMatchMode.Wildcard };
        Assert(!PopupRuleValidator.Validate(catchAll).Succeeded, "Catch-all rule should be rejected.");

        var protectedRule = new PopupRule { Name = "Unsafe", ProcessName = "explorer.exe", WindowClass = "CabinetWClass" };
        Assert(!PopupRuleValidator.Validate(protectedRule).Succeeded, "Protected process should be rejected.");

        var protectedPathRule = new PopupRule
        {
            Name = "Unsafe path",
            ProcessPath = @"C:\Windows\System32\sihost.exe",
            WindowClass = "SystemWindow"
        };
        Assert(!PopupRuleValidator.Validate(protectedPathRule).Succeeded,
            "A protected process supplied only as a full path should be rejected.");

        var invalidRegex = new PopupRule
        {
            Name = "Invalid regex",
            ProcessName = "sample",
            TitlePattern = "[",
            TitleMatchMode = TextMatchMode.Regex
        };
        Assert(!PopupRuleValidator.Validate(invalidRegex).Succeeded, "Invalid regex should be rejected.");

        var safe = new PopupRule { Name = "Safe", ProcessName = "sample", WindowClass = "AdWindow" };
        Assert(PopupRuleValidator.Validate(safe).Succeeded, "Scoped rule should be accepted.");
        return Task.CompletedTask;
    }

    private static Task TestStableIds()
    {
        var first = StableId.Create("HKCU", "Run", "Example");
        var second = StableId.Create("hkcu", "run", "example");
        var different = StableId.Create("HKCU", "Run", "Other");
        Assert(first == second, "Stable ids should normalize case.");
        Assert(first != different, "Different inputs should produce different ids.");
        Assert(first.Length == 24, "Stable ids should use a compact 96-bit representation.");
        return Task.CompletedTask;
    }

    private static async Task TestJsonSettingsRoundTrip()
    {
        var root = CreateTestDirectory();
        try
        {
            var path = Path.Combine(root, "settings.json");
            var store = new JsonSettingsStore(path);
            var updateCheckAt = new DateTimeOffset(2026, 7, 28, 8, 30, 0, TimeSpan.Zero);
            var settings = new AppSettings
            {
                PopupBlockingEnabled = false,
                WindowRecordingEnabled = false,
                CheckForUpdatesEnabled = false,
                LastUpdateCheckAt = updateCheckAt,
                StartWithWindows = true,
                PopupRules =
                [
                    new PopupRule { Name = "Rule", ProcessName = "sample", WindowClass = "AdWindow", HitCount = 7 }
                ]
            };

            await store.SaveAsync(settings);
            var loaded = await store.LoadAsync();
            Assert(!loaded.PopupBlockingEnabled, "Boolean setting should round-trip.");
            Assert(!loaded.WindowRecordingEnabled, "Window-recording setting should round-trip.");
            Assert(!loaded.CheckForUpdatesEnabled && loaded.LastUpdateCheckAt == updateCheckAt,
                "Update-check settings should round-trip.");
            Assert(loaded.StartWithWindows, "Startup setting should round-trip.");
            Assert(loaded.PopupRules is [{ Name: "Rule", HitCount: 7 }], "Rules should round-trip.");
            Assert(!Directory.EnumerateFiles(root, "*.tmp").Any(), "Atomic-save temporary files should not remain.");
        }
        finally
        {
            DeleteTestDirectory(root);
        }
    }

    private static async Task TestJsonSettingsConcurrentSaves()
    {
        var root = CreateTestDirectory();
        try
        {
            var path = Path.Combine(root, "settings.json");
            var store = new JsonSettingsStore(path);
            var writes = Enumerable.Range(0, 20)
                .Select(index => store.SaveAsync(new AppSettings
                {
                    PopupBlockingEnabled = index % 2 == 0,
                    PopupRules = [new PopupRule { Name = $"Rule {index}", ProcessName = "sample", WindowClass = "AdWindow" }]
                }));

            await Task.WhenAll(writes);
            var loaded = await store.LoadAsync();
            Assert(loaded.PopupRules.Count == 1 && loaded.PopupRules[0].Name.StartsWith("Rule ", StringComparison.Ordinal),
                "Concurrent saves should leave one complete, readable document.");
            Assert(!Directory.EnumerateFiles(root, "*.tmp").Any(), "Concurrent saves should not leave temporary files.");
        }
        finally
        {
            DeleteTestDirectory(root);
        }
    }

    private static async Task TestJsonSettingsCorruptionRecovery()
    {
        var root = CreateTestDirectory();
        try
        {
            var path = Path.Combine(root, "settings.json");
            await File.WriteAllTextAsync(path, "{ invalid json");
            var store = new JsonSettingsStore(path);
            var loaded = await store.LoadAsync();
            Assert(loaded.PopupRules.Count == 0, "Corrupt settings should fall back to defaults.");
            Assert(!File.Exists(path), "Corrupt source should be moved aside.");
            Assert(Directory.EnumerateFiles(root, "settings.json.invalid-*").Count() == 1, "Corrupt source should have one backup.");
        }
        finally
        {
            DeleteTestDirectory(root);
        }
    }

    private static async Task TestJsonSettingsNullCollectionRecovery()
    {
        var root = CreateTestDirectory();
        try
        {
            var path = Path.Combine(root, "settings.json");
            await File.WriteAllTextAsync(path, "{\"popupRules\":null,\"language\":null}");
            var loaded = await new JsonSettingsStore(path).LoadAsync();
            Assert(loaded.PopupRules.Count == 0, "A null rule collection should normalize to an empty collection.");
            Assert(loaded.Language == "zh-CN", "A null language should normalize to the default language.");
        }
        finally
        {
            DeleteTestDirectory(root);
        }
    }

    private static Task TestWindowCatalog()
    {
        var windows = new WindowCatalog().GetVisibleWindows();
        Assert(windows.Select(window => window.Handle).Distinct().Count() == windows.Count, "Window handles should be unique.");
        Assert(windows.All(window => window.ProcessId > 0), "Every window should have a process id.");
        return Task.CompletedTask;
    }

    private static async Task TestContextMenuEnumeration()
    {
        var root = CreateTestDirectory();
        try
        {
            using var manager = new WindowsContextMenuManager(root);
            var first = await manager.GetEntriesAsync();
            var second = await manager.GetEntriesAsync();
            Assert(first.Count > 0, "Windows should expose at least one context-menu entry.");
            Assert(first.All(entry => !string.IsNullOrWhiteSpace(entry.Id) && !string.IsNullOrWhiteSpace(entry.Name)), "Entries need ids and names.");
            Assert(first.Select(entry => entry.Id).Distinct(StringComparer.Ordinal).Count() == first.Count, "Context-menu ids should be unique.");
            Assert(first.Select(entry => entry.Id).SequenceEqual(second.Select(entry => entry.Id)), "Context-menu ordering and ids should be stable.");
        }
        finally
        {
            DeleteTestDirectory(root);
        }
    }

    private static async Task TestStartupEnumeration()
    {
        var root = CreateTestDirectory();
        try
        {
            using var manager = new WindowsStartupManager(root);
            var first = await manager.GetEntriesAsync();
            var second = await manager.GetEntriesAsync();
            Assert(first.Select(entry => entry.Id).Distinct(StringComparer.Ordinal).Count() == first.Count, "Startup ids should be unique.");
            Assert(first.Select(entry => entry.Id).SequenceEqual(second.Select(entry => entry.Id)), "Startup ordering and ids should be stable.");
            Assert(first.All(entry => !string.IsNullOrWhiteSpace(entry.Name)), "Startup entries need names.");
        }
        finally
        {
            DeleteTestDirectory(root);
        }
    }

    private static string CreateTestDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "DeskHush.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTestDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
