using System.Diagnostics;
using System.IO;
using AcerHelper.Localization;

namespace AcerHelper.Infrastructure;

/// <summary>Linux-only: installs the udev rules, the tmpfiles.d entry, the systemd unit that re-applies the SMU
/// mailbox grant at every boot, and (on an Acer) the modprobe.d options file bundled next to the app (AppImage
/// case — a sandbox-free binary can call pkexec directly) into /etc via a single polkit prompt, so the root-only
/// control nodes become writable by the user's group and, where it applies, the acer-wmi Predator/Nitro features
/// are actually present at module load. A native RPM install already ships the rules (under
/// /usr/lib/udev/rules.d), so the action is offered when an installed copy of some applicable file differs from
/// the bundled bytes OR when the permission those files grant is missing on the running machine (a grant can be
/// lost at boot without a single file changing — see <see cref="RulesNeeded"/> and
/// <see cref="SmuMailboxAccess"/>). No-op on Windows (no udev). Cross-platform file so the all-platforms callers
/// don't trip CA1416.
///
/// THE INSTALL IS CONSENTED TO BEFORE IT RUNS, and this file owns the CONTENT of that consent as well as the
/// install: the entries' own <c>Description</c>s are the prompt's rows (<see cref="ConsentRows"/>, rendered by
/// <c>UI/HardwareAccessConsent.cs</c>), so what the user agrees to cannot drift from what is installed. The
/// privileged mechanism itself is untouched by that — still one pkexec script, still the same order inside it.</summary>
public static class HardwareAccess
{
    /// <summary>The files bundled next to the app, each with EVERY location an installed copy can occupy (the
    /// RPM's /usr/lib/udev/rules.d is the udev rule's second one) and whether the file is ACER-ONLY.
    ///
    /// THE SPLIT IS BY SUBJECT MATTER, NOT BY BRAND, and it is the reason the flag exists. The udev rules and the
    /// tmpfiles entry grant access to vendor-NEUTRAL nodes — keyboard backlight brightness/stop_timeout,
    /// power_supply charge thresholds, firmware-attributes BIOS values — which the Dell and generic Linux
    /// backends drive too, so they are offered on whatever machine the app runs on. The modprobe.d options line
    /// is about ONE driver: on a machine that is not an Acer it would ask the user to install parameters for a
    /// module nothing ever loads, and on the machine it IS about (an Acer outside acer-wmi's DMI quirk table) it
    /// is what makes the features exist at all. Gating the whole set on Acer instead — the shape this code had
    /// before the flag — silently takes the access offer away from every Dell and generic machine that has it
    /// today, which is why the two halves are told apart here rather than at the call sites.
    ///
    /// The modprobe file's INSTALLED name differs from its bundled one deliberately: packaging/acer-helper.conf
    /// is the tmpfiles.d file, so the modprobe one is bundled as "…-modprobe.conf" and installed as
    /// acer-helper.conf into a different /etc subdirectory — two bundled files cannot share one name in the
    /// publish folder or AppDir, where flat copies of both land side by side.
    ///
    /// EACH ENTRY ALSO CARRIES THE SENTENCE THE CONSENT PROMPT SHOWS FOR IT (Description → <see cref="ConsentRows"/>),
    /// because the prompt is the user's only sight of what this install does and a second, hand-kept list of
    /// permissions is exactly the thing that drifts from the installer. The pairing is ENFORCED rather than asked
    /// for: Description is a positional member of <see cref="InstallerFile"/>, so an entry cannot be written
    /// without one (the compiler refuses), an empty or untranslated one reddens
    /// <c>HardwareAccessConsentTests</c>, and the prompt renders this table itself — one row per applicably
    /// installed entry, no more and no fewer.
    ///
    /// THE SMU GRANT IS FOURTH, AND IT IS A FILE OF ITS OWN because the grant needed a mechanism the udev rule
    /// cannot be. The first version of this grant was an EXTENSION of the udev entry's sentence — the same
    /// /sys/kernel/ryzen_smu_drv attributes, granted by a second rule in the same file — and it did not survive
    /// contact with the machine it was written for: the driver is inserted from the INITRAMFS, so the PCI bind
    /// event the rule hangs off is emitted before the real systemd-udevd exists, the coldplug trigger replays
    /// add/change events and never a bind, and the four attributes came up root-only again at the next boot with
    /// every file in /etc byte-identical. So the deterministic half is a systemd unit
    /// (packaging/acer-helper-smu-perms.service, After=systemd-modules-load.service) that re-applies the grant on
    /// every boot, and this table gains the entry the earlier design deliberately avoided — which is exactly what
    /// the shape of the table asks for: one entry per bundled FILE, each sentence describing what its own file
    /// grants. The udev entry keeps its own SMU sentence, because its rule still grants the same four attributes
    /// on a manual module reload.
    ///
    /// What the design guarantees is unchanged by the addition: the sentence the user consents to is the one
    /// describing what that file grants, it is rendered from this table rather than from a list kept beside it,
    /// and an entry still cannot be written without one. What the entry list must NOT become is two entries naming
    /// one file — that would make the bundled-name list disagree with the files on disk
    /// (HardwareAccessWiringTests) and put two rows in the prompt for one permission.</summary>
    private static readonly InstallerFile[] Files =
    [
        new("60-acer-helper.rules",
            ["/etc/udev/rules.d/60-acer-helper.rules", "/usr/lib/udev/rules.d/60-acer-helper.rules"],
            false,
            "Write access for the {0} group to the nodes the app drives: the performance/thermal profile, "
            + "the keyboard-backlight brightness and timeout, the Acer fan-speed controls, the battery charge mode "
            + "and thresholds, and the Dell BIOS attributes — plus session access to the two Acer HID interfaces "
            + "(RGB keyboard/lightbar and the power-envelope channel) and to the keyboard that carries the Nitro key. "
            + "It also covers the CPU's SMU mailbox attributes in /sys/kernel/ryzen_smu_drv (smu_args, mp1_smu_cmd, "
            + "rsmu_cmd and smn), which is how the Curve Optimizer undervolt reaches the processor without the app "
            + "ever running as root."),
        new("acer-helper-smu-perms.service",
            ["/etc/systemd/system/acer-helper-smu-perms.service"],
            false,
            "Write access for the {0} group to the CPU's SMU mailbox attributes in /sys/kernel/ryzen_smu_drv (smu_args, mp1_smu_cmd, rsmu_cmd and smn), re-applied at every boot by a systemd unit: the udev rule above can only grant them when the driver's bind event reaches a running udev, which is not what happens on a machine whose ryzen_smu module is loaded from the initramfs. This is what keeps the CPU undervolt working after a restart."),
        new("acer-helper.conf",
            ["/etc/tmpfiles.d/acer-helper.conf"],
            false,
            "The same file permissions re-applied at every boot by systemd-tmpfiles, for the nodes that exist at "
            + "boot: the legacy platform-profile alias, the battery thresholds, the keyboard backlight and the "
            + "Dell BIOS attributes."),
        new("acer-helper-modprobe.conf",
            ["/etc/modprobe.d/acer-helper.conf"],
            true,
            "An options line for the acer-wmi kernel driver: it is loaded with predator_v4=1 and force_caps=7200, "
            + "which is what enables the five Acer performance profiles, the fan telemetry and the fan-speed "
            + "control (PWM) on a model the driver's own device table does not know. This changes how the driver "
            + "behaves at load rather than granting a file permission, and if the module is in use it takes effect "
            + "only after you restart the computer."),
    ];

