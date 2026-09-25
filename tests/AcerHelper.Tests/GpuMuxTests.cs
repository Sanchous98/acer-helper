using System.Reflection;
using System.Runtime.CompilerServices;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Composition;
using AcerHelper.Infrastructure.Vendors.Acer;
using AcerHelper.Localization;
using AcerHelper.Tests.Fakes;

namespace AcerHelper.Tests;

/// <summary>
/// The shared GPU-mode (MUX) abstraction: the Acer gaming-WMI
/// adapter (read at selector 2, capability at selector 9, queued write at <c>(mode &lt;&lt; 8) | 2</c>), and the
/// service/device flow. The safety properties that matter are pinned here:
///   * a request QUEUES the value and never applies it immediately;
///   * validation refuses over guessing (the armoury port's live-limit rule, and the Acer mode enum);
///   * the Acer port reads the real mode and refuses an unknown/unreadable one instead of guessing;
///   * a request is single-flight and a pending value never outlives the reboot it describes;
///   * the domain messages are translated.
/// </summary>
public class GpuMuxTests
{
    // ---- Acer adapter ----
    //
    // THE WIRE, driven without WMI or an Acer machine: the gaming channel is two delegates — selector in /
    // raw gmOutput out, and packed gmInput in / (ok, error) out — exactly the shape the Windows backend builds
    // from its `GmGet`/`GmSet` helpers. The class under test holds no transport.

    /// <summary>The Acer gaming-WMI channel as the MUX sees it: a firmware mode, a capability signal, and the
    /// raw call log. A failed call answers the all-ones sentinel — the same thing <c>GmGet</c> collapses a WMI
    /// failure to — so the failure paths are exercised rather than mocked away.</summary>
    private sealed class FakeGaming
    {
        public byte Mode { get; set; } = 1;            // the firmware's current mode
        public byte Capability { get; set; } = 7;      // selector 9's presence/bitmask signal (observed 7)
        public byte ReadStatus { get; set; }           // selector 2's status byte
        public byte CapStatus { get; set; }            // selector 9's status byte
        public bool WriteOk { get; set; } = true;
        public List<ulong> Reads { get; } = [];
        public List<ulong> Writes { get; } = [];

        public ulong Read(ulong selector)
        {
            Reads.Add(selector);
            return selector switch
            {
                AcerGpuMuxProtocol.CapabilitySelector => Pack(CapStatus, Capability),
                AcerGpuMuxProtocol.ReadModeSelector   => Pack(ReadStatus, Mode),
                _ => ulong.MaxValue,   // the all-ones sentinel: a failed/unknown read must never be guessed
            };
        }

        public (bool ok, string? error) Write(ulong packed)
        {
            Writes.Add(packed);
            if (!WriteOk) return (false, "SetGamingMiscSetting status=1");
            Mode = (byte)((packed >> 8) & 0xFF);   // the firmware records the request at once
            return (true, null);
        }

        private static ulong Pack(byte status, byte value) => (ulong)status | ((ulong)value << 8);
    }

    private static AcerGpuMux AcerMux(FakeGaming fake) => AcerGpuMux.Create(fake.Read, fake.Write);

    /// <summary>The read decode: the low byte is the status and the next byte the mode. The status-is-not-zero
    /// and mode-255 rows are the ones that must never be mistaken for a real mode.</summary>
    [Theory]
    [InlineData(0x0000UL, 0x00, 0x00)]
    [InlineData(0x0200UL, 0x00, 0x02)]   // status 0, mode 2
    [InlineData(0x0100UL, 0x00, 0x01)]   // status 0, mode 1
    [InlineData(0x0201UL, 0x01, 0x02)]   // status 1 (error), mode 2
    [InlineData(0xFF00UL, 0x00, 0xFF)]   // mode 255 = not supported
    public void TheAcerDecodeSplitsStatusAndMode(ulong output, byte status, byte mode)
    {
        var (s, v) = AcerGpuMuxProtocol.Decode(output);
        Assert.Equal(status, s);
        Assert.Equal(mode, v);
    }

    /// <summary>The write packing, one row per wire mode: the mode sits above the selector 2 in the low byte.</summary>
    [Theory]
    [InlineData(1, 0x0102UL)]
    [InlineData(2, 0x0202UL)]
    [InlineData(3, 0x0302UL)]
    public void TheAcerWritePacksTheModeAboveSelectorTwo(byte mode, ulong packed)
        => Assert.Equal(packed, AcerGpuMuxProtocol.PackSet(mode));

