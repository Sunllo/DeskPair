using DeskPair.Desktop.Services;

namespace DeskPair.Desktop.Tests;

public class DeviceBookTests
{
    [Fact]
    public void Adding_a_device_keeps_what_the_user_typed()
    {
        DeviceBook book = new DeviceBook().With(new SavedDevice { Target = "123456789", Alias = "Reception", Group = "Taipei" });

        SavedDevice device = book.Devices.ShouldHaveSingleItem();
        device.Target.ShouldBe("123456789");
        device.Alias.ShouldBe("Reception");
        device.Group.ShouldBe("Taipei");
    }

    [Fact]
    public void The_target_is_the_identity_so_saving_twice_updates_rather_than_duplicates()
    {
        DeviceBook book = new DeviceBook()
            .With(new SavedDevice { Target = "192.168.1.5", Alias = "Old" })
            .With(new SavedDevice { Target = "192.168.1.5", Alias = "New", Group = "Site A" });

        SavedDevice device = book.Devices.ShouldHaveSingleItem();
        device.Alias.ShouldBe("New");
        device.Group.ShouldBe("Site A");
    }

    [Fact]
    public void A_target_that_differs_only_in_case_or_spacing_is_the_same_device()
    {
        DeviceBook book = new DeviceBook()
            .With(new SavedDevice { Target = "lan-ABCDEF12" })
            .With(new SavedDevice { Target = " LAN-abcdef12 ", Alias = "Same box" });

        book.Devices.ShouldHaveSingleItem().Alias.ShouldBe("Same box");
    }

    [Fact]
    public void A_device_with_no_target_is_not_stored()
    {
        new DeviceBook().With(new SavedDevice { Target = "   ", Alias = "Nothing" }).Devices.ShouldBeEmpty();
    }

    [Fact]
    public void Removing_uses_the_same_loose_match()
    {
        DeviceBook book = new DeviceBook().With(new SavedDevice { Target = "123456789" }).Without(" 123456789 ");

        book.Devices.ShouldBeEmpty();
    }

    [Fact]
    public void Groups_skip_the_ungrouped_devices_and_come_back_once_each()
    {
        DeviceBook book = new DeviceBook()
            .With(new SavedDevice { Target = "1", Group = "Taipei" })
            .With(new SavedDevice { Target = "2", Group = "taipei" })
            .With(new SavedDevice { Target = "3", Group = "Kaohsiung" })
            .With(new SavedDevice { Target = "4" });

        book.Groups.ShouldBe(["Kaohsiung", "Taipei"], ignoreOrder: true);
    }

    [Fact]
    public void A_group_can_be_made_before_anything_is_in_it()
    {
        DeviceBook book = new DeviceBook().WithGroup("Taipei");

        book.Groups.ShouldBe(["Taipei"]);
        book.Devices.ShouldBeEmpty();
    }

    [Fact]
    public void Making_a_group_that_already_exists_changes_nothing()
    {
        DeviceBook book = new DeviceBook().WithGroup("Taipei").WithGroup(" taipei ").WithGroup("   ");

        book.Groups.ShouldBe(["Taipei"]);
    }

    [Fact]
    public void Removing_a_group_keeps_its_devices_and_ungroups_them()
    {
        DeviceBook book = new DeviceBook()
            .WithGroup("Taipei")
            .With(new SavedDevice { Target = "123456789", Group = "Taipei" })
            .WithoutGroup("Taipei");

        book.Groups.ShouldBeEmpty();
        book.Devices.ShouldHaveSingleItem().Group.ShouldBe(string.Empty);
    }

    [Fact]
    public void Renaming_a_group_takes_its_devices_with_it()
    {
        DeviceBook book = new DeviceBook()
            .WithGroup("Taipei")
            .With(new SavedDevice { Target = "1", Group = "Taipei" })
            .With(new SavedDevice { Target = "2", Group = "Kaohsiung" })
            .RenameGroup("Taipei", "Taipei office");

        book.Groups.ShouldBe(["Kaohsiung", "Taipei office"], ignoreOrder: true);
        book.Devices.First(d => d.Target == "1").Group.ShouldBe("Taipei office");
        book.Devices.First(d => d.Target == "2").Group.ShouldBe("Kaohsiung");
    }

    [Fact]
    public void Renaming_onto_an_existing_group_merges_the_two()
    {
        DeviceBook book = new DeviceBook()
            .With(new SavedDevice { Target = "1", Group = "Taipei" })
            .With(new SavedDevice { Target = "2", Group = "Taipei office" })
            .RenameGroup("Taipei", "Taipei office");

        book.Groups.ShouldBe(["Taipei office"]);
        book.Devices.Count(d => d.Group == "Taipei office").ShouldBe(2);
    }

    [Fact]
    public void A_rename_that_goes_nowhere_is_ignored()
    {
        DeviceBook book = new DeviceBook().WithGroup("Taipei");

        book.RenameGroup("Taipei", "  ").Groups.ShouldBe(["Taipei"]);
        book.RenameGroup("  ", "Other").Groups.ShouldBe(["Taipei"]);
        book.RenameGroup("Taipei", " taipei ").Groups.ShouldBe(["Taipei"]);
    }

    [Fact]
    public void A_group_only_a_device_names_still_shows()
    {
        // Books written before groups were kept by name, and groups typed straight into the device editor.
        DeviceBook book = DeviceBook.FromJson("""{"Devices":[{"Target":"1","Group":"Taipei"}]}""");

        book.Groups.ShouldBe(["Taipei"]);
    }