    /// <summary>One bundled file, everything an install of it needs to know. A type rather than a tuple for one
    /// reason: <see cref="Description"/> is what keeps the consent prompt honest, and a positional member is a
    /// thing a fourth entry cannot be written without.</summary>
    /// <param name="Bundled">The file's name in the publish output / AppImage, read from <c>AppContext.BaseDirectory</c>.</param>
    /// <param name="Installed">Every location an installed copy can occupy, the first being where this install writes.</param>
    /// <param name="AcerOnly">Whether the file applies to Acer machines alone — see the table's remarks.</param>
    /// <param name="Description">What installing this file grants, in the words the user consents to. An English
    /// localisation key (gettext-style, see <see cref="Loc"/>) and a format string with one placeholder, the group
    /// the permissions are handed to — not every entry uses it (a driver parameter is granted to nobody).</param>
    private sealed record InstallerFile(string Bundled, string[] Installed, bool AcerOnly, string Description);

    /// <summary>The group the installed files hand the controls to, as a constant because the consent prompt has to
    /// NAME it ("write access for the &lt;group&gt; group") and the packaging files spell it out in a dozen places.
    /// The two copies cannot drift in silence: <c>HardwareAccessConsentTests</c> reads the group out of
    /// packaging/60-acer-helper.rules and packaging/acer-helper.conf and compares it with this constant, so
    /// editing "wheel" in one place reddens the suite instead of leaving the prompt promising access to a group
    /// the rules no longer mention. (The packaging files say the group may be edited; that edit is this line.)</summary>
    internal const string AccessGroup = "wheel";

