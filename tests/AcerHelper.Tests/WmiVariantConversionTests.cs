using System.Runtime.InteropServices;
using AcerHelper.Infrastructure.Vendors.Generic;

namespace AcerHelper.Tests;

/// <summary>
/// VARIANT -> ulong marshalling (<c>WmiObject.ToU64</c>) and BSTR -> ulong (<c>WmiObject.ParseBstr</c>).
///
/// Silent-failure territory: a mis-read VARIANT does not throw. It yields a plausible-looking number that
/// then flows into a fan duty, a battery percentage or a firmware field. The two shapes that matter here are
///   * a SIGNED VARIANT must sign-extend (a sensor reporting -1 must not become 4294967295), and
///   * an UNSUPPORTED VARTYPE must not be mistaken for a reading of zero,
/// and the code makes both choices explicitly, so they are assertable.
/// </summary>
public class WmiVariantConversionTests
{
    private static Variant Vt(ushort vt)
    {
        var v = default(Variant);
        v.vt = vt;
        return v;
    }

    private static ulong ToU64(Variant v) => WmiObject.ToU64(v);

    // ================= VARTYPE dispatch =================

    [Fact]
    public void EmptyVariant_IsZero()
    {
        Assert.Equal(0UL, ToU64(default));                       // vt == 0 == VT_EMPTY
        Assert.Equal(0UL, ToU64(Vt(Wbem.VT_EMPTY)));
    }

    [Fact]
    public void NullVariant_IsZero()
    {
        Assert.Equal(0UL, ToU64(Vt(Wbem.VT_NULL)));
    }

    [Fact]
    public void BVariant_WithAPayload_IsIgnored()
    {
        // The vt decides, not the union: a VT_EMPTY that happens to carry a stale payload must not leak it.
        var v = Vt(Wbem.VT_EMPTY);
        v.ullVal = ulong.MaxValue;
        Assert.Equal(0UL, ToU64(v));
    }

    [Theory]
    [InlineData(4)]       // VT_R4
    [InlineData(5)]       // VT_R8
    [InlineData(7)]       // VT_DATE
    [InlineData(9)]       // VT_DISPATCH
    [InlineData(0x2000)]  // VT_ARRAY with no element type
    public void UnsupportedVartype_IsZero_NotAPlausibleReading(int vt)
    {
        // WMI really does return VT_R4/VT_R8 for float/double CIM properties, so this is a live shape, not
        // a hypothetical. Documented behaviour: an unsupported type reads as 0 rather than raising.
        var v = Vt((ushort)vt);
        v.ullVal = 0xDEADBEEF;
        Assert.Equal(0UL, ToU64(v));
    }

    // ================= VT_BOOL =================

    [Fact]
    public void Bool_VariantTrue_IsOne()
    {
        // VARIANT_BOOL is a 16-bit field, and VARIANT_TRUE is -1 (0xFFFF), not 1.
        var v = Vt(Wbem.VT_BOOL);
        v.iVal = -1;
        Assert.Equal(1UL, ToU64(v));
    }

    [Fact]
    public void Bool_VariantFalse_IsZero()
    {
        var v = Vt(Wbem.VT_BOOL);
        v.iVal = 0;
        Assert.Equal(0UL, ToU64(v));
    }

    [Theory]
    [InlineData(1, 1UL)]
    [InlineData(2, 1UL)]
    [InlineData(short.MaxValue, 1UL)]
    public void Bool_AnyNonZeroBecomesExactlyOne(short raw, ulong expected)
    {
        var v = Vt(Wbem.VT_BOOL);
        v.iVal = raw;
        Assert.Equal(expected, ToU64(v));
    }

    // ================= signed narrow types must SIGN-extend =================

    [Theory]
    [InlineData(0, 0UL)]
    [InlineData(1, 1UL)]
    [InlineData(127, 127UL)]
    [InlineData(-1, 0xFFFFFFFFFFFFFFFFUL)]
    [InlineData(-2, 0xFFFFFFFFFFFFFFFEUL)]
    [InlineData(-128, 0xFFFFFFFFFFFFFF80UL)]
    public void SignedByte_SignExtendsTo64Bits(sbyte raw, ulong expected)
    {
        var v = Vt(Wbem.VT_I1);
        v.cVal = raw;
        Assert.Equal(expected, ToU64(v));
    }