    [Fact]
    public void Group_names_survive_a_round_trip_through_json()
    {
        DeviceBook book = new DeviceBook().WithGroup("Taipei").WithGroup("Kaohsiung");

        DeviceBook.FromJson(System.Text.Json.JsonSerializer.Serialize(book, DeviceBookJsonForTests.Options))
            .Groups.ShouldBe(["Kaohsiung", "Taipei"], ignoreOrder: true);
    }

    [Fact]
    public void A_group_remembers_being_folded_away()
    {
        DeviceBook book = new DeviceBook().WithGroup("Taipei");

        book.IsCollapsed("Taipei").ShouldBeFalse();
        book = book.WithCollapsed("Taipei", true);
        book.IsCollapsed("Taipei").ShouldBeTrue();
        book.WithCollapsed("Taipei", false).IsCollapsed("Taipei").ShouldBeFalse();
    }

    [Fact]
    public void Folding_the_same_group_twice_stores_it_once()
    {
        DeviceBook book = new DeviceBook().WithGroup("Taipei").WithCollapsed("Taipei", true).WithCollapsed("Taipei", true);

        book.CollapsedGroups.ShouldHaveSingleItem();
    }

    [Fact]
    public void A_renamed_group_stays_folded_and_a_removed_one_is_forgotten()
    {
        DeviceBook folded = new DeviceBook().WithGroup("Taipei").WithCollapsed("Taipei", true);

        folded.RenameGroup("Taipei", "Taipei office").IsCollapsed("Taipei office").ShouldBeTrue();
        folded.WithoutGroup("Taipei").CollapsedGroups.ShouldBeEmpty();
    }

    [Fact]
    public void The_ungrouped_section_folds_under_the_empty_name()
    {
        // Its shown name is a translation, so the empty string is what it is stored as.
        DeviceBook book = new DeviceBook().WithCollapsed(string.Empty, true);

        book.IsCollapsed(string.Empty).ShouldBeTrue();
        book.Groups.ShouldBeEmpty();
    }

    [Fact]
    public void Folded_groups_survive_a_round_trip_through_json()
    {
        DeviceBook book = new DeviceBook().WithGroup("Taipei").WithCollapsed("Taipei", true);

        DeviceBook.FromJson(System.Text.Json.JsonSerializer.Serialize(book, DeviceBookJsonForTests.Options))
            .IsCollapsed("Taipei").ShouldBeTrue();
    }

    [Fact]
    public void Touching_records_the_time_for_a_saved_device_only()
    {
        DateTimeOffset when = new(2026, 9, 17, 10, 0, 0, TimeSpan.Zero);
        DeviceBook book = new DeviceBook().With(new SavedDevice { Target = "123456789" });

        book.Touched("123456789", when).Devices.ShouldHaveSingleItem().LastConnected.ShouldBe(when);
        book.Touched("987654321", when).Devices.ShouldHaveSingleItem().LastConnected.ShouldBe(default);
    }

    [Fact]
    public void A_round_trip_through_json_keeps_every_field()
    {
        DateTimeOffset when = new(2026, 9, 17, 10, 0, 0, TimeSpan.Zero);
        DeviceBook book = new DeviceBook().With(new SavedDevice
        {
            Target = "192.168.1.5:21118",
            Alias = "Reception",
            Group = "Site A",
            Note = "behind the counter",
            LastConnected = when,
        });

        SavedDevice device = DeviceBook.FromJson(System.Text.Json.JsonSerializer.Serialize(book, DeviceBookJsonForTests.Options))
            .Devices.ShouldHaveSingleItem();
        device.Target.ShouldBe("192.168.1.5:21118");
        device.Alias.ShouldBe("Reception");
        device.Group.ShouldBe("Site A");
        device.Note.ShouldBe("behind the counter");
        device.LastConnected.ShouldBe(when);
    }

    [Fact]
    public void A_file_written_by_an_older_build_loses_nothing_it_does_have()
    {
        DeviceBook book = DeviceBook.FromJson("""{"Devices":[{"Target":"123456789","Alias":"Reception"}]}""");

        SavedDevice device = book.Devices.ShouldHaveSingleItem();
        device.Alias.ShouldBe("Reception");
        device.Group.ShouldBe(string.Empty);
        device.Note.ShouldBe(string.Empty);
    }

    [Fact]
    public void A_file_that_is_not_a_device_book_reads_as_an_empty_one()
    {
        DeviceBook.FromJson("[1, 2, 3]").Devices.ShouldBeEmpty();
    }

    [Fact]
    public void Saving_and_loading_a_real_file_comes_back_the_same()
    {
        string path = Path.Combine(Path.GetTempPath(), "sunllo-devices-" + Guid.NewGuid().ToString("N"), "devices.json");
        try
        {
            new DeviceBook().With(new SavedDevice { Target = "123456789", Alias = "Reception" }).Save(path);

            DeviceBook.Load(path).Devices.ShouldHaveSingleItem().Alias.ShouldBe("Reception");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void Loading_a_file_that_is_not_there_gives_an_empty_book()
    {
        DeviceBook.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "devices.json")).Devices.ShouldBeEmpty();
    }
}

/// <summary>The book serialises through a source-generated context; tests only need matching options.</summary>
internal static class DeviceBookJsonForTests
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault,
    };
}