    /// <summary>The DMI vendor file, the same source <c>MachineInfo</c> identifies the machine from — read here
    /// directly rather than through it so this installer stays independent of the vendor tree (it is a
    /// cross-platform file, and the vendor types are per-OS).</summary>
    private const string DmiVendorPath = "/sys/devices/virtual/dmi/id/sys_vendor";

    /// <summary>The bundled files that apply to a machine, given whether it is an Acer: the split above, as one
    /// pure function. Both entry points read it, so the offer and the install cannot disagree about which files
    /// this machine gets, and the rule can be pinned by a test without a DMI to fake or an /etc to prepare.</summary>
    private static IEnumerable<InstallerFile> Applicable(bool isAcer)
        => Files.Where(f => !f.AcerOnly || isAcer);

    /// <summary>The same decision, projected to names for the test suite (see <see cref="Applicable"/>).</summary>
    internal static IEnumerable<string> ApplicableBundledNames(bool isAcer)
        => Applicable(isAcer).Select(f => f.Bundled);

    /// <summary>The consent prompt's rows, one per entry this machine's install will place, IN THE INSTALLER'S OWN
    /// ORDER and already localized — this is the prompt's entire content, so the user reads exactly the set that is
    /// about to be installed and not a list kept beside it. The <paramref name="isAcer"/> parameter is the machine
    /// fact the install itself decides on (see <see cref="Applicable"/>): a non-Acer gets the two permission files
    /// and no word about module parameters, which is precisely what it will get installed.
    ///
    /// Rows are built from the TABLE, never from a list of their own — that is what makes a fourth entry appear
    /// here (with its own sentence, which the compiler demanded) instead of being installed silently.</summary>
    public static IReadOnlyList<string> ConsentRows(bool isAcer)
        => Applicable(isAcer).Select(f => Loc.T(f.Description, AccessGroup)).ToArray();

    /// <summary>The same rows for THIS machine, from the one authority the install uses for the question
    /// (<see cref="IsAcer"/>): reading DMI twice cannot disagree inside one install, and the prompt is shown
    /// immediately before it. See the remarks on <see cref="Applicable"/> for why the DMI read lives here and not
    /// at the prompt's own call site.</summary>
    public static IReadOnlyList<string> ConsentRows() => ConsentRows(IsAcer());

