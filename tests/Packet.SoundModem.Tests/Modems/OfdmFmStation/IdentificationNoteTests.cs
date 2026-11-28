using AwesomeAssertions;
using Packet.SoundModem.Modems.OfdmFmStation;
using Packet.SoundModem.Tests.Channel;

namespace Packet.SoundModem.Tests.Modems.OfdmFmStation;

public class IdentificationNoteTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pdn-note-{Guid.NewGuid():N}");
    private readonly VirtualAir.Clock _clock = new();
    private readonly List<string> _said = [];

    public IdentificationNoteTests() => _clock.Elapse(TimeSpan.FromDays(10));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private IdentificationNote Note(string name = "note.txt") => new(Path.Combine(_dir, name), _clock, _said.Add);

    [Fact]
    public void A_time_written_is_read_back_and_the_file_is_one_iso_line_in_utc()
    {
        IdentificationNote note = Note();
        DateTimeOffset when = _clock.GetUtcNow() - TimeSpan.FromMinutes(2);

        note.Write(when);

        note.Read().Should().Be(when);
        File.ReadAllText(Path.Combine(_dir, "note.txt")).Should().MatchRegex(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z\n$");
        Directory.GetFiles(_dir).Should().ContainSingle("the temporary file was renamed away");
        _said.Should().BeEmpty();
    }

    [Fact]
    public void A_second_write_replaces_the_first()
    {
        IdentificationNote note = Note();
        note.Write(_clock.GetUtcNow() - TimeSpan.FromMinutes(5));
        DateTimeOffset later = _clock.GetUtcNow() - TimeSpan.FromMinutes(1);

        note.Write(later);

        note.Read().Should().Be(later);
    }

    [Fact]
    public void A_missing_file_is_no_record()
    {
        Note().Read().Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("not a time\n")]
    [InlineData("2026-11-28\n")]
    [InlineData("2026-11-28T09:15:02.250Z\nsecond line\n")]
    [InlineData("\0\0\0\0")]
    public void An_empty_or_garbled_file_is_no_record(string content)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "note.txt"), content);

        Note().Read().Should().BeNull();
    }

    [Fact]
    public void A_time_later_than_the_clock_is_no_record()
    {
        IdentificationNote note = Note();
        note.Write(_clock.GetUtcNow() + TimeSpan.FromMinutes(1));

        note.Read().Should().BeNull();
    }

    [Fact]
    public void A_file_that_cannot_be_read_is_no_record_and_does_not_throw()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "note.txt"));

        Note().Read().Should().BeNull();
    }

    [Fact]
    public void A_failed_write_is_said_once_in_one_line_and_does_not_throw_and_is_said_again_only_after_one_has_worked()
    {
        // The parent of the note's directory is a file, so the directory can never be made.
        Directory.CreateDirectory(_dir);
        string blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "x");
        var note = new IdentificationNote(Path.Combine(blocker, "sub", "note.txt"), _clock, _said.Add);

        note.Write(_clock.GetUtcNow());
        note.Write(_clock.GetUtcNow());

        _said.Should().ContainSingle();
        _said[0].Should().StartWith("cannot write the identification time to ").And.NotContain("\n");
        _said[0].Should().MatchRegex("^[\\x20-\\x7e]+$");
        note.Read().Should().BeNull();

        // After a write that works, the next failure is said again.
        var good = new IdentificationNote(Path.Combine(_dir, "good.txt"), _clock, _said.Add);
        good.Write(_clock.GetUtcNow());
        File.Delete(blocker);
        Directory.CreateDirectory(blocker);
        File.WriteAllText(Path.Combine(blocker, "sub"), "x");
        var again = new IdentificationNote(Path.Combine(blocker, "sub", "note.txt"), _clock, _said.Add);
        again.Write(_clock.GetUtcNow());
        _said.Should().HaveCount(2, "a different note failing is a different report");
    }

    [Fact]
    public void Two_callsign_and_device_pairs_do_not_share_a_file_even_when_they_look_alike_once_made_safe()
    {
        string[] names =
        [
            IdentificationNote.FileName("G4ABC-1", "plughw:CARD=Device,DEV=0"),
            IdentificationNote.FileName("G4ABC-1", "plughw:CARD=Device,DEV=1"),
            IdentificationNote.FileName("G4ABC-2", "plughw:CARD=Device,DEV=0"),
            IdentificationNote.FileName("G4ABC-1", "plughw_CARD_Device_DEV_0"),
            IdentificationNote.FileName("G4ABC-1", "pipe:a,b"),
            IdentificationNote.FileName("G4ABC-1", "pipe:a;b"),
        ];

        names.Distinct().Should().HaveCount(names.Length);
    }

    [Fact]
    public void A_file_name_is_safe_on_any_file_system_and_is_the_same_each_time()
    {
        string name = IdentificationNote.FileName("VK2/M0LTE/QRP-15", "/dev/snd/../../etc:passwd *?\"<>|\u00e9");

        name.Should().MatchRegex("^[A-Za-z0-9_.-]+$").And.EndWith(".txt");
        name.Length.Should().BeLessThan(100);
        IdentificationNote.FileName("VK2/M0LTE/QRP-15", "/dev/snd/../../etc:passwd *?\"<>|\u00e9").Should().Be(name);
    }
}