    [Theory]
    [InlineData(0, 0UL)]
    [InlineData(255, 255UL)]
    public void UnsignedByte_ZeroExtends(byte raw, ulong expected)
    {
        var v = Vt(Wbem.VT_UI1);
        v.bVal = raw;
        Assert.Equal(expected, ToU64(v));
    }

    [Theory]
    [InlineData(0, 0UL)]
    [InlineData(32767, 32767UL)]
    [InlineData(-1, 0xFFFFFFFFFFFFFFFFUL)]
    [InlineData(-32768, 0xFFFFFFFFFFFF8000UL)]
    public void SignedShort_SignExtendsTo64Bits(short raw, ulong expected)
    {
        var v = Vt(Wbem.VT_I2);
        v.iVal = raw;
        Assert.Equal(expected, ToU64(v));
    }

    [Theory]
    [InlineData(0, 0UL)]
    [InlineData(65535, 65535UL)]
    [InlineData(32768, 32768UL)]
    public void UnsignedShort_ZeroExtends(ushort raw, ulong expected)
    {
        var v = Vt(Wbem.VT_UI2);
        v.uiVal = raw;
        Assert.Equal(expected, ToU64(v));
    }

    // ================= the 32-bit edge =================

    [Theory]
    [InlineData(0, 0UL)]
    [InlineData(int.MaxValue, 2147483647UL)]
    [InlineData(-1, 0xFFFFFFFFFFFFFFFFUL)]
    [InlineData(int.MinValue, 0xFFFFFFFF80000000UL)]
    public void SignedLong_SignExtendsTo64Bits(int raw, ulong expected)
    {
        var v = Vt(Wbem.VT_I4);
        v.lVal = raw;
        Assert.Equal(expected, ToU64(v));
    }

    [Theory]
    [InlineData(0, 0UL)]
    [InlineData(int.MaxValue, 2147483647UL)]
    [InlineData(int.MinValue, 0xFFFFFFFF80000000UL)]
    public void VariantInt_SignExtendsLikeI4(int raw, ulong expected)
    {
        // VT_INT is the other signed 32-bit VARTYPE and the switch folds it in with VT_I4.
        var v = Vt(Wbem.VT_INT);
        v.lVal = raw;
        Assert.Equal(expected, ToU64(v));
    }

    [Theory]
    [InlineData(0u, 0UL)]
    [InlineData(4294967295u, 4294967295UL)]
    [InlineData(2147483648u, 2147483648UL)]      // the value a sign-extension bug would turn into 0xFFFFFFFF80000000
    [InlineData(2147483647u, 2147483647UL)]
    public void UnsignedLong_ZeroExtends(uint raw, ulong expected)
    {
        var v = Vt(Wbem.VT_UI4);
        v.ulVal = raw;
        Assert.Equal(expected, ToU64(v));
    }

    [Fact]
    public void VariantUint_ZeroExtendsLikeUi4()
    {
        var v = Vt(Wbem.VT_UINT);
        v.ulVal = 2147483648u;
        Assert.Equal(2147483648UL, ToU64(v));
    }

    // ================= the 64-bit edge =================

    [Fact]
    public void SignedLongLong_SignExtendsTo64Bits()
    {
        var v = Vt(Wbem.VT_I8);
        v.llVal = -1;
        Assert.Equal(0xFFFFFFFFFFFFFFFFUL, ToU64(v));
    }

    [Fact]
    public void SignedLongLong_MinValue_IsTheSignBitAlone()
    {
        var v = Vt(Wbem.VT_I8);
        v.llVal = long.MinValue;
        Assert.Equal(0x8000000000000000UL, ToU64(v));
    }

    [Fact]
    public void SignedLongLong_MaxValue_RoundTrips()
    {
        var v = Vt(Wbem.VT_I8);
        v.llVal = long.MaxValue;
        Assert.Equal(9223372036854775807UL, ToU64(v));
    }

    [Fact]
    public void UnsignedLongLong_RoundTrips_WithoutSignExtension()
    {
        var v = Vt(Wbem.VT_UI8);
        v.ullVal = ulong.MaxValue;
        Assert.Equal(ulong.MaxValue, ToU64(v));
    }

    [Fact]
    public void UnsignedLongLong_HighBitSet_IsNotTreatedAsNegative()
    {
        var v = Vt(Wbem.VT_UI8);
        v.ullVal = 0xFFFFFFFF00000000UL;
        Assert.Equal(0xFFFFFFFF00000000UL, ToU64(v));
    }