    /// <summary>The same rows UNRENDERED — the bundled name each row belongs to and its English description key —
    /// for the test suite, whose two claims need the pair rather than the words: "every entry carries a
    /// description", and "the prompt's rows are exactly the entries this machine installs, no more and no fewer".
    /// The second needs a source the rendering does NOT share, or it would only be comparing a function with
    /// itself (see <c>HardwareAccessConsentTests</c>, which counts against <see cref="ApplicableBundledNames"/>
    /// as well).</summary>
    internal static IReadOnlyList<(string Bundled, string Description)> DescribedEntries(bool isAcer)
        => Applicable(isAcer).Select(f => (f.Bundled, f.Description)).ToArray();

    /// <summary>True when we should offer "Grant hardware access": Linux, the applicable bundled files present
    /// (i.e. running from the AppImage/publish, not a rules-less build), and EITHER at least one of them with no
    /// installed copy byte-identical to it — covering both "never installed" and "installed by an older version"
    /// (the files evolve with the app; the pkexec install is idempotent) — OR the permission those files grant
    /// missing on this machine right now (see the second half below).
    ///
    /// "Applicable" is where the per-file split lands, and it is the whole of the Acer question in this type:
    /// the vendor-neutral rules, tmpfiles entry and SMU unit are weighed on every machine (a Dell or generic
    /// laptop must keep seeing the offer exactly as it did before the options file existed), while the modprobe.d
    /// entry is weighed only on an Acer. The comparison walks the applicable entries, so a bundled file starts
    /// being compared — and, if it is marked Acer-only, starts being gated — the moment it is listed above,
    /// instead of being silently ignored here.
    ///
    /// THE SECOND HALF EXISTS BECAUSE A GRANT CAN BE LOST WITHOUT A FILE CHANGING, which is not hypothetical: the
    /// four /sys/kernel/ryzen_smu_drv attributes are created root-only by the kernel at every boot, and on this
    /// machine the udev rule's bind event never reached a running udev (the module comes from the initramfs), so
    /// the grant was gone while every installed file stayed byte-identical to the bundled one. Byte comparison
    /// alone therefore offered nothing, and the feature the user had already consented to disappeared with no way
    /// back except by hand. <see cref="SmuMailboxAccessHost.GrantMissing"/> answers that question from the
    /// filesystem, and it is asked LAST, only after every applicable file has been found present and current —
    /// so a machine that still needs a file installed is offered the install for that reason alone, and a machine
    /// whose files are current is offered it only when the grant is really missing.
    ///
    /// NO PROMPT STORM: a machine where the grant is in place answers false here, so the banner stays away, and a
    /// machine that has none of these nodes AT ALL (a Dell, or an Acer without ryzen_smu) also answers false —
    /// absence is not a lost grant, and offering an install for a driver that is not there is the one way this
    /// check could be worse than the byte comparison it extends.</summary>
    public static bool RulesNeeded()
    {
        if (!OperatingSystem.IsLinux()) return false;
        foreach (var file in Applicable(IsAcer()))
        {
            var source = Bundled(file.Bundled);
            if (source == null) return false;   // no bundled file to install -> nothing to offer
            if (!file.Installed.Any(loc => SameContent(loc, source))) return true;
        }

        // Every applicable file is present, current and byte-identical — so the only thing that can still be
        // missing is the PERMISSION, which no comparison of file bytes can see.
        return SmuMailboxAccessHost.GrantMissing();
    }

