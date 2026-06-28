namespace AcerHelper;

/// <summary>One lighting mode of the ENE controller (verified on Acer Nitro 18).</summary>
public sealed class RgbEffect
{
    public string Name     { get; }
    public byte   ModeByte { get; }
    public bool   IsEffect { get; }   // true => effect flag (0x02) + speed; false => static (0x01)
    public bool   HasColor { get; }   // honours the chosen colour
    public bool   HasSpeed { get; }

    public RgbEffect(string name, byte modeByte, bool isEffect, bool hasColor, bool hasSpeed)
    {
        Name = name; ModeByte = modeByte; IsEffect = isEffect; HasColor = hasColor; HasSpeed = hasSpeed;
    }

    public override string ToString() => Name;
}

public static class RgbEffects
{
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
    {
        new("Static",    STATIC,    isEffect: false, hasColor: true,  hasSpeed: false),
        new("Breathing", BREATHING, isEffect: true,  hasColor: true,  hasSpeed: true),
        new("Neon",      NEON,      isEffect: true,  hasColor: false, hasSpeed: true),
        new("Wave",      WAVE,      isEffect: true,  hasColor: false, hasSpeed: true),
        new("Shifting",  SHIFTING,  isEffect: true,  hasColor: false, hasSpeed: true),
        new("Zoom",      ZOOM,      isEffect: true,  hasColor: false, hasSpeed: true),
        new("Meteor",    METEOR,    isEffect: true,  hasColor: false, hasSpeed: true),
        new("Twinkling", TWINKLING, isEffect: true,  hasColor: false, hasSpeed: true),
    };

    // Lightbar (single zone): Static + Breathing honour colour, Neon cycles. Spatial effects do nothing.
    public static readonly RgbEffect[] Lightbar =
    {
        new("Static",    STATIC,    isEffect: false, hasColor: true,  hasSpeed: false),
        new("Breathing", BREATHING, isEffect: true,  hasColor: true,  hasSpeed: true),
        new("Neon",      NEON,      isEffect: true,  hasColor: false, hasSpeed: true),
    };
}