    /// <summary>Capability gating: a clean status and a value that is neither 0 nor 255 means present. A zero
    /// value, the 255 sentinel and a non-zero status are all "no MUX" — refuse, never parse the signal as an
    /// enum.</summary>
    [Theory]
    [InlineData(0, 7, true)]
    [InlineData(0, 1, true)]
    [InlineData(0, 0, false)]         // zero = not present
    [InlineData(0, 255, false)]       // the not-supported sentinel
    [InlineData(1, 7, false)]         // a non-zero status is an error, not a capability
    [InlineData(0xFF, 0xFF, false)]   // a failed WMI call, all-ones
    public void TheAcerCapabilityNeedsACleanPresenceValue(byte status, byte value, bool present)
        => Assert.Equal(present, AcerGpuMuxProtocol.CapabilityPresent((ulong)status | ((ulong)value << 8)));

    [Fact]
    public void TheAcerMuxReadsTheCurrentModeAtSelectorTwo()
    {
        var fake = new FakeGaming { Mode = 2 };
        var mux = AcerMux(fake);

        Assert.True(mux.Supported);
        Assert.Equal(["1", "2", "3"], mux.Modes.Select(m => m.Id));   // Hybrid, Discrete, Auto/DDS
        var state = mux.Read();
        Assert.Equal("2", state.Current!.Id);
        Assert.Null(state.Pending);
        Assert.False(state.RebootRequired);

        // The capability was probed first, then the read went to selector 2 (and nothing else).
        Assert.Contains(AcerGpuMuxProtocol.CapabilitySelector, fake.Reads);
        Assert.Contains(AcerGpuMuxProtocol.ReadModeSelector, fake.Reads);
    }

    /// <summary>An unreadable current mode — an error status, the 255 sentinel, or a value outside the enum —
    /// is never guessed into a real mode. The card shows "unknown" instead of a routing the firmware did not
    /// report.</summary>
    [Theory]
    [InlineData(0, 255)]   // not supported
    [InlineData(0, 0)]     // not a mode at all
    [InlineData(0, 4)]     // outside the enum
    [InlineData(1, 2)]     // error status, even with a plausible mode byte
    public void TheAcerMuxNeverGuessesAnUnknownOrFailedCurrentMode(byte status, byte value)
    {
        var fake = new FakeGaming { ReadStatus = status, Mode = value };
        Assert.Null(AcerMux(fake).Read().Current);
    }

    /// <summary>The request QUEUES: it writes the packed mode once and returns Queued. This backend has no
    /// "apply" call to make — the single WMI write IS the queue — so the row also pins that the request never
    /// comes back as an immediate change. The firmware then reads back the requested mode, so it EQUALS the
    /// current: the state normalizes it to NO pending and no restart (an equal value is never a change).</summary>
    [Fact]
    public void TheAcerRequestQueuesAndNeverClaimsTheRoutingMoved()
    {
        var fake = new FakeGaming { Mode = 1 };
        var mux = AcerMux(fake);

        var change = mux.Request("2");

        Assert.True(change.Ok);
        Assert.True(change.Queued);
        Assert.False(change.NoOp);
        Assert.Equal([0x0202UL], fake.Writes);   // (2 << 8) | 2, exactly once
        var state = mux.Read();
        Assert.Equal("2", state.Current!.Id);
        Assert.Null(state.Pending);              // the read-back equals the current -> not pending
        Assert.False(state.RebootRequired);
    }

    /// <summary>Requesting the mode the firmware already reports is a NO-OP: no WMI write, no pending, no
    /// restart. The card must never show "after restart: 2" when the mode is already 2.</summary>
    [Fact]
    public void RequestingTheCurrentAcerModeIsANoOp()
    {
        var fake = new FakeGaming { Mode = 2 };
        var mux = AcerMux(fake);

        var change = mux.Request("2");

        Assert.True(change.Ok);
        Assert.True(change.NoOp);
        Assert.False(change.Queued);
        Assert.Empty(fake.Writes);          // the firmware was not touched

        var state = mux.Read();
        Assert.Null(state.Pending);
        Assert.False(state.RebootRequired);
        Assert.Equal("2", state.Current!.Id);
    }