    /// <summary>Whether this machine is an Acer, from DMI. "Absent or unreadable" counts as NOT an Acer: this
    /// file is the only authority here, so an unknown machine must not read as yes (and a machine whose DMI the
    /// kernel cannot expose is not one to write module parameters for). It gates the Acer-only entries alone —
    /// see <see cref="Files"/> for why the rest of the bundle must not be gated with them.</summary>
    private static bool IsAcer()
    {
        try
        {
            return File.Exists(DmiVendorPath) &&
                   File.ReadAllText(DmiVendorPath).Trim()
                       .StartsWith("Acer", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool SameContent(string installed, string bundled)
    {
        try
        {
            return File.Exists(installed) &&
                   File.ReadAllBytes(installed).AsSpan().SequenceEqual(File.ReadAllBytes(bundled));
        }
        catch { return false; }
    }

    /// <summary>The systemd unit that owns the SMU mailbox grant — one mechanism for BOTH cases, which is what
    /// replaced the inline chmod this step used to be.
    ///
    /// WHY THE INLINE CHMOD IS GONE. It granted the four /sys/kernel/ryzen_smu_drv attributes to the group right
    /// there in the script, for the reason that is still true: the driver's sysfs tree is a BARE kobject (not a
    /// device, no uevent), so udev's MODE/GROUP can never reach those files, and packaging/60-acer-helper.rules
    /// therefore hangs its grant off <c>ACTION=="bind"</c> — an event that covers every future bind and NOTHING
    /// about the present one, while the user grants access with the driver already bound. What the inline chmod
    /// could not do is the OTHER half, and that half was measured: on this machine ryzen_smu is inserted from the
    /// initramfs, so the bind uevent is emitted before the real systemd-udevd exists, the coldplug trigger replays
    /// add/change events and never a bind, and the four attributes came back root-only at the next boot with every
    /// file in /etc byte-identical. A chmod in an install script cannot fix a permission that is lost at boot.
    ///
    /// SO THE GRANT MOVED INTO A UNIT (packaging/acer-helper-smu-perms.service), and this step does the two
    /// things that make one mechanism cover both cases: <c>daemon-reload</c> makes the file that was just
    /// installed into a known unit, and then <c>enable</c> writes the boot symlink while <c>restart</c> runs it
    /// here and now (the immediate case the inline chmod used to cover).
    ///
    /// RESTART, NOT <c>enable --now</c>, and the difference is a real second install in the same boot: the unit
    /// is <c>Type=oneshot</c> with <c>RemainAfterExit=yes</c>, so once it has run it counts as ACTIVE, and
    /// <c>--now</c> starts nothing on an active unit. A grant that was lost again in that boot — a driver reload
    /// recreating the attributes root-only is exactly how this happened once — would then be re-offered by the app
    /// and NOT re-applied by the install, which is a prompt that fixes nothing. <c>restart</c> re-runs the unit
    /// whether or not it is active, so one install always leaves the grant in place.
    ///
    /// The unit carries the parts this script must not have to re-decide:
    /// <c>After=systemd-modules-load.service</c> for the ordering, and
    /// <c>ConditionPathExists=/sys/kernel/ryzen_smu_drv</c> so that a machine without the driver
    /// skips it instead of failing — which is why the four node names are no longer spelled here at all. They live
    /// in <see cref="SmuMailboxAccess.Nodes"/> and in the two packaging files, and the suite requires all three to
    /// agree.
    ///
    /// It sits after the tmpfiles pass and before the module reload, which stays LAST — it is what creates the
    /// pwm nodes the hwmon rule matches. An earlier position would be wrong for the same reason the reload is
    /// last, and a later one would put a unit start between the reload and the honest "did it apply" reading.
    ///
    /// THE DOUBLE PARENTHESES KEEP THE CHAIN'S SHAPE, which is the hazard the module reload's parentheses address
    /// too: the script is one <c>&amp;&amp;</c> chain, so a bare <c>|| true</c> re-associates everything after it —
    /// steps that follow a failure would no longer be skipped. A unit that always succeeds leaves the order and
    /// the short-circuiting exactly as they were, and this step is allowed to fail for the reason the inline chmod
    /// was: it is an ADDITIONAL grant, not a link in the chain. The one machine it can fail on is one where
    /// <c>systemctl</c> cannot talk to a systemd (an AppImage run under another init), and an install that placed
    /// every file correctly must not be reported as failed because this extra step could not run. Note what
    /// follows, and it is why the failure is tolerable rather than hidden: the grant being absent is now a
    /// QUESTION the app can ask (<see cref="SmuMailboxAccess"/>, and the reason this step no longer needs to
    /// report it) — the banner comes back on the next refresh instead of the loss being silent.</summary>
    private const string SmuPermissionsUnit = "acer-helper-smu-perms.service";

    /// <summary>The step itself — see <see cref="SmuPermissionsUnit"/> for the ordering, the parentheses and the
    /// tolerance it is built from.</summary>
    private static readonly string SmuPermissionsStep =
        "((systemctl daemon-reload && systemctl enable " + SmuPermissionsUnit
        + " && systemctl restart " + SmuPermissionsUnit + ") || true)";

    /// <summary>Where the kernel reports the parameters a LOADED module was given, one file per parameter
    /// (<c>predator_v4</c>, <c>force_caps</c>, …). World-readable, and the only place that says what the module
    /// is actually running with — see <see cref="ParametersAreLive"/>.</summary>
    private const string ModuleParametersDir = "/sys/module/acer_wmi/parameters";

    /// <summary>What one install attempt achieved. TWO success shapes, not one, because they need different
    /// words and the wrong one is a false promise: the caller must not tell the user to restart the APP when
    /// what is missing is a reboot (restarting an app cannot load a module), and must not talk about a reboot
    /// when the settings are already live.</summary>
    public enum AccessInstall
    {
        /// <summary>Nothing usable was installed: pkexec was dismissed, or a step of the chain before the module
        /// reload failed. The caller reports the failure with the error text.</summary>
        Failed,

        /// <summary>The files are in /etc and, where module parameters were part of this install, they are LIVE
        /// in the loaded module. The unlocked controls exist now; only the app has to restart, because it caches
        /// its sysfs paths at construction.</summary>
        Applied,

        /// <summary>The files are in /etc but the parameters are NOT live: acer_wmi was in use (rfkill/wifi hold
        /// a reference) or not loaded, so the reload could not take. The settings apply at the next REBOOT, and
        /// the app cannot show them before that however often it is restarted.</summary>
        PendingReboot,
    }

    /// <summary>Install the applicable bundled files into /etc via one pkexec prompt and apply them live. Runs
    /// the blocking pkexec call — call it off the UI thread. Reports WHICH of the two success shapes happened
    /// (see <see cref="AccessInstall"/>): the files being in place is not the same as the settings being in
    /// effect, and only the second one is what the user is waiting for.
    ///
    /// THE ORDER INSIDE THE ONE SCRIPT IS LOAD-BEARING and is the reason it is one script rather than several
    /// prompts: the files land first; udev is reloaded so the new hwmon rule is in the ruleset; the trigger
    /// replays the existing devices through it; systemd-tmpfiles applies the tmpfiles entry; the SMU permissions
    /// unit is loaded into systemd and started, which grants the mailbox attributes now and enables the grant for
    /// every boot; and LAST the module is reloaded so the new parameters take effect in this session and the
    /// just-installed rule sees the pwm nodes that reload creates. Reloading the module before the rule was
    /// loaded would leave every node the reload produced unwritable until the next boot.
    ///
    /// The reload is `|| true` — and parenthesised so that `|| true` binds to the RELOAD ALONE — because a module
    /// that is in use (rfkill/wifi hold a reference) cannot be unloaded: that degrades to "applies after a
    /// reboot" instead of failing an install that has already succeeded at everything else. Without the
    /// parentheses, a failure at any earlier step would be swallowed by the same `|| true` and the caller would
    /// report success for a half-installed set. Note what follows from that: the reload's own exit status is
    /// unavailable by design, so "did it apply" is answered by the kernel instead — see
    /// <see cref="ParametersAreLive"/>, checked before this method returns.
    ///
    /// The reload is ALSO gated on whether the ACER-ONLY half of the install is in this script (<c>AcerOnly</c>
    /// in the table above — today that is the acer_wmi options line and nothing else), and that is one decision
    /// rather than two: the reload exists for exactly one reason (making the freshly installed parameters live),
    /// so where they were not installed there is nothing to reload — on a non-Acer, `modprobe -r acer_wmi` would
    /// only report "Module acer_wmi not found" out of a root prompt (harmless, since `|| true` covers it, but
    /// noise about a driver that is not this machine's). Gating it here also means the non-Acer script ends at
    /// the tmpfiles pass, whose exit status is then the script's own — an earlier failure still propagates.
    ///
    /// A restart of the APP is still needed after this: it caches its sysfs paths at construction, so nodes that
    /// appear on the reload are invisible to the instance that is already running (the banner text the caller
    /// shows after a successful install already tells the user to restart).</summary>
    public static AccessInstall Install(out string? error)
    {
        error = null;
        var applicable = Applicable(IsAcer()).ToArray();
        string? stagedOptionsFile = null;

        // Copy the bundled files to a plain temp dir first: root (via pkexec) can't traverse into the user's
        // AppImage FUSE mount, but can read /tmp. Then pkexec installs from there.
        DirectoryInfo? stage = null;
        try
        {
            stage = Directory.CreateTempSubdirectory("acer-helper-rules");
            var installs = new List<string>();
            foreach (var file in applicable)
            {
                var source = Bundled(file.Bundled);
                if (source == null) { error = "bundled rules not found"; return AccessInstall.Failed; }
                var staged = Path.Combine(stage.FullName, file.Bundled);
                File.Copy(source, staged, overwrite: true);
                installs.Add($"install -m0644 '{staged}' '{file.Installed[0]}'");
                if (file.AcerOnly) stagedOptionsFile = staged;   // the file whose parameters have to be made live
            }

            var script = string.Join(" && ", installs) +
                " && udevadm control --reload-rules" +
                " && udevadm trigger --subsystem-match=power_supply --subsystem-match=leds --subsystem-match=platform-profile " +
                "--subsystem-match=platform --subsystem-match=input --subsystem-match=hidraw --subsystem-match=hwmon" +
                " && systemd-tmpfiles --create /etc/tmpfiles.d/acer-helper.conf" +
                " && " + SmuPermissionsStep;   // the SMU grant: applied now AND enabled for every boot
            if (applicable.Any(f => f.AcerOnly))
                script += " && (/usr/sbin/modprobe -r acer_wmi && /usr/sbin/modprobe acer_wmi) || true";

            var psi = new ProcessStartInfo("pkexec") { UseShellExecute = false };
            psi.ArgumentList.Add("/bin/sh");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(script);

            using var p = Process.Start(psi);
            if (p == null) { error = "could not start pkexec"; return AccessInstall.Failed; }
            p.WaitForExit(120_000);
            if (p.ExitCode != 0)
            {
                error = $"pkexec exited with code {p.ExitCode}";   // 126/127 = auth dismissed / not authorized
                return AccessInstall.Failed;
            }

            // Exit status 0 means the files are in /etc (they are the first links of that chain). It does NOT
            // mean the SETTINGS are in effect: the module reload at the end is `|| true` precisely so an in-use
            // module cannot fail the install, so its outcome has to be read from the kernel. And this is the
            // distinction the user needs told apart, because the promise differs: the app's own restart is
            // enough for one, and cannot help at all with the other.
            if (stagedOptionsFile == null) return AccessInstall.Applied;   // no parameters were installed
            return ParametersAreLive(stagedOptionsFile) ? AccessInstall.Applied : AccessInstall.PendingReboot;
        }
        catch (Exception ex) { error = ex.Message; return AccessInstall.Failed; }
        finally { try { stage?.Delete(recursive: true); } catch { /* ignore */ } }
    }

    /// <summary>True when every parameter the options file asks for is IN EFFECT in the loaded module. This is
    /// the honest test of "did it apply", and it is deliberately not the reload's exit code: that command is
    /// wrapped in `|| true` (see the remarks on <see cref="Install"/>) so its status says nothing, while the
    /// kernel's parameter files say exactly what the module is running with. A module that is not loaded at all,
    /// or a parameter name it does not have, also reads as not live — which is correct: the settings then apply
    /// at the next boot, and that is what the caller has to tell the user.</summary>
    private static bool ParametersAreLive(string optionsFile)
    {
        try
        {
            var wanted = OptionsLineParameters(optionsFile);
            if (wanted.Count == 0) return false;
            foreach (var (name, value) in wanted)
            {
                var path = Path.Combine(ModuleParametersDir, name);
                if (!File.Exists(path)) return false;
                if (!SameValue(File.ReadAllText(path).Trim(), value)) return false;
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>The name=value pairs of the options file's <c>options</c> line, read from the staged copy that
    /// was just installed. Only real options lines are read — a COMMENT that mentions a parameter must not be
    /// mistaken for one, which is not hypothetical here: this file's prose names every parameter it sets. A
    /// token without "=" is skipped (the module name itself, or a valueless flag).
    ///
    /// WHITESPACE IS WHATEVER KMOD ACCEPTS, not one space. modprobe.d splits a directive on any run of spaces
    /// and/or tabs, so "options\ttacer_wmi\tpredator_v4=1" is the same directive as the space-separated one — and
    /// a parser that insisted on single spaces would report a LIVE module as not live (zero pairs, or a
    /// parameter named "acer_wmi\tpredator_v4") and send the user to reboot for nothing. The keyword is matched
    /// as a TOKEN for the same reason: a prefix test on "options " is the same single-space assumption wearing a
    /// different hat.</summary>
    internal static IReadOnlyList<(string Name, string Value)> OptionsLineParameters(string optionsFile)
    {
        var pairs = new List<(string, string)>();
        foreach (var line in File.ReadAllLines(optionsFile))
        {
            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2 || tokens[0] != "options") continue;
            foreach (var token in tokens.Skip(1))
            {
                var eq = token.IndexOf('=');
                if (eq <= 0) continue;
                pairs.Add((token[..eq], token[(eq + 1)..]));
            }
        }
        return pairs;
    }

    /// <summary>Whether the kernel's rendering of a parameter equals what the options file asks for. The two
    /// spellings differ by design: modprobe takes 1/0 for a bool parameter while the kernel's parameter file
    /// reports it as Y/N (and <c>force_caps</c> has a numeric default of -1), so booleans are compared by
    /// meaning and numbers by value; anything else is compared as text.</summary>
    internal static bool SameValue(string live, string wanted)
    {
        if (string.Equals(live, wanted, StringComparison.OrdinalIgnoreCase)) return true;
        if (IsTrue(live) && IsTrue(wanted)) return true;
        if (IsFalse(live) && IsFalse(wanted)) return true;
        return long.TryParse(live, out var l) && long.TryParse(wanted, out var w) && l == w;
    }

    private static bool IsTrue(string v)
        => v.Equals("Y", StringComparison.OrdinalIgnoreCase)
           || v.Equals("yes", StringComparison.OrdinalIgnoreCase)
           || v.Equals("on", StringComparison.OrdinalIgnoreCase)
           || v.Equals("true", StringComparison.OrdinalIgnoreCase)
           || v == "1";

    private static bool IsFalse(string v)
        => v.Equals("N", StringComparison.OrdinalIgnoreCase)
           || v.Equals("no", StringComparison.OrdinalIgnoreCase)
           || v.Equals("off", StringComparison.OrdinalIgnoreCase)
           || v.Equals("false", StringComparison.OrdinalIgnoreCase)
           || v == "0";

    private static string? Bundled(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, name);
        return File.Exists(path) ? path : null;
    }
}
