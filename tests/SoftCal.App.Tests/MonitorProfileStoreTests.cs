using System.IO;
using AwesomeAssertions;
using SoftCal.App.Services;
using Xunit;

namespace SoftCal.App.Tests;

/// <summary>Each monitor's profiles: always one at least, made, renamed and deleted at will, and read from the files
/// of before profiles, which kept the settings per picture mode.</summary>
public sealed class MonitorProfileStoreTests : IDisposable
{
    private const string Key = "BNQ7F2B-11aedabb1c";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "softcal-tests", Guid.NewGuid().ToString("N"));

    private MonitorProfileStore Store => new(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void A_monitor_starts_with_one_profile_named_Default()
    {
        var profiles = Store.List(Key);

        profiles.Should().ContainSingle().Which.Name.Should().Be("Default");
        Store.Active(Key).Name.Should().Be("Default");
        Store.LastSaved(Key).Should().BeNull("nothing was written");
    }

    [Fact]
    public void The_picture_modes_of_before_become_profiles_of_their_names()
    {
        Write("""
            {
              "lastMode": "2-DIM",
              "modes": {
                "3-GAME": { "pictureMode": "3-GAME", "brightness": 60, "contrast": 50 },
                "1-DESK": { "pictureMode": "1-DESK", "brightness": 28, "contrast": 46, "redGain": 84, "greenGain": 78, "blueGain": 85, "colorPreset": "User" },
                "": { "brightness": 40, "contrast": 50 },
                "2-DIM": { "pictureMode": "2-DIM", "brightness": 18, "contrast": 46 }
              }
            }
            """);

        var profiles = Store.List(Key);

        profiles.Select(p => p.Name).Should().Equal("Default", "1-DESK", "2-DIM", "3-GAME");
        Store.Active(Key).Name.Should().Be("2-DIM", "the last mode used is the profile in use");
        var desk = Store.Find(Key, "1-DESK")!.Settings;
        (desk.Brightness, desk.Contrast, desk.Gains()).Should().Be((28, 46, (84, 78, 85)));
        Store.Find(Key, "Default")!.Settings.Brightness.Should().Be(40, "the unnamed mode");
    }

    [Fact]
    public void The_oldest_file_one_settings_object_is_one_profile()
    {
        Write("""{ "pictureMode": "Standard", "brightness": 50, "contrast": 70 }""");

        var profile = Store.List(Key).Should().ContainSingle().Subject;

        profile.Name.Should().Be("Standard");
        profile.Settings.Contrast.Should().Be(70);
    }

    [Fact]
    public void Saving_writes_the_new_form_which_reads_back_whole()
    {
        Write("""{ "lastMode": "1-DESK", "modes": { "1-DESK": { "brightness": 28, "contrast": 46 } } }""");

        Store.SaveSettings(Key, "1-DESK", Store.Find(Key, "1-DESK")!.Settings with { Brightness = 30 });

        var text = File.ReadAllText(Path.Combine(_folder, Key + ".json"));
        text.Should().Contain("\"profiles\"").And.NotContain("\"modes\"");
        Store.Find(Key, "1-DESK")!.Settings.Brightness.Should().Be(30);
        Store.Active(Key).Name.Should().Be("1-DESK");
    }

    [Fact]
    public void Profiles_are_made_renamed_and_deleted()
    {
        var store = Store;
        store.SaveSettings(Key, "Default", new MonitorSettings { Brightness = 50, Contrast = 50 });

        store.Create(Key, "Night", new MonitorSettings { Brightness = 15, Contrast = 50 });
        store.Active(Key).Name.Should().Be("Night", "a new profile is the one in use");
        store.Rename(Key, "Night", "Evening");
        store.Active(Key).Name.Should().Be("Evening", "renaming keeps it in use");
        store.Find(Key, "Evening")!.Settings.Brightness.Should().Be(15);
        store.Find(Key, "Night").Should().BeNull();

        store.Delete(Key, "Evening");
        store.List(Key).Select(p => p.Name).Should().Equal("Default");
        store.Active(Key).Name.Should().Be("Default", "the first left is in use when the one deleted was");
        var deleteLast = () => store.Delete(Key, "Default");
        deleteLast.Should().Throw<InvalidOperationException>("a monitor keeps at least one profile");
    }

    [Fact]
    public void Names_are_unique_whatever_their_case_and_kept_short()
    {
        Store.Create(Key, "Day", new MonitorSettings());

        var twice = () => Store.Create(Key, "day", new MonitorSettings());

        twice.Should().Throw<ArgumentException>().WithMessage("*already*");
        MonitorProfileStore.CheckName("  ", ["Default"]).Should().NotBeNull();
        MonitorProfileStore.CheckName(new string('x', 41), ["Default"]).Should().NotBeNull();
        MonitorProfileStore.CheckName("Default", ["Default"], except: "Default").Should().BeNull("renaming to its own name");
        MonitorProfileStore.CheckName(" Games ", ["Default"]).Should().BeNull();
    }

    [Fact]
    public void A_profile_keeps_its_calibration_and_an_older_installed_one_finds_its_profile()
    {
        Write("""{ "lastMode": "1-DESK", "modes": { "1-DESK": { "brightness": 28 }, "2-DIM": { "brightness": 18 } } }""");
        var older = new ProfileCalibration("SoftCal ZOWIE XL LCD 1-DESK 2026-10-18 1309.icc", "keep native tone, neutral grays", DateTimeOffset.Now);

        var owner = Store.Adopt(Key, "1-DESK", older);
        var again = Store.Adopt(Key, "something else", older);
        var nobody = Store.Adopt(Key, "4-NONE", older with { File = "other.icc" });

        owner.Should().Be("1-DESK");
        again.Should().Be("1-DESK", "a calibration already held stays with its profile");
        nobody.Should().BeNull("no profile has that name");
        Store.Find(Key, "1-DESK")!.Calibration.Should().Be(older);
        Store.Find(Key, "2-DIM")!.Calibration.Should().BeNull();
        Store.SetCalibration(Key, "1-DESK", null);
        Store.Find(Key, "1-DESK")!.Calibration.Should().BeNull();
    }

    [Fact]
    public void Whether_the_panel_is_TN_belongs_to_the_monitor_whatever_the_profile()
    {
        Store.IsTnPanel(Key).Should().BeNull("never said");

        Store.SetTnPanel(Key, true);
        Store.Create(Key, "Night", new MonitorSettings { Brightness = 15 });
        Store.SaveSettings(Key, "Default", new MonitorSettings { Brightness = 50 });

        Store.IsTnPanel(Key).Should().BeTrue("profile changes keep it");
    }

    [Fact]
    public void The_monitors_settings_now_are_kept_apart_from_its_profiles()
    {
        var store = Store;
        store.SetFromMonitor(Key, "Default", new MonitorSettings { Brightness = 50 });
        store.Create(Key, "Night", new MonitorSettings { Brightness = 15 });

        // Choosing Night does not change the monitor: its settings now stay Default's until Night is loaded.
        store.SetActive(Key, "Night");
        store.Current(Key).Brightness.Should().Be(50);

        store.SaveCurrent(Key, new MonitorSettings { Brightness = 45 });
        store.Find(Key, "Default")!.Settings.Brightness.Should().Be(50, "typing what the monitor has now does not change a profile");

        store.SetFromMonitor(Key, "Night", new MonitorSettings { Brightness = 20 });
        store.Find(Key, "Night")!.Settings.Brightness.Should().Be(20, "saved from the monitor");
        store.Current(Key).Brightness.Should().Be(20);
        store.Delete(Key, "Night");
        store.Current(Key).Brightness.Should().Be(20, "the monitor itself keeps its settings");
    }

    [Fact]
    public void Before_they_were_kept_apart_the_profile_in_use_had_the_monitors_settings()
    {
        Write("""{ "lastMode": "2-DIM", "modes": { "1-DESK": { "brightness": 28 }, "2-DIM": { "brightness": 18 } } }""");

        Store.Current(Key).Brightness.Should().Be(18);
    }

    [Fact]
    public void An_unreadable_file_is_kept_aside_and_the_monitor_starts_again()
    {
        Write("{ not json");

        Store.List(Key).Should().ContainSingle().Which.Name.Should().Be("Default");
        File.Exists(Path.Combine(_folder, Key + ".json.damaged")).Should().BeTrue();
    }

    private void Write(string json)
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, Key + ".json"), json);
    }
}