    /// <summary>An unreadable current mode (an error status) is UNKNOWN, so the request proceeds as a real
    /// change instead of guessing it is a no-op — the conservative answer.</summary>
    [Fact]
    public void AnUnreadableAcerCurrentModeIsNotTreatedAsANoOp()
    {
        var fake = new FakeGaming { Mode = 2, ReadStatus = 1 };
        var change = AcerMux(fake).Request("2");

        Assert.True(change.Ok);
        Assert.True(change.Queued);
        Assert.False(change.NoOp);
        Assert.Equal([0x0202UL], fake.Writes);
    }

    /// <summary>The pending claim is SESSION-SCOPED: a fresh port built over the same firmware (the process
    /// after the restart) reads the mode with no pending and no restart. The claim never outlives the reboot it
    /// describes.</summary>
    [Fact]
    public void AnAcerPendingDoesNotOutliveTheRebootItDescribes()
    {
        var fake = new FakeGaming { Mode = 1 };
        AcerMux(fake).Request("2");   // queued in this session, firmware records it

        var state = AcerMux(fake).Read();   // a new process after the restart
        Assert.Equal("2", state.Current!.Id);
        Assert.Null(state.Pending);
        Assert.False(state.RebootRequired);
    }

    /// <summary>Only the enum's wire modes are accepted; 255, 0, a foreign string or a near miss are refused
    /// before the WMI write.</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("4")]
    [InlineData("255")]
    [InlineData("2 ")]
    [InlineData("discrete")]
    [InlineData("")]
    public void TheAcerMuxRefusesAModeOutsideTheEnum(string modeId)
    {
        var fake = new FakeGaming();
        var change = AcerMux(fake).Request(modeId);

        Assert.False(change.Ok);
        Assert.Equal(GpuMuxMessages.UnknownMode, change.Error);
        Assert.Empty(fake.Writes);
    }

    /// <summary>Selector 9 saying "no MUX" makes the port unsupported: no modes, every request refused with the
    /// shared sentence, and no write reaches the firmware.</summary>
    [Fact]
    public void AnAcerWithoutTheCapabilityIsUnsupportedAndRefuses()
    {
        var fake = new FakeGaming { Capability = 0 };
        var mux = AcerMux(fake);

        Assert.False(mux.Supported);
        Assert.Empty(mux.Modes);
        Assert.Null(mux.Read().Current);

        var change = mux.Request("2");
        Assert.False(change.Ok);
        Assert.False(change.Queued);
        Assert.Equal(GpuMuxMessages.Unsupported, change.Error);
        Assert.Empty(fake.Writes);
    }

    /// <summary>The non-Windows / WMI-unavailable refusal every other OS keeps: the singleton never probes,
    /// offers no mode and refuses everything.</summary>
    [Fact]
    public void TheAcerUnsupportedSingletonRefuses()
    {
        var mux = AcerGpuMux.Unsupported;

        Assert.False(mux.Supported);
        Assert.Empty(mux.Modes);
        Assert.Null(mux.Read().Current);

        var change = mux.Request("everything");
        Assert.False(change.Ok);
        Assert.False(change.Queued);
        Assert.Equal(GpuMuxMessages.Unsupported, change.Error);
    }

    /// <summary>A write the firmware rejects reports the vendor's status and queues nothing — no pending, no
    /// restart.</summary>
    [Fact]
    public void AFailedAcerWriteIsReportedAndQueuesNothing()
    {
        var fake = new FakeGaming { Mode = 1, WriteOk = false };
        var mux = AcerMux(fake);

        var change = mux.Request("2");

        Assert.False(change.Ok);
        Assert.False(change.Queued);
        Assert.NotNull(change.Error);
        Assert.False(mux.Read().RebootRequired);
        Assert.Single(fake.Writes);
    }

    /// <summary>Single-flight: concurrent requests serialize on the port's lock, so a second WMI write can
    /// never overlap the first.</summary>
    [Fact]
    public async Task TheAcerWriteIsSingleFlight()
    {
        var fake = new FakeGaming();
        var read = fake.Read;
        var gate = new object();
        var inFlight = 0;
        var maxInFlight = 0;
        var mux = AcerGpuMux.Create(read, _ =>
        {
            lock (gate) { inFlight++; maxInFlight = Math.Max(maxInFlight, inFlight); }
            Thread.Sleep(25);
            lock (gate) { inFlight--; }
            return (true, null);
        });

        var tasks = Enumerable.Range(0, 6).Select(_ => Task.Run(() => mux.Request("2"))).ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(1, maxInFlight);
        Assert.All(tasks, t => Assert.True(t.Result.Queued));
    }

