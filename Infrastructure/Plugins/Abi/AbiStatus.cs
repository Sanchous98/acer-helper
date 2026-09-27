namespace AcerHelper.Infrastructure.Plugins.Abi;

/// <summary>
/// The status codes every ABI entry point returns as an <c>int32</c> (docs/vendor-plugins.md §3.2). They are
/// chosen so the host can branch on the NUMBER without parsing the returned JSON body, which is the property a
/// generic envelope needs: the envelope carries a status and an opaque body, and the body is only decoded on
/// the branch that promises a shape.
///
/// THESE ARE THE VALUES WRITTEN INTO THE C ABI, so they are part of the contract compiled into both the host
/// and every plugin (see <see cref="VendorAbi"/>'s note on the source-shared file). They must not be renumbered
/// within a major: a status is a wire value (§3.8.5 — any change to op numbers or payload semantics is a new
/// major).
///
/// <see cref="Refused"/> is the one with a payload convention rather than a pure classification: the machine or
/// transport refused the operation and the REASON travels in the JSON body (the <c>(ok, error)</c> shape the
/// in-host ports already return, <c>Domain/Ports.cs</c> <c>IFlagPort</c>/<c>IChoicePort</c>). That is why the
/// host needs the body at all on a non-zero status, and why "refused" is distinct from "a bug" — the former is
/// an expected hardware answer, the latter (<see cref="NotSupported"/>, <see cref="InvalidArg"/>) means the
/// host asked something it should not have and is surfaced as such.
/// </summary>
internal static class AbiStatus
{
    /// <summary>The operation succeeded; the response body is the result.</summary>
    public const int Ok = 0;

    /// <summary>The machine/transport refused the operation; the reason is in the JSON body (the current
    /// <c>(ok, error)</c> port shape). An expected hardware answer, not a host bug.</summary>
    public const int Refused = 1;

    /// <summary>This session does not implement that op/capability. The host should not have asked (a bug).</summary>
    public const int NotSupported = 2;

    /// <summary>The request was malformed (a host bug).</summary>
    public const int InvalidArg = 3;

    /// <summary>An exception was caught inside the plugin; the body carries <c>{"error":"..."}</c>. Negative so
    /// it can never be confused with a documented positive status (§3.2).</summary>
    public const int Internal = -1;

    /// <summary><c>ah_create</c> refused the handshake: the plugin needs a NEWER API than the host sent, or the
    /// host cannot adapt the declared capability/API version (§3.8.4). Negative like <see cref="Internal"/>,
    /// and deliberately its own code because it is neither a hardware refusal nor an internal fault.</summary>
    public const int AbiMismatch = -2;
}