    // ---- the narrowing accessors built on GetU64: a signed reading keeps its two's-complement byte ----

    [Fact]
    public void GetByteOfASignedMinusOne_Is0xFF()
    {
        var v = Vt(Wbem.VT_I1);
        v.cVal = -1;
        Assert.Equal(0xFF, (byte)ToU64(v));
    }

    [Fact]
    public void GetIntOfAnUnsigned32BitEdge_IsMinValue()
    {
        // A VT_UI4 of 2^31 narrows to a negative int — WMI never sends this for the properties we read, but
        // the narrowing is unchecked and silent, so it is worth pinning.
        var v = Vt(Wbem.VT_UI4);
        v.ulVal = 2147483648u;
        Assert.Equal(int.MinValue, (int)ToU64(v));
    }

    // ================= ParseBstr: 64-bit CIM integers arrive as decimal strings =================

    private static T WithBstr<T>(string s, Func<nint, T> f)
    {
        var bstr = Wbem.SysAllocString(s);
        try { return f(bstr); }
        finally { Wbem.SysFreeString(bstr); }
    }

    private static ulong Parse(string s) => WithBstr(s, WmiObject.ParseBstr);

    [Fact]
    public void ParseBstr_NullPointer_IsZero() => Assert.Equal(0UL, WmiObject.ParseBstr(0));

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("abc")]
    [InlineData("12abc")]
    [InlineData("0x1F")]
    [InlineData("1.5")]
    [InlineData("--1")]
    public void ParseBstr_Unparseable_IsZero(string s) => Assert.Equal(0UL, Parse(s));

    [Theory]
    [InlineData("1", 1UL)]
    [InlineData("42", 42UL)]
    [InlineData("12345", 12345UL)]
    [InlineData("4294967295", 4294967295UL)]           // uint.MaxValue
    public void ParseBstr_PlainDecimalValue(string s, ulong expected) => Assert.Equal(expected, Parse(s));

    [Theory]
    [InlineData(" 42 ", 42UL)]     // NumberStyles.Integer allows surrounding whitespace
    [InlineData("\t42\n", 42UL)]
    [InlineData("+42", 42UL)]      // ...and a leading '+' on an unsigned parse
    [InlineData("-0", 0UL)]
    public void ParseBstr_PermissiveButDeterministic(string s, ulong expected) => Assert.Equal(expected, Parse(s));

    [Fact]
    public void ParseBstr_AtThe32To64BitBoundary()
    {
        // 2^32 is the first value that does not fit in 32 bits. If the BSTR path were ever swapped for a
        // VT_UI4 read, this is the value that would come back wrong.
        Assert.Equal(4294967296UL, Parse("4294967296"));
        Assert.Equal(4294967297UL, Parse("4294967297"));
    }

    [Fact]
    public void ParseBstr_UlongMaxValue_RoundTrips()
    {
        Assert.Equal(ulong.MaxValue, Parse("18446744073709551615"));
    }

    [Fact]
    public void ParseBstr_LongMaxValue_RoundTrips()
    {
        Assert.Equal(9223372036854775807UL, Parse("9223372036854775807"));
    }

    /// <summary>Signed 64-bit CIM values (CIM_SINT64, e.g. a signed firmware counter) arrive as decimal
    /// strings with a leading '-'. <c>ulong.TryParse</c> rejects them and the code falls back to
    /// <c>long.TryParse</c> and reinterprets — so the reading is a two's-complement ulong, not zero. The
    /// source comment states this intent explicitly ("negative sint64 -> reinterpret").</summary>
    [Theory]
    [InlineData("-1", 0xFFFFFFFFFFFFFFFFUL)]
    [InlineData("-2", 0xFFFFFFFFFFFFFFFEUL)]
    [InlineData("-9223372036854775808", 0x8000000000000000UL)]   // long.MinValue
    [InlineData("-100", 0xFFFFFFFFFFFFFF9CUL)]
    public void ParseBstr_NegativeSInt64_IsReinterpreted_NotRejected(string s, ulong expected) =>
        Assert.Equal(expected, Parse(s));

    /// <summary>Out of range for BOTH parses, so it lands on the `0` default — a silent zero for a value
    /// that is merely absurd rather than malformed. Documented, not asserted as desirable; if a future
    /// change makes this throw or saturate instead, this test is the place that will say so.</summary>
    [Theory]
    [InlineData("18446744073709551616")]    // ulong.MaxValue + 1
    [InlineData("-9223372036854775809")]    // long.MinValue - 1
    [InlineData("99999999999999999999999999")]
    public void ParseBstr_OutOfRangeForBothParses_SilentlyBecomesZero(string s) =>
        Assert.Equal(0UL, Parse(s));

    // ================= ParseBstr reached through the VT_BSTR arm of ToU64 =================

    [Fact]
    public void ToU64OfABstrVariant_ParsesTheString()
    {
        WithBstr("12345", bstr =>
        {
            var v = Vt(Wbem.VT_BSTR);
            v.ptrVal = bstr;
            Assert.Equal(12345UL, ToU64(v));
            return 0;
        });
    }

    [Fact]
    public void ToU64OfAnEmptyBstrVariant_IsZero()
    {
        WithBstr("", bstr =>
        {
            var v = Vt(Wbem.VT_BSTR);
            v.ptrVal = bstr;
            Assert.Equal(0UL, ToU64(v));
            return 0;
        });
    }

    [Fact]
    public void ToU64OfABstrVariantWithANullPointer_IsZero()
    {
        // WMI leaves a property unset rather than failing the whole Get, so a null BSTR is a live shape.
        var v = Vt(Wbem.VT_BSTR);
        v.ptrVal = 0;
        Assert.Equal(0UL, ToU64(v));
    }

    [Fact]
    public void ToU64OfABstrVariant_WithA64BitDecimalString()
    {
        WithBstr("18446744073709551615", bstr =>
        {
            var v = Vt(Wbem.VT_BSTR);
            v.ptrVal = bstr;
            Assert.Equal(ulong.MaxValue, ToU64(v));
            return 0;
        });
    }

    // ================= the shape the union actually occupies =================

    [Fact]
    public void VariantIsTwentyFourBytes_AndEveryUnionAccessorSharesOffset8()
    {
        // The callee writes a whole VARIANT into this buffer, so an undersized struct would be a silent
        // stack corruption rather than a wrong number.
        Assert.Equal(24, Marshal.SizeOf<Variant>());
        Assert.Equal(0, (int)Marshal.OffsetOf<Variant>(nameof(Variant.vt)));
        foreach (var name in new[]
                 {
                     nameof(Variant.ptrVal), nameof(Variant.llVal), nameof(Variant.ullVal), nameof(Variant.lVal),
                     nameof(Variant.ulVal), nameof(Variant.iVal), nameof(Variant.uiVal), nameof(Variant.cVal),
                     nameof(Variant.bVal),
                 })
            Assert.Equal(8, (int)Marshal.OffsetOf<Variant>(name));
    }

    [Fact]
    public void TheUnionAccessorsOverlap()
    {
        var v = Vt(Wbem.VT_I4);
        v.lVal = -1;

        // Writing through the 32-bit accessor fills only the low 4 bytes of the 16-byte union; the rest
        // stays as the struct was constructed. This is the reason the ToU64 switch must read the accessor
        // that MATCHES vt rather than whichever one happens to hold the widest field: for a real VARIANT the
        // callee writes the union, and the bytes outside the value are not the value.
        Assert.Equal(0x00000000FFFFFFFFUL, v.ullVal);
        Assert.Equal((short)-1, v.iVal);
        Assert.Equal((byte)0xFF, v.bVal);
    }

    [Fact]
    public void WritingTheWideAccessorIsVisibleThroughTheNarrowOnes()
    {
        var v = Vt(Wbem.VT_UI8);
        v.ullVal = ulong.MaxValue;
        Assert.Equal(-1, v.lVal);
        Assert.Equal((short)-1, v.iVal);
        Assert.Equal((byte)0xFF, v.bVal);
    }

    /// <summary>Every VARTYPE the switch handles must be reachable through the 8-byte union at offset 8 —
    /// a mistyped FieldOffset would make one of the arms read a different value's bytes.</summary>
    [Fact]
    public void EachWideAccessorRoundTripsThroughItsOwnArm()
    {
        var i8 = Vt(Wbem.VT_I8);
        i8.llVal = 0x0102030405060708L;
        Assert.Equal(0x0102030405060708UL, ToU64(i8));

        var ui8 = Vt(Wbem.VT_UI8);
        ui8.ullVal = 0x0807060504030201UL;
        Assert.Equal(0x0807060504030201UL, ToU64(ui8));
    }
}