    /// <summary>THE BACKEND IS WIRED TO THE PROBED PORT — the hole the adapter rows cannot see: they drive
    /// <see cref="AcerGpuMux"/> directly, so a backend that stopped building it (leaving the refusal default
    /// on every Acer) would leave them all green. <c>AcerDevice</c> cannot be constructed without a real WMI
    /// session, so this reads the Windows partial as text — the same technique the sensor wiring test uses.</summary>
    [Fact]
    public void TheAcerWindowsBackendBuildsThePortFromTheGamingHelpers()
    {
        var windows = Source("Infrastructure/Vendors/Acer/AcerDevice.Windows.cs");
        Assert.Contains("AcerGpuMux.Create(", windows, StringComparison.Ordinal);
        Assert.Contains("\"GetGamingMiscSetting\"", windows, StringComparison.Ordinal);
        Assert.Contains("\"SetGamingMiscSetting\"", windows, StringComparison.Ordinal);

        Assert.Contains("GpuMux = AcerGpuMux.Unsupported;",
                        Source("Infrastructure/Vendors/Acer/AcerDevice.cs"), StringComparison.Ordinal);
    }

    /// <summary>THE LAYOUT GUARD (source-pinned like the other markup tests): the MUX card renders ONE stable
    /// line — the mode the firmware is in NOW, explicitly NoWrap, marked with a trailing <c>*</c> when the value
    /// differs from the original. The mark is a single character, so it can never wrap onto a second row; the
    /// explaining note sits NEXT TO THE SELECTOR (the same Grid row) and is conditional on the baseline-vs-changed
    /// flag, so it neither reserves an always-present blank nor adds a full-width caption. There is NO Apply
    /// button: the selector IS the switch. The separate no-MUX refusal is prose and may wrap. Neither a queued
    /// mode nor a non-queued outcome may appear as a second (pending) line or an outcome/message line. That is
    /// why the flyout can go back to its normal <c>SizeToContent="WidthAndHeight"</c>: no fixed frame and no
    /// reserved blank area is needed. The reserved-blank cap is absent too.</summary>
    [Fact]
    public void TheMuxCardHasOneStableLineAndTheFlyoutIsSizeToContentAgain()
    {
        var xaml = Source("UI/Views/TuningView.axaml");

        Assert.Contains("x:Name=\"GpuMuxState\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding GpuMux.CurrentText}\"", xaml, StringComparison.Ordinal);
        // The current-mode line is explicitly SINGLE-line: it can never wrap onto a second row.
        Assert.Contains("x:Name=\"GpuMuxState\" Text=\"{Binding GpuMux.CurrentText}\" TextWrapping=\"NoWrap\"",
                        xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("MinHeight=", xaml, StringComparison.Ordinal);   // and no reserved blank height
        Assert.DoesNotContain("MaxHeight=", xaml, StringComparison.Ordinal);
        // The no-MUX refusal is a distinct, wrapping TextBlock, visible only when the port is unsupported.
        Assert.Contains("x:Name=\"GpuMuxUnsupported\" Text=\"{Binding GpuMux.CurrentText}\" TextWrapping=\"Wrap\"",
                        xaml, StringComparison.Ordinal);

        // NO Apply button: the selector IS the switch, so the old button and its command are gone.
        Assert.DoesNotContain("GpuMux.ApplyCommand", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("{l:Tr Change}", xaml, StringComparison.Ordinal);

        // The note sits NEXT TO the SELECTOR: the same Grid row (Column 1) as the ComboBox (Column 0),
        // conditional on GpuMux.NoteVisible. Sharing the row means it never reserves an always-present blank and
        // never becomes a full-width bottom caption; the old permanent caption is gone.
        Assert.Contains("<Grid ColumnDefinitions=\"Auto,*\" ColumnSpacing=\"8\">", xaml, StringComparison.Ordinal);
        Assert.Contains("<ComboBox Grid.Column=\"0\"", xaml, StringComparison.Ordinal);
        var noteStart = xaml.IndexOf("x:Name=\"GpuMuxNote\"", StringComparison.Ordinal);
        Assert.True(noteStart >= 0, "the MUX selector note is gone from TuningView.axaml");
        var noteEnd = xaml.IndexOf("/>", noteStart, StringComparison.Ordinal);
        Assert.True(noteEnd > noteStart, "the MUX note element is not closed");
        var note = xaml[noteStart..noteEnd];
        Assert.Contains("Grid.Column=\"1\"", note, StringComparison.Ordinal);          // on the selector row
        Assert.Contains("Classes=\"muted\"", note, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding GpuMux.Note}\"", note, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding GpuMux.NoteVisible}\"", note, StringComparison.Ordinal);
        Assert.DoesNotContain("MinHeight", note, StringComparison.Ordinal);
        Assert.DoesNotContain("MaxHeight", note, StringComparison.Ordinal);
        Assert.DoesNotContain("GpuMuxFootnote", xaml, StringComparison.Ordinal);       // no always-present caption
        Assert.DoesNotContain("GpuMux.Footnote", xaml, StringComparison.Ordinal);

        // The pending mode and the non-queued outcomes are NOT extra lines: pending is only a '*' on CurrentText,
        // and "already current"/errors go to the transient status line (GpuMuxViewModel).
        Assert.DoesNotContain("GpuMux.PendingText", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("GpuMux.HasPending", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("GpuMux.Message", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("GpuMux.HasMessage", xaml, StringComparison.Ordinal);

        var window = Source("UI/MainWindow.axaml");

        // Back to the app's normal flyout: SizeToContent, with no hard-coded height (the fixed frame is what put
        // a gap under the Home page) and no cap-to-Home workaround either.
        var tagStart = window.IndexOf("<Window ", StringComparison.Ordinal);
        Assert.True(tagStart >= 0, "the window element is gone from MainWindow.axaml");
        var tagEnd = window.IndexOf('>', tagStart);
        var windowTag = window[tagStart..tagEnd];
        Assert.Contains("SizeToContent=\"WidthAndHeight\"", windowTag, StringComparison.Ordinal);
        Assert.DoesNotContain("Height=\"860\"", windowTag, StringComparison.Ordinal);
        Assert.DoesNotContain("MaxHeight=\"{Binding #HomePage.Bounds.Height}\"", window, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"HomePage\" VerticalAlignment=\"Top\"", window, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"HomePage\" Classes.pushed", window, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DrawerPage\" Classes.open", window, StringComparison.Ordinal);
    }

    /// <summary>The repository root, taken from the COMPILER's path rather than the working directory (the test
    /// host runs in the output folder).</summary>
    private static string Source(string relativePath, [CallerFilePath] string thisFile = "")
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"the tree no longer has '{relativePath}' — this guard is looking at nothing");
        return File.ReadAllText(path);
    }

    // ---- the device slot + service flow ----

    private sealed class FakeMux : IGpuMux
    {
        public string? LastError => null;
        public bool Supported { get; init; } = true;
        public IReadOnlyList<ChoiceOption> Modes { get; init; } =
            [new("0", GpuMuxMessages.DiscreteMode), new("1", GpuMuxMessages.HybridMode)];
        public GpuMuxState State { get; set; } = new(null, null, false);
        public GpuMuxChange Next { get; set; } = new(true, true, null);
        public List<string> Requests { get; } = [];
        public GpuMuxState Read() => State;
        public GpuMuxChange Request(string modeId) { Requests.Add(modeId); return Next; }
    }

    [Fact]
    public void TheServiceReadsAndRequestsThroughTheDeviceSlot()
    {
        var mux = new FakeMux { State = new(new("1", GpuMuxMessages.HybridMode), new("0", GpuMuxMessages.DiscreteMode), true) };
        var device = new FakeDevice { GpuMux = mux };
        var service = new LaptopService(device, new FakeSettingsStore());

        Assert.True(service.GpuMux!.Supported);
        Assert.True(service.ReadGpuMux().RebootRequired);

        var change = service.RequestGpuMux("0");
        Assert.True(change.Ok);
        Assert.True(change.Queued);
        Assert.Equal(["0"], mux.Requests);
    }

    [Fact]
    public void AnAllNullMachineHasNoMuxAndStillServes()
    {
        var service = new LaptopService(new FakeDevice(), new FakeSettingsStore());

        Assert.Null(service.GpuMux);
        Assert.Null(service.ReadGpuMux().Current);
        Assert.False(service.RequestGpuMux("0").Ok);
    }

    // ---- the shared messages are translated ----

    [Fact]
    public void EverySharedGpuMuxMessageHasARussianEntry()
    {
        var keys = typeof(GpuMuxMessages)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();

        Assert.True(keys.Length >= 5, $"only {keys.Length} keys were collected — the extraction is broken");
        foreach (var key in keys)
            Assert.True(Strings.Ru.ContainsKey(key),
                "this shared GPU-mode message has no entry in Localization/Strings.Ru.cs:\n  " + key);
    }
}
