using AcerHelper.Features;

namespace AcerHelper.Vendors.Acer;

/// <summary>One lighting mode of the ENE controller (verified on Acer Nitro 18).</summary>
public sealed class RgbEffect(string name, byte modeByte, bool isEffect, bool hasColor, bool hasSpeed)
{
    public string Name     { get; } = name;
    public byte   ModeByte { get; } = modeByte;
    public bool   IsEffect { get; } = isEffect; // true => effect flag (0x02) + speed; false => static (0x01)
    public bool   HasColor { get; } = hasColor; // honours the chosen colour
    public bool   HasSpeed { get; } = hasSpeed;

    /// <summary>Project to the vendor-neutral descriptor the UI binds to (this effect is the opaque handle).</summary>
    public RgbModeInfo ToModeInfo() => new(Name, HasColor, HasSpeed, this);

    public override string ToString() => Name;
}

public static class RgbEffects
{
    /// <summary>Mode byte for the plain "Static" effect (used for per-zone colours).</summary>
    public static byte StaticModeByte => STATIC;

    // mode bytes
    private const byte STATIC    = 0x02;
    private const byte BREATHING = 0x04;
    private const byte NEON      = 0x05;
    private const byte WAVE      = 0x07;
    private const byte SHIFTING  = 0x08;
    private const byte ZOOM      = 0x09;
    private const byte METEOR    = 0x0A;
    private const byte TWINKLING = 0x0B;

    // Keyboard: Static + Breathing honour colour; the rest cycle their own colours.
    public static readonly RgbEffect[] Keyboard =
    [
        new("Static",    STATIC,    isEffect: false, hasColor: true,  hasSpeed: false),
        new("Breathing", BREATHING, isEffect: true,  hasColor: true,  hasSpeed: true),
        new("Neon",      NEON,      isEffect: true,  hasColor: false, hasSpeed: true),
        new("Wave",      WAVE,      isEffect: true,  hasColor: false, hasSpeed: true),
        new("Shifting",  SHIFTING,  isEffect: true,  hasColor: false, hasSpeed: true),
        new("Zoom",      ZOOM,      isEffect: true,  hasColor: false, hasSpeed: true),
        new("Meteor",    METEOR,    isEffect: true,  hasColor: false, hasSpeed: true),
        new("Twinkling", TWINKLING, isEffect: true,  hasColor: false, hasSpeed: true)
    ];

    // Lightbar (single zone): Static + Breathing honour colour, Neon cycles. Spatial effects do nothing.
    public static readonly RgbEffect[] Lightbar =
    [
        new("Static",    STATIC,    isEffect: false, hasColor: true,  hasSpeed: false),
        new("Breathing", BREATHING, isEffect: true,  hasColor: true,  hasSpeed: true),
        new("Neon",      NEON,      isEffect: true,  hasColor: false, hasSpeed: true)
    ];
}
